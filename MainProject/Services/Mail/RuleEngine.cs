using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>Move requests a rule pass produced; the caller (sync service) executes them where it can.</summary>
    public sealed class RuleProcessResult
    {
        /// <summary>(uid, target server folder) — executed over the open IMAP connection.</summary>
        public readonly List<(uint Uid, string TargetFolder)> RemoteMoves = [];

        /// <summary>(uid, local folder name) — executed against <see cref="Storage.MessageStore"/>.</summary>
        public readonly List<(uint Uid, string LocalFolder)> LocalMoves = [];
    }

    /// <summary>
    /// Evaluates <see cref="FilterRuleStore.Rules"/> against incoming message summaries. Flag,
    /// tag and mute effects mutate the summaries in place (before they are stored); move requests
    /// are returned for the caller to execute. Notification decisions (<see cref="ShouldNotify"/>,
    /// <see cref="GetNotificationSound"/>) are answered from transient per-arrival decisions.
    /// </summary>
    public static class RuleEngine
    {
        // Summary identity isolates concurrent accounts even when Message-Id is absent or duplicated.
        sealed class NotificationDecision { public bool? Notify; public int Priority; public string? Sound; }
        static readonly ConditionalWeakTable<MailMessageSummary, NotificationDecision> notificationDecisions = new();

        /// <summary>Explicit rule choice; null inherits account/folder notification preferences.</summary>
        public static bool? GetNotificationOverride(MailMessageSummary summary) =>
            notificationDecisions.TryGetValue(summary, out var decision) ? decision.Notify : null;

        public static int GetNotificationPriority(MailMessageSummary summary) =>
            notificationDecisions.TryGetValue(summary, out var decision) ? decision.Priority : 0;

        /// <summary>Sender > account > mailbox > nested account container > application.</summary>
        public static int Priority(FilterRule rule)
        {
            if (rule.SenderAddress.Length > 0) return 400;
            if (rule.FolderFullName.Length > 0) return 250;
            if (rule.AccountFolderId.Length > 0)
            {
                int depth = 0;
                string? current = rule.AccountFolderId;
                HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
                while (current != null && visited.Add(current))
                {
                    var folder = AccountFolderStore.Folders.FirstOrDefault(f => f.Id.Equals(current, StringComparison.OrdinalIgnoreCase));
                    if (folder == null) break;
                    depth++;
                    current = folder.ParentFolderId;
                }
                return 200 + Math.Min(depth, 49);
            }
            return rule.AccountId.Length > 0 ? 300 : 100;
        }

        public static bool AppliesToScope(FilterRule rule, MailAccountData account, string folderFullName, MailMessageSummary? summary = null)
        {
            if (rule.AccountId.Length > 0 && rule.AccountId != account.Id) return false;
            if (rule.FolderFullName.Length > 0 && (rule.AccountId.Length == 0 || rule.FolderFullName != folderFullName)) return false;
            if (rule.SenderAddress.Length > 0 && summary != null &&
                !rule.SenderAddress.Trim().Equals(summary.FromAddress.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            if (rule.AccountFolderId.Length == 0) return true;
            string? current = AccountFolderStore.NormalizeFolderId(account.FolderId);
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
            while (current != null && visited.Add(current))
            {
                var folder = AccountFolderStore.Folders.FirstOrDefault(f => f.Id.Equals(current, StringComparison.OrdinalIgnoreCase));
                if (folder == null) return false;
                if (folder.Id.Equals(rule.AccountFolderId, StringComparison.OrdinalIgnoreCase)) return true;
                current = folder.ParentFolderId;
            }
            return false;
        }

        /// <summary>Runs every enabled matching rule over the new summaries. Mutates flags/tags in place.</summary>
        public static RuleProcessResult ProcessIncoming(MailAccountData account, string folderFullName, List<MailMessageSummary> newSummaries)
        {
            var result = new RuleProcessResult();

            var orderedRules = FilterRuleStore.Rules.Where(r => r.Enabled).OrderByDescending(Priority).ToArray();
            foreach (var summary in newSummaries)
            {
                notificationDecisions.Remove(summary);
                HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);
                foreach (var rule in orderedRules)
                {
                    if (!AppliesToScope(rule, account, folderFullName, summary)) continue;
                    if (!Matches(rule, summary)) continue;

                    ApplyActions(account, folderFullName, rule, summary, result, claimed);
                    if (rule.StopProcessing)
                    {
                        // Recorded too: "why did my second rule not run" is the same question.
                        Audit(account, folderFullName, rule, summary, "StopProcessing", string.Empty);
                        break;
                    }
                }
            }
            return result;
        }

        /// <summary>How one condition of a previewed rule fares against the mail already on disk.</summary>
        public sealed record ConditionHits(int Index, string Description, int Hits, bool OperatorIgnored);

        /// <summary>What a rule would do if it were armed, measured against the cache.</summary>
        public sealed record RulePreview(int Scanned, int Matched, List<ConditionHits> PerCondition, List<string> SampleSubjects, bool ScanLimitReached = false);

        /// <summary>Maximum messages scanned per preview, so a large cache cannot stall the caller.</summary>
        const int PreviewScanCap = 5000;

        /// <summary>Subjects shown as proof of what matched.</summary>
        const int PreviewSampleSize = 10;

        /// <summary>
        /// Answers "what would this rule have done" against the summaries already on disk, WITHOUT
        /// applying anything. The editor could only Save, so a rule that matches nothing and one
        /// that matches everything looked identical until real mail arrived — by which time a
        /// move-to-folder action has already run on the server, which this app cannot undo.
        /// <para>
        /// Strictly read-only: <see cref="MessageStore.GetSummaries"/> hands back the STORED
        /// instances, so calling ApplyActions here would really tag, flag and mark-read the user's
        /// cache. Actions are only ever described, never run.
        /// </para>
        /// </summary>
        public static RulePreview Preview(FilterRule rule)
        {
            List<ConditionHits> perCondition = [.. rule.Conditions.Select((condition, index) =>
                new ConditionHits(index + 1, Describe(condition), 0, IgnoresOperator(condition)))];
            int scanned = 0, matched = 0;
            bool limitReached = false;
            List<string> sample = [];

            foreach (var account in AccountStore.Accounts)
            {
                // Same account gate as ProcessIncoming: an empty AccountId means every account.
                if (rule.AccountId.Length > 0 && rule.AccountId != account.Id) continue;

                foreach (var folder in MessageStore.GetFolders(account.Id))
                {
                    if (!AppliesToScope(rule, account, folder.FullName)) continue;
                    foreach (var summary in MessageStore.GetSummaries(account.Id, folder.FullName))
                    {
                        if (scanned >= PreviewScanCap) { limitReached = true; goto done; }
                        scanned++;

                        for (int i = 0; i < rule.Conditions.Count; i++)
                        {
                            if (MatchesCondition(rule.Conditions[i], summary))
                                perCondition[i] = perCondition[i] with { Hits = perCondition[i].Hits + 1 };
                        }

                        if (!AppliesToScope(rule, account, folder.FullName, summary) || !Matches(rule, summary)) continue;
                        matched++;
                        if (sample.Count < PreviewSampleSize) sample.Add(summary.Subject);
                    }
                }
            }
        done:
            return new RulePreview(scanned, matched, perCondition, sample, limitReached);
        }

        /// <summary>
        /// True when the engine will not honour the chosen operator for this field.
        /// </summary>
        static bool IgnoresOperator(FilterCondition condition) => condition.Field switch
        {
            FilterField.HasAttachment or FilterField.IsUnread or FilterField.IsStarred
                or FilterField.IsImportant or FilterField.IsMuted => condition.Operator != FilterOperator.Equals,
            FilterField.ReceivedDate => condition.Operator is not (FilterOperator.Equals or FilterOperator.LessThan or FilterOperator.GreaterThan),
            FilterField.SizeKb => condition.Operator is not (FilterOperator.Equals or FilterOperator.LessThan or FilterOperator.GreaterThan),
            _ => condition.Operator is FilterOperator.GreaterThan or FilterOperator.LessThan
        };

        static string Describe(FilterCondition condition) => $"{condition.Field} {condition.Operator} \"{condition.Value}\"";

        /// <summary>True when the rule's conditions match under its AND/OR mode (a rule without conditions never matches).</summary>
        static bool Matches(FilterRule rule, MailMessageSummary summary)
        {
            if (rule.Conditions.Count == 0) return rule.SenderAddress.Length > 0 || rule.AccountId.Length > 0 || rule.AccountFolderId.Length > 0;
            return rule.MatchMode == FilterMatchMode.All
                ? rule.Conditions.All(c => MatchesCondition(c, summary))
                : rule.Conditions.Any(c => MatchesCondition(c, summary));
        }

        public static bool MatchesCondition(FilterCondition condition, MailMessageSummary summary)
        {
            if (condition.Field == FilterField.HasAttachment)
                return summary.HasAttachments == !string.Equals(condition.Value, "false", StringComparison.OrdinalIgnoreCase);

            if (condition.Field == FilterField.IsUnread)
                return summary.IsUnread == !string.Equals(condition.Value, "false", StringComparison.OrdinalIgnoreCase);

            if (condition.Field == FilterField.IsStarred)
                return summary.Flags.HasFlag(MailFlags.Flagged) == !string.Equals(condition.Value, "false", StringComparison.OrdinalIgnoreCase);

            if (condition.Field == FilterField.IsImportant)
                return summary.Flags.HasFlag(MailFlags.Important) == !string.Equals(condition.Value, "false", StringComparison.OrdinalIgnoreCase);

            if (condition.Field == FilterField.IsMuted)
                return summary.Flags.HasFlag(MailFlags.Muted) == !string.Equals(condition.Value, "false", StringComparison.OrdinalIgnoreCase);

            if (condition.Field == FilterField.Tag)
            {
                return condition.Operator switch
                {
                    FilterOperator.Contains => summary.Tags.Any(t => t.Contains(condition.Value, StringComparison.OrdinalIgnoreCase)),
                    FilterOperator.NotContains => !summary.Tags.Any(t => t.Contains(condition.Value, StringComparison.OrdinalIgnoreCase)),
                    FilterOperator.Equals => summary.Tags.Any(t => t.Equals(condition.Value, StringComparison.OrdinalIgnoreCase)),
                    _ => false
                };
            }

            if (condition.Field == FilterField.ReceivedDate)
            {
                if (!FilterFieldInfo.TryReadDate(condition.Value, out var dateUtc)) return true;
                return condition.Operator switch
                {
                    FilterOperator.GreaterThan => summary.DateUtc >= dateUtc,
                    FilterOperator.LessThan => summary.DateUtc < dateUtc,
                    FilterOperator.Equals => summary.DateUtc >= dateUtc && summary.DateUtc < dateUtc.AddDays(1),
                    _ => true
                };
            }

            if (condition.Field == FilterField.SizeKb)
            {
                if (!double.TryParse(condition.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double thresholdKb)) return false;
                double sizeKb = summary.SizeBytes / 1024.0;
                return condition.Operator switch { FilterOperator.LessThan => sizeKb < thresholdKb,
                    FilterOperator.GreaterThan => sizeKb > thresholdKb, FilterOperator.Equals => sizeKb == thresholdKb, _ => false };
            }

            string text = condition.Field switch
            {
                FilterField.From => $"{summary.FromName} {summary.FromAddress}",
                FilterField.FromAddress => summary.FromAddress,
                FilterField.To => summary.ToAddresses,
                FilterField.Subject => summary.Subject,
                FilterField.BodyPreview => summary.PreviewText,
                FilterField.SenderDomain => summary.FromAddress.Contains('@') ? summary.FromAddress[(summary.FromAddress.IndexOf('@') + 1)..] : string.Empty,
                _ => string.Empty
            };

            return condition.Operator switch
            {
                FilterOperator.Contains => text.Contains(condition.Value, StringComparison.OrdinalIgnoreCase),
                FilterOperator.NotContains => !text.Contains(condition.Value, StringComparison.OrdinalIgnoreCase),
                FilterOperator.Equals => text.Equals(condition.Value, StringComparison.OrdinalIgnoreCase),
                FilterOperator.StartsWith => text.StartsWith(condition.Value, StringComparison.OrdinalIgnoreCase),
                FilterOperator.EndsWith => text.EndsWith(condition.Value, StringComparison.OrdinalIgnoreCase),
                FilterOperator.MatchesRegex => SafeRegexMatch(text, condition.Value),
                _ => false
            };
        }

        static bool SafeRegexMatch(string text, string pattern)
        {
            try
            {
                return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
            }
            catch (Exception ex)
            {
                Log($"Rule regex '{pattern}' failed: {ex.Message}", LogLevel.Warning);
                return false;
            }
        }

        static string ActionKey(FilterAction action) => action.Type switch
        {
            FilterActionType.MoveToLocalFolder or FilterActionType.MoveToRemoteFolder => "move",
            FilterActionType.MarkRead or FilterActionType.MarkUnread => "read",
            FilterActionType.MarkImportant or FilterActionType.ClearImportant => "important",
            FilterActionType.Flag or FilterActionType.Unflag => "star",
            FilterActionType.Mute or FilterActionType.Unmute => "notification",
            FilterActionType.AddTag or FilterActionType.RemoveTag => "tag:" + action.Argument,
            _ => action.Type.ToString()
        };

        static void ApplyActions(MailAccountData account, string folderFullName, FilterRule rule, MailMessageSummary summary, RuleProcessResult result, HashSet<string> claimed)
        {
            foreach (var action in rule.Actions)
            {
                if (!claimed.Add(ActionKey(action))) continue;
                // Recorded BEFORE the switch runs: MoveToRemoteFolder only queues the move, and
                // the sync service deletes the source row as soon as it succeeds - after that the
                // uid this was found under no longer exists.
                Audit(account, folderFullName, rule, summary, action.Type.ToString(), action.Argument);
                switch (action.Type)
                {
                    case FilterActionType.MarkRead:
                        summary.Flags |= MailFlags.Seen;
                        break;
                    case FilterActionType.MarkUnread: summary.Flags &= ~MailFlags.Seen; break;
                    case FilterActionType.ClearImportant: summary.Flags &= ~MailFlags.Important; break;
                    case FilterActionType.Unflag: summary.Flags &= ~MailFlags.Flagged; break;
                    case FilterActionType.MarkImportant:
                        summary.Flags |= MailFlags.Important;
                        break;
                    case FilterActionType.Flag:
                        summary.Flags |= MailFlags.Flagged;
                        break;
                    case FilterActionType.Mute:
                        summary.Flags |= MailFlags.Muted;
                        var muted = notificationDecisions.GetValue(summary, static _ => new());
                        muted.Notify = false; muted.Priority = Priority(rule);
                        break;
                    case FilterActionType.Unmute:
                        summary.Flags &= ~MailFlags.Muted;
                        var unmuted = notificationDecisions.GetValue(summary, static _ => new());
                        unmuted.Notify = true; unmuted.Priority = Priority(rule);
                        break;
                    case FilterActionType.RemoveTag: summary.Tags.RemoveAll(t => t.Equals(action.Argument, StringComparison.OrdinalIgnoreCase)); break;
                    case FilterActionType.AddTag:
                        if (action.Argument.Length > 0 && !summary.Tags.Contains(action.Argument, StringComparer.OrdinalIgnoreCase))
                            summary.Tags.Add(action.Argument);
                        break;
                    case FilterActionType.SetNotificationSound:
                        notificationDecisions.GetValue(summary, static _ => new()).Sound = action.Argument;
                        break;
                    case FilterActionType.MoveToRemoteFolder:
                        if (action.Argument.Length > 0 && action.Argument != folderFullName)
                            result.RemoteMoves.Add((summary.Uid, action.Argument));
                        break;
                    case FilterActionType.MoveToLocalFolder:
                        if (action.Argument.Length > 0 && MessageStore.LocalFolderPrefix + action.Argument != folderFullName)
                            result.LocalMoves.Add((summary.Uid, action.Argument));
                        break;
                }
            }
        }

        static void Audit(MailAccountData account, string folderFullName, FilterRule rule, MailMessageSummary summary, string action, string argument) =>
            RuleAuditStore.Record(new RuleAuditEntry
            {
                WhenUtc = DateTime.UtcNow,
                AccountId = account.Id,
                FolderFullName = folderFullName,
                Uid = summary.Uid,
                MessageId = summary.MessageId,
                RuleId = rule.Id,
                RuleName = rule.Name,
                Action = action,
                Argument = argument
            });

        /// <summary>False when a rule muted the message (its arrival should stay silent).</summary>
        public static bool ShouldNotify(MailMessageSummary summary) => !summary.Flags.HasFlag(MailFlags.Muted);

        /// <summary>The rule-chosen sound for this message, or null to use the account/global default.</summary>
        public static string? GetNotificationSound(MailMessageSummary summary) =>
            notificationDecisions.TryGetValue(summary, out var decision) ? decision.Sound : null;
    }
}
