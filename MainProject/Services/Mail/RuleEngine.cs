using System.Collections.Concurrent;
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
    /// <see cref="GetNotificationSound"/>) are answered from the stored per-message sound map.
    /// </summary>
    public static class RuleEngine
    {
        /// <summary>Message-id → custom notification sound chosen by a SetNotificationSound action.
        /// Concurrent: IMAP and POP3 sync passes for different accounts can write simultaneously.</summary>
        static readonly ConcurrentDictionary<string, string> customSoundByMessageId = [];

        /// <summary>The sound only matters for the toast fired seconds after arrival, so the map is
        /// simply dropped when it grows past this instead of tracking entry age.</summary>
        const int MaxRememberedSounds = 500;

        /// <summary>Runs every enabled matching rule over the new summaries. Mutates flags/tags in place.</summary>
        public static RuleProcessResult ProcessIncoming(MailAccountData account, string folderFullName, List<MailMessageSummary> newSummaries)
        {
            var result = new RuleProcessResult();

            foreach (var summary in newSummaries)
            {
                foreach (var rule in FilterRuleStore.Rules)
                {
                    if (!rule.Enabled) continue;
                    if (rule.AccountId.Length > 0 && rule.AccountId != account.Id) continue;
                    if (!Matches(rule, summary)) continue;

                    ApplyActions(rule, summary, result);
                    if (rule.StopProcessing) break;
                }
            }
            return result;
        }

        /// <summary>How one condition of a previewed rule fares against the mail already on disk.</summary>
        public sealed record ConditionHits(int Index, string Description, int Hits, bool OperatorIgnored);

        /// <summary>What a rule would do if it were armed, measured against the cache.</summary>
        public sealed record RulePreview(int Scanned, int Matched, List<ConditionHits> PerCondition, List<string> SampleSubjects);

        /// <summary>Newest messages scanned per preview, so a large cache cannot stall the caller.</summary>
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
            List<string> sample = [];

            foreach (var account in AccountStore.Accounts)
            {
                // Same account gate as ProcessIncoming: an empty AccountId means every account.
                if (rule.AccountId.Length > 0 && rule.AccountId != account.Id) continue;

                foreach (var folder in MessageStore.GetFolders(account.Id))
                {
                    foreach (var summary in MessageStore.GetSummaries(account.Id, folder.FullName))
                    {
                        if (scanned >= PreviewScanCap) goto done;
                        scanned++;

                        for (int i = 0; i < rule.Conditions.Count; i++)
                        {
                            if (MatchesCondition(rule.Conditions[i], summary))
                                perCondition[i] = perCondition[i] with { Hits = perCondition[i].Hits + 1 };
                        }

                        if (!Matches(rule, summary)) continue;
                        matched++;
                        if (sample.Count < PreviewSampleSize) sample.Add(summary.Subject);
                    }
                }
            }
        done:
            return new RulePreview(scanned, matched, perCondition, sample);
        }

        /// <summary>
        /// True when the engine will not honour the chosen operator for this field. Three such
        /// combinations exist and all of them fail silently today: HasAttachment never reads the
        /// operator, SizeKb treats everything that is not LessThan as greater-than, and an
        /// ordering operator on a text field falls through to "never matches".
        /// </summary>
        static bool IgnoresOperator(FilterCondition condition) => condition.Field switch
        {
            FilterField.HasAttachment => true,
            FilterField.SizeKb => condition.Operator is not (FilterOperator.LessThan or FilterOperator.GreaterThan),
            _ => condition.Operator is FilterOperator.GreaterThan or FilterOperator.LessThan
        };

        static string Describe(FilterCondition condition) => $"{condition.Field} {condition.Operator} \"{condition.Value}\"";

        /// <summary>True when the rule's conditions match under its AND/OR mode (a rule without conditions never matches).</summary>
        static bool Matches(FilterRule rule, MailMessageSummary summary)
        {
            if (rule.Conditions.Count == 0) return false;
            return rule.MatchMode == FilterMatchMode.All
                ? rule.Conditions.All(c => MatchesCondition(c, summary))
                : rule.Conditions.Any(c => MatchesCondition(c, summary));
        }

        static bool MatchesCondition(FilterCondition condition, MailMessageSummary summary)
        {
            if (condition.Field == FilterField.HasAttachment)
                return summary.HasAttachments == !string.Equals(condition.Value, "false", StringComparison.OrdinalIgnoreCase);

            if (condition.Field == FilterField.SizeKb)
            {
                if (!double.TryParse(condition.Value, out double thresholdKb)) return false;
                double sizeKb = summary.SizeBytes / 1024.0;
                return condition.Operator == FilterOperator.LessThan ? sizeKb < thresholdKb : sizeKb > thresholdKb;
            }

            string text = condition.Field switch
            {
                FilterField.From => $"{summary.FromName} {summary.FromAddress}",
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

        static void ApplyActions(FilterRule rule, MailMessageSummary summary, RuleProcessResult result)
        {
            foreach (var action in rule.Actions)
            {
                switch (action.Type)
                {
                    case FilterActionType.MarkRead:
                        summary.Flags |= MailFlags.Seen;
                        break;
                    case FilterActionType.MarkImportant:
                        summary.Flags |= MailFlags.Important;
                        break;
                    case FilterActionType.Flag:
                        summary.Flags |= MailFlags.Flagged;
                        break;
                    case FilterActionType.Mute:
                        summary.Flags |= MailFlags.Muted;
                        break;
                    case FilterActionType.AddTag:
                        if (action.Argument.Length > 0 && !summary.Tags.Contains(action.Argument))
                            summary.Tags.Add(action.Argument);
                        break;
                    case FilterActionType.SetNotificationSound:
                        if (summary.MessageId.Length > 0)
                        {
                            if (customSoundByMessageId.Count >= MaxRememberedSounds)
                                customSoundByMessageId.Clear();
                            customSoundByMessageId[summary.MessageId] = action.Argument;
                        }
                        break;
                    case FilterActionType.MoveToRemoteFolder:
                        if (action.Argument.Length > 0)
                            result.RemoteMoves.Add((summary.Uid, action.Argument));
                        break;
                    case FilterActionType.MoveToLocalFolder:
                        if (action.Argument.Length > 0)
                            result.LocalMoves.Add((summary.Uid, action.Argument));
                        break;
                }
            }
        }

        /// <summary>False when a rule muted the message (its arrival should stay silent).</summary>
        public static bool ShouldNotify(MailMessageSummary summary) => !summary.Flags.HasFlag(MailFlags.Muted);

        /// <summary>The rule-chosen sound for this message, or null to use the account/global default.</summary>
        public static string? GetNotificationSound(MailMessageSummary summary) =>
            summary.MessageId.Length > 0 && customSoundByMessageId.TryGetValue(summary.MessageId, out var sound) ? sound : null;
    }
}
