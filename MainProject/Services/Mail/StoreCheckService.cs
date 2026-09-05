using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>What kind of damage a cached folder is carrying.</summary>
    public enum StoreDefect
    {
        /// <summary>
        /// A folder directory holding mail but no readable <c>folder.json</c>. Everything reaches
        /// folders through <see cref="MessageStore.GetFolders"/>, which skips a directory whose
        /// info file is missing and swallows one that will not deserialize — so the folder list,
        /// search and the cache trimmer all walk past it while its bodies keep occupying disk.
        /// </summary>
        InvisibleFolder,

        /// <summary>A <c>Messages/&lt;uid&gt;.eml</c> whose uid has no line in <c>index.jsonl</c>: bytes nothing will ever read or trim.</summary>
        OrphanBody,

        /// <summary>A zero-byte <c>.eml</c>. The index claims a cached body; opening it finds nothing.</summary>
        EmptyBody,

        /// <summary>A leftover <c>.tmp</c> sibling from an interrupted <see cref="AtomicFile"/> write. Nothing in the app reads or sweeps these.</summary>
        StrayTemp,

        /// <summary>The folder's stored UnreadCount disagrees with its own rows, in a folder whose cache holds the whole thing.</summary>
        BadgeDrift
    }

    /// <summary>One defect, with the exact path so it can be looked at by hand.</summary>
    public sealed record StoreFinding(StoreDefect Defect, string Path, string Detail, long Bytes = 0);

    /// <summary>
    /// One pass over the cache. Mirrors the shape of the trimmer's report so the two read alike:
    /// when it ran, what it found, keyed by "&lt;email&gt; / &lt;folder&gt;".
    /// </summary>
    public sealed record StoreCheckReport(
        DateTime RanAtUtc,
        int AccountsScanned,
        int FoldersScanned,
        int FindingCount,
        long InvisibleFolderBytes,
        long StrandedFileBytes,
        Dictionary<string, int> CountsByDefect,
        Dictionary<string, List<StoreFinding>> ByFolder);

    /// <summary>
    /// Reads the message cache off disk and reports what does not add up. READ-ONLY by
    /// construction: it opens files, and never writes, moves or deletes one.
    /// <para>
    /// It walks the directories with plain file I/O rather than through
    /// <see cref="MessageStore.GetFolders"/> on purpose — that method skips and collapses the very
    /// defects being hunted, so a scan built on it would report a clean cache no matter what was
    /// on disk. Repair is deliberately NOT here: a delete-capable pass built on a classifier that
    /// has never run once is the wrong order.
    /// </para>
    /// </summary>
    public static class StoreCheckService
    {
        public const string ReportFileName = "store-check.json";

        static string ReportPath => Path.Combine(AppPaths.UserData, ReportFileName);

        static StoreCheckReport? lastReport;

        /// <summary>The most recent scan, read from disk on first access; null before the first ever run.</summary>
        public static StoreCheckReport? LastReport
        {
            get
            {
                if (lastReport != null) return lastReport;
                try
                {
                    if (File.Exists(ReportPath))
                        lastReport = JsonSerializer.Deserialize<StoreCheckReport>(File.ReadAllText(ReportPath), JsonDefaults.Indented);
                }
                catch (Exception ex)
                {
                    Log($"Could not read the last store-check report: {ex.Message}", LogLevel.Warning);
                }
                return lastReport;
            }
        }

        /// <summary>Scans every cached account and folder, writes the report, and returns it.</summary>
        public static StoreCheckReport Run()
        {
            var byFolder = new Dictionary<string, List<StoreFinding>>();
            int accounts = 0, folders = 0;

            string accountsRoot = Path.Combine(AppPaths.UserCache, MessageStore.AccountsFolderName);
            if (Directory.Exists(accountsRoot))
            {
                foreach (string accountDir in SafeDirectories(accountsRoot))
                {
                    accounts++;
                    string accountId = Path.GetFileName(accountDir);
                    string label = AccountStore.GetById(accountId)?.EmailAddress ?? accountId;

                    string foldersRoot = Path.Combine(accountDir, MessageStore.FoldersFolderName);
                    if (!Directory.Exists(foldersRoot)) continue;

                    foreach (string folderDir in SafeDirectories(foldersRoot))
                    {
                        folders++;
                        var findings = new List<StoreFinding>();
                        string folderName = ScanFolder(folderDir, findings);
                        if (findings.Count > 0) byFolder[$"{label} / {folderName}"] = findings;
                    }
                }
            }

            var all = byFolder.Values.SelectMany(static f => f).ToList();
            var report = new StoreCheckReport(
                DateTime.UtcNow,
                accounts,
                folders,
                all.Count,
                all.Where(static f => f.Defect == StoreDefect.InvisibleFolder).Sum(static f => f.Bytes),
                all.Where(static f => f.Defect is StoreDefect.OrphanBody or StoreDefect.EmptyBody or StoreDefect.StrayTemp).Sum(static f => f.Bytes),
                all.GroupBy(static f => f.Defect).ToDictionary(static g => g.Key.ToString(), static g => g.Count()),
                byFolder);

            Persist(report);
            Log($"Store check: {folders} folder(s) in {accounts} account(s), {all.Count} finding(s).");
            return report;
        }

        /// <summary>Scans one folder directory and returns the name to report it under.</summary>
        static string ScanFolder(string folderDir, List<StoreFinding> findings)
        {
            string directoryName = Path.GetFileName(folderDir);
            string infoPath = Path.Combine(folderDir, MessageStore.FolderInfoFileName);
            string indexPath = Path.Combine(folderDir, MessageStore.IndexFileName);
            string messagesDir = Path.Combine(folderDir, MessageStore.MessagesFolderName);

            MailFolderData? info = null;
            string? infoProblem = null;
            try
            {
                if (File.Exists(infoPath))
                {
                    info = JsonSerializer.Deserialize<MailFolderData>(File.ReadAllText(infoPath), JsonDefaults.Indented);
                    if (info == null) infoProblem = "folder.json deserialized to null";
                }
                else
                {
                    infoProblem = "no folder.json";
                }
            }
            catch (Exception ex)
            {
                infoProblem = $"folder.json will not parse: {ex.Message}";
            }

            var bodies = SafeFiles(messagesDir).ToList();

            // Only a directory that actually HOLDS something is worth reporting: an empty leftover
            // directory costs nothing and would drown the real findings.
            if (infoProblem != null && (File.Exists(indexPath) || bodies.Count > 0))
            {
                long bytes = bodies.Sum(SafeLength);
                findings.Add(new StoreFinding(StoreDefect.InvisibleFolder, folderDir,
                    $"{infoProblem}; {bodies.Count} body file(s) here are invisible to the folder list, search and the cache trimmer.", bytes));
            }

            var indexedUids = ReadIndexedUids(indexPath, findings);

            foreach (string body in bodies)
            {
                string fileName = Path.GetFileName(body);
                if (!fileName.EndsWith(MessageStore.MessageExtension, StringComparison.OrdinalIgnoreCase)) continue;

                long length = SafeLength(body);
                if (length == 0)
                    findings.Add(new StoreFinding(StoreDefect.EmptyBody, body, "Zero bytes: the index says this body is cached, opening it finds nothing.", 0));

                // A folder with no readable index has no uid list to be orphaned FROM, and calling
                // every body an orphan there would just restate the invisible-folder finding.
                if (indexedUids == null) continue;

                if (!uint.TryParse(Path.GetFileNameWithoutExtension(fileName), out uint uid))
                    findings.Add(new StoreFinding(StoreDefect.OrphanBody, body, "File name is not a uid, so nothing can ever look it up.", length));
                else if (!indexedUids.Contains(uid))
                    findings.Add(new StoreFinding(StoreDefect.OrphanBody, body, $"uid {uid} has no line in index.jsonl.", length));
            }

            foreach (string temp in SafeFiles(folderDir).Concat(SafeFiles(messagesDir))
                         .Where(static p => p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add(new StoreFinding(StoreDefect.StrayTemp, temp,
                    "Left behind by an interrupted atomic write; nothing in the app reads or sweeps it.", SafeLength(temp)));
            }

            CheckBadgeDrift(info, indexPath, findings);
            return info?.FullName ?? directoryName;
        }

        /// <summary>The uids named by index.jsonl, or null when the folder has no readable index at all.</summary>
        static HashSet<uint>? ReadIndexedUids(string indexPath, List<StoreFinding> findings)
        {
            if (!File.Exists(indexPath)) return null;

            var uids = new HashSet<uint>();
            try
            {
                foreach (string line in File.ReadLines(indexPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<MailMessageSummary>(line, JsonDefaults.SingleLine) is { } summary)
                            uids.Add(summary.Uid);
                    }
                    catch
                    {
                        // A line the loader also skips. It is not a defect class of its own here:
                        // the bodies it should have named surface as orphans, which is the fact
                        // that costs disk.
                    }
                }
            }
            catch (Exception ex)
            {
                findings.Add(new StoreFinding(StoreDefect.InvisibleFolder, indexPath, $"index.jsonl could not be read: {ex.Message}"));
                return null;
            }
            return uids;
        }

        /// <summary>
        /// Reported only when the cache holds the whole folder — the same gate
        /// <c>MessageStore.RefreshUnreadCount</c> uses before it trusts a recount. Below it the
        /// badge is maintained by deltas and a disagreement with the rows is expected, not a defect.
        /// </summary>
        static void CheckBadgeDrift(MailFolderData? info, string indexPath, List<StoreFinding> findings)
        {
            if (info == null || !File.Exists(indexPath)) return;

            int rows = 0, unread = 0;
            try
            {
                foreach (string line in File.ReadLines(indexPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<MailMessageSummary>(line, JsonDefaults.SingleLine) is not { } summary) continue;
                        rows++;
                        if (summary.IsUnread) unread++;
                    }
                    catch { /* Counted by the pass above; a line that will not parse is not a row. */ }
                }
            }
            catch
            {
                return;
            }

            if (rows < info.TotalCount || info.UnreadCount == unread) return;

            findings.Add(new StoreFinding(StoreDefect.BadgeDrift, indexPath,
                $"folder.json says {info.UnreadCount} unread, its {rows} row(s) say {unread}."));
        }

        static IEnumerable<string> SafeDirectories(string path)
        {
            try { return Directory.EnumerateDirectories(path); }
            catch (Exception ex)
            {
                Log($"Store check could not list '{path}': {ex.Message}", LogLevel.Warning);
                return [];
            }
        }

        static IEnumerable<string> SafeFiles(string path)
        {
            try { return Directory.Exists(path) ? Directory.EnumerateFiles(path) : []; }
            catch (Exception ex)
            {
                Log($"Store check could not list '{path}': {ex.Message}", LogLevel.Warning);
                return [];
            }
        }

        static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        static void Persist(StoreCheckReport report)
        {
            lastReport = report;
            try
            {
                AtomicFile.WriteAllText(ReportPath, JsonSerializer.Serialize(report, JsonDefaults.Indented));
            }
            catch (Exception ex)
            {
                Log($"Could not write the store-check report: {ex.Message}", LogLevel.Warning);
            }
        }
    }
}
