using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Services.Tor;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.Constants;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;
using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail.MainProject.ZTests;

public static partial class DebugApi
{
    /// <summary>Handles the folders endpoints using the application's existing services.</summary>
    /// <param name="request">The accepted local request, including its method and body.</param>
    /// <param name="query">Decoded query values shared with the route dispatcher.</param>
    /// <returns>The response object serialized by the listener.</returns>
    static async Task<object?> HandleFoldersAsync(HttpListenerRequest request, NameValueCollection query)
    {
        switch (request.HttpMethod, request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant())
        {

                case ("GET", "/export/folder"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    return MailboxExportService.ExportFolder(RequireAccount(accountId), ReadFolder(query));
                }

                // The server's list, not the cache's. "Which folders is the app missing" cannot be
                // answered from one side alone, and it turned out the app was missing several.
                case ("GET", "/folders/server"):
                    return new { folders = await ImapSyncService.ListServerFoldersAsync(RequireAccount(RequireQueryValue(query, "accountId"))) };

                // Every folder with the role it ended up holding, plus any role held twice — which
                // is the shape of the bug this endpoint exists to make impossible to miss.
                case ("GET", "/folders/roles"):
                {
                    string rolesAccountId = RequireQueryValue(query, "accountId");
                    var folders = MessageStore.GetFolders(rolesAccountId);
                    return new
                    {
                        folders = folders.Select(f => new
                        {
                            f.FullName,
                            f.DisplayName,
                            Role = f.Role.ToString(),
                            Direction = FolderSyncPolicy.For(rolesAccountId, f.FullName).ToString(),
                            f.Selectable,
                            f.IsLocal,
                            f.TotalCount
                        }),
                        duplicateRoles = folders
                            .Where(static f => f.Role != FolderRole.None)
                            .GroupBy(static f => f.Role)
                            .Where(static g => g.Count() > 1)
                            .Select(static g => new { Role = g.Key.ToString(), Folders = g.Select(static f => f.FullName) })
                    };
                }

                // The resolver over handmade folders: every language, every conflict and every
                // tie-break is provable here with no mail server at all.
                case ("POST", "/folders/roles/probe"):
                {
                    var candidates = await JsonSerializer.DeserializeAsync<List<FolderRoleCandidate>>(
                        request.InputStream, JsonDefaults.SingleLine) ?? [];
                    return new { decisions = FolderRoleResolver.Resolve(candidates).Select(static d => new { d.FullName, Role = d.Role.ToString(), Evidence = d.Evidence.ToString(), d.Why }) };
                }

                // Which of the empty-folder reasons a folder lands on, and the action offered for
                // it. The branch depends on cache state that a screenshot cannot show — a paused
                // account, a folder never fetched, a server count with nothing behind it.
                case ("GET", "/folder-why"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folderFullName = ReadFolder(query);
                    // ?probe=true builds the folder from the query instead of the cache. Several
                    // branches depend on state that is a nuisance to arrange for real — a server
                    // count with nothing behind it, a folder never fetched — and the whole point
                    // of Explain is that it is a pure function of exactly these fields.
                    var folder = query["probe"] == "true"
                        ? new MailFolderData
                        {
                            AccountId = accountId,
                            FullName = folderFullName,
                            DisplayName = folderFullName,
                            IsLocal = query["isLocal"] == "true",
                            TotalCount = int.TryParse(query["totalCount"], out int probeTotal) ? probeTotal : 0,
                            LastSyncedUtc = DateTime.TryParse(query["lastSynced"], out var probeSynced) ? probeSynced.ToUniversalTime() : null
                        }
                        : MessageStore.GetFolders(accountId).FirstOrDefault(f => f.FullName == folderFullName)
                          ?? throw new InvalidOperationException("Unknown folder.");
                    var why = FolderEmptyReason.Explain(folder);
                    return new
                    {
                        folder = folder.FullName,
                        why.Headline,
                        why.Detail,
                        action = why.Action.ToString(),
                        why.ActionLabel,
                        folder.TotalCount,
                        folder.LastSyncedUtc,
                        cached = MessageStore.GetSummaries(accountId, folderFullName).Count,
                        lastFolderFailure = ImapSyncService.LastFolderFailure(accountId, folderFullName)
                    };
                }

                // Runs whatever /folder-why offered, so the button's effect is checkable without
                // a click on the user's screen.
                case ("POST", "/folder-why/act"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folderFullName = ReadFolder(query);
                    var folder = MessageStore.GetFolders(accountId).FirstOrDefault(f => f.FullName == folderFullName)
                        ?? throw new InvalidOperationException("Unknown folder.");
                    var action = Enum.Parse<EmptyFolderAction>(query["action"] ?? FolderEmptyReason.Explain(folder).Action.ToString(), ignoreCase: true);
                    FolderEmptyReason.RunAction(folder, action);
                    return new { ran = action.ToString(), syncing = ImapSyncService.IsFolderSyncing(accountId, folderFullName) };
                }

                case ("GET", "/folder-tree"):
                {
                    List<MailFolderData> folders = query["probe"] is { Length: > 0 } probe
                        ? [.. probe.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(name => new MailFolderData { FullName = name, DisplayName = name.Split('/').Last(), Delimiter = '/', IsLocal = name.StartsWith(MessageStore.LocalFolderPrefix) })]
                        : MessageStore.GetFolders(RequireQueryValue(query, "accountId"));
                    return FolderTreeService.Build(folders).Select(n => new { n.Folder.FullName, n.Folder.DisplayName, n.Depth });
                }

                case ("GET", "/folders"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    return MessageStore.GetFolders(accountId)
                        .Select(f => new
                        {
                            f.FullName, f.DisplayName, f.Role, f.TotalCount, f.UnreadCount, f.IsLocal,
                            f.LastSeenUid, f.OldestFetchedUid, f.LastSyncedUtc, f.Delimiter,
                            CachedCount = MessageStore.GetSummaries(accountId, f.FullName).Count
                        });
                }

                case ("POST", "/folder/read-all"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    return new { marked = MessageActions.SetFolderRead(RequireAccount(accountId), folder) };
                }

                // Awaited, unlike the UI's fire-and-forget kick, so a test can read the count back.
                case ("POST", "/backfill"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    int landed = await ImapSyncService.BackfillFolderAsync(RequireAccount(accountId), folder, ReadTake(query, 300));
                    return new { landed };
                }

                case ("POST", "/resync"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    ImapSyncService.KickFolderResync(RequireAccount(accountId), folder);
                    return new { ok = true };
                }

            default:
                throw new InvalidOperationException($"Unknown endpoint: {request.HttpMethod} {request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant()}");
        }
    }
}