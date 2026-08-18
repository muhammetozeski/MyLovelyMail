using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MyLovelyMail.MainProject.DataModels.Mail;
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
