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

namespace MyLovelyMail.MainProject.Diagnostics;

public static partial class DebugApi
{
    /// <summary>Handles the rules endpoints using the application's existing services.</summary>
    /// <param name="request">The accepted local request, including its method and body.</param>
    /// <param name="query">Decoded query values shared with the route dispatcher.</param>
    /// <returns>The response object serialized by the listener.</returns>
    static object? HandleRules(HttpListenerRequest request, NameValueCollection query)
    {
        switch (request.HttpMethod, request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant())
        {

                // ?probe= runs the builder over handmade paths, so the nesting rules are checkable
                // without a server that happens to have nested folders.
                case ("GET", "/rule-audit"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    var summary = MessageStore.GetSummary(accountId, folder, uid);
                    return new { entries = RuleAuditStore.For(accountId, folder, uid, summary?.MessageId ?? string.Empty) };
                }

                // Feeds a synthetic arrival straight to the rule engine, so the trace is checkable
                // without waiting for mail that happens to match a rule.
                case ("POST", "/rule-audit/probe"):
                {
                    var account = RequireAccount(RequireQueryValue(query, "accountId"));
                    var probe = new MailMessageSummary
                    {
                        Uid = uint.MaxValue,
                        MessageId = query["messageId"] ?? "<rule-probe@mylovelymail.invalid>",
                        FromAddress = query["from"] ?? "probe@example.invalid",
                        Subject = query["subject"] ?? string.Empty,
                        DateUtc = DateTime.UtcNow
                    };
                    RuleEngine.ProcessIncoming(account, ReadFolder(query), [probe]);
                    return new { flags = probe.Flags.ToString(), probe.Tags, entries = RuleAuditStore.For(account.Id, ReadFolder(query), probe.Uid, probe.MessageId) };
                }

                case ("GET", "/rule-preview"):
                {
                    string ruleId = RequireQueryValue(query, "ruleId");
                    var rule = FilterRuleStore.Rules.FirstOrDefault(r => r.Id == ruleId)
                        ?? throw new InvalidOperationException("Unknown rule.");
                    return RuleEngine.Preview(rule);
                }

                case ("POST", "/mute"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    var account = RequireAccount(accountId);
                    var summary = RequireSummary(accountId, folder, uid);
                    int touched = query["mute"] == "false"
                        ? MuteService.UnmuteThread(account, folder, summary)
                        : MuteService.MuteThread(account, folder, summary);
                    return new { touched, mutedThreadIds = MutedThreadStore.All.Count };
                }

                case ("GET", "/muted"):
                    return new { messageIds = MutedThreadStore.All };

                // Hands ApplyToIncoming a summary that only claims to be a reply, which is the one
                // way to prove the future-arrival half without waiting on real mail. A resync
                // proves nothing here: Muted survives that through CarryOverLocalState anyway.
                case ("POST", "/mute/incoming-probe"):
                {
                    var probe = new MailMessageSummary
                    {
                        Uid = uint.MaxValue,
                        MessageId = query["messageId"] ?? "<probe@mylovelymail.invalid>",
                        InReplyTo = RequireQueryValue(query, "inReplyTo")
                    };
                    bool muted = MuteService.ApplyToIncoming(probe);
                    return new { muted, flags = probe.Flags.ToString(), remembered = MutedThreadStore.All.Contains(probe.MessageId) };
                }

                case ("POST", "/snooze"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    int minutes = int.TryParse(query["minutes"], out int parsedMinutes) ? parsedMinutes : 60;
                    SnoozeService.Snooze(RequireAccount(accountId), folder, RequireSummary(accountId, folder, uid),
                        DateTime.UtcNow.AddMinutes(minutes));
                    return new { ok = true, dueUtc = DateTime.UtcNow.AddMinutes(minutes) };
                }

                case ("POST", "/snooze/wake"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    SnoozeService.Wake(RequireAccount(accountId), folder, RequireSummary(accountId, folder, uid));
                    return new { ok = true };
                }

                case ("GET", "/snoozed"):
                    return SnoozeService.Snoozed(RequireQueryValue(query, "accountId"))
                        .Select(entry => new { folder = entry.FolderFullName, entry.Summary.Uid, entry.Summary.Subject, entry.Summary.SnoozedUntilUtc });

            default:
                throw new InvalidOperationException($"Unknown endpoint: {request.HttpMethod} {request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant()}");
        }
    }
}