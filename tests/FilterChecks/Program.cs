using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.UI.Pages;
using Settings = MyLovelyMail.MainProject.Stores.Settings;

// Check the executable location before initializing any file-backed application service.
string expectedRoot = Path.GetFullPath(@"C:\C\TorAnonymity\FilterChecks") + Path.DirectorySeparatorChar;
if (!Path.GetFullPath(AppContext.BaseDirectory).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Run this check project from its temporary artifacts folder.");
if (!Path.GetFullPath(AppPaths.Root).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Application data must stay inside the check artifacts folder.");

AppPaths.EnsureCreated();
AccountFolderStore.Load();
AccountStore.Load();
FilterRuleStore.Load();
var parent = AccountFolderStore.CreateFolder("Filter checks parent");
var child = AccountFolderStore.CreateFolder("Filter checks child", parent.Id);
var other = AccountFolderStore.CreateFolder("Filter checks unrelated");
var account = new MailAccountData { Id = "filter-check-a", EmailAddress = "a@example.invalid", FolderId = child.Id };
var accountB = new MailAccountData { Id = "filter-check-b", EmailAddress = "b@example.invalid", FolderId = other.Id };
AccountStore.Save(account);
AccountStore.Save(accountB);
foreach (var a in new[] { account, accountB })
    foreach (string name in new[] { "INBOX", "Archive", "Other", MessageStore.LocalFolderPrefix + "Saved" })
        MessageStore.SaveFolder(new MailFolderData { AccountId = a.Id, FullName = name, DisplayName = name,
            Selectable = true, IsLocal = MessageStore.IsLocalFolder(name), Role = name == "INBOX" ? FolderRole.Inbox : FolderRole.None });

int passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + description);
    passed++;
    Console.WriteLine("PASS " + description);
}
FilterRule Rule(FilterActionType action, string argument = "", string? accountId = null, string? container = null,
    string? mailbox = null, string? sender = null, bool stop = false) => new() {
    Name = $"Check {action}", AccountId = accountId ?? "", AccountFolderId = container ?? "",
    FolderFullName = mailbox ?? "", SenderAddress = sender ?? "", StopProcessing = stop,
    Conditions = accountId == null && container == null && sender == null
        ? [new FilterCondition { Field = FilterField.Subject, Operator = FilterOperator.Contains, Value = "check" }] : [],
    Actions = [new FilterAction { Type = action, Argument = argument }] };
void Rules(params FilterRule[] values)
{
    foreach (var existing in FilterRuleStore.Rules) FilterRuleStore.Remove(existing.Id);
    foreach (var value in values) FilterRuleStore.Save(value);
}
MailMessageSummary Message(string sender = "sender@example.invalid") => new() {
    Uid = 1, MessageId = "<" + Guid.NewGuid().ToString("N") + "@example.invalid>", Subject = "check message",
    FromName = "Visible sender", FromAddress = sender, SizeBytes = 1536 };
(MailMessageSummary Message, RuleProcessResult Result) Process(MailAccountData? a = null, string folder = "INBOX", string sender = "sender@example.invalid")
{
    var message = Message(sender);
    return (message, RuleEngine.ProcessIncoming(a ?? account, folder, [message]));
}

var app = Rule(FilterActionType.Mute);
var group = Rule(FilterActionType.Unmute, container: parent.Id);
var nested = Rule(FilterActionType.Mute, container: child.Id);
var mailbox = Rule(FilterActionType.Unmute, accountId: account.Id, mailbox: "INBOX");
var accountRule = Rule(FilterActionType.Mute, accountId: account.Id);
var senderRule = Rule(FilterActionType.Unmute, accountId: account.Id, sender: "SENDER@example.invalid");
Rules(app, group, nested, mailbox, accountRule, senderRule);
var (arrival, result) = Process();
Check(!arrival.Flags.HasFlag(MailFlags.Muted), "sender unmute overrides every broader scope");
Check(RuleEngine.GetNotificationOverride(arrival) == true && RuleEngine.GetNotificationPriority(arrival) >= 400, "notification metadata retains winning sender priority");
Check(Process(sender: "someone@example.invalid").Message.Flags.HasFlag(MailFlags.Muted), "sender address gate is exact");
Rules(app, group, nested, mailbox, accountRule);
Check(Process().Message.Flags.HasFlag(MailFlags.Muted), "account rule overrides mailbox and account-container rules");
Rules(app, group, nested, mailbox);
Check(!Process().Message.Flags.HasFlag(MailFlags.Muted), "mailbox rule overrides account-container rules");
Check(Process(folder: "Archive").Message.Flags.HasFlag(MailFlags.Muted), "mailbox scope retains source folder identity");
Rules(app, group, nested);
Check(Process().Message.Flags.HasFlag(MailFlags.Muted), "nearest account-container rule overrides ancestor");
Rules(app, group);
Check(!Process().Message.Flags.HasFlag(MailFlags.Muted), "account-container rule includes descendant accounts");
Check(Process(accountB).Message.Flags.HasFlag(MailFlags.Muted), "account-container rules isolate unrelated branches");
Check(!RuleEngine.AppliesToScope(group, accountB, "INBOX"), "unrelated account scope gate fails");
var orphan = Rule(FilterActionType.MarkRead, container: Guid.NewGuid().ToString("D"));
Check(!RuleEngine.AppliesToScope(orphan, account, "INBOX"), "deleted account-container ID never normalizes to Main");

Rules(Rule(FilterActionType.MoveToRemoteFolder, "Other"),
    Rule(FilterActionType.MoveToLocalFolder, "Saved", accountId: account.Id),
    Rule(FilterActionType.MoveToRemoteFolder, "Archive", accountId: account.Id, sender: "sender@example.invalid"));
(_, result) = Process();
Check(result.RemoteMoves.Count == 1 && result.RemoteMoves[0].TargetFolder == "Archive" && result.LocalMoves.Count == 0, "conflicting move actions produce exactly one winning destination");
Rules(Rule(FilterActionType.MoveToRemoteFolder, "Other"), Rule(FilterActionType.MoveToRemoteFolder, "INBOX", accountId: account.Id));
(_, result) = Process();
Check(result.RemoteMoves.Count == 0 && result.LocalMoves.Count == 0, "higher-priority stay-in-current-folder suppresses lower moves");
Rules(Rule(FilterActionType.MarkRead, accountId: account.Id), Rule(FilterActionType.MarkUnread, accountId: account.Id, sender: "sender@example.invalid"));
Check(Process().Message.IsUnread, "sender mark-unread overrides broader mark-read");
Rules(Rule(FilterActionType.Flag, accountId: account.Id), Rule(FilterActionType.Unflag, accountId: account.Id, sender: "sender@example.invalid"));
Check(!Process().Message.Flags.HasFlag(MailFlags.Flagged), "sender unflag overrides broader star");
Rules(Rule(FilterActionType.MarkImportant, accountId: account.Id), Rule(FilterActionType.ClearImportant, accountId: account.Id, sender: "sender@example.invalid"));
Check(!Process().Message.Flags.HasFlag(MailFlags.Important), "sender clear-importance overrides broader mark-important");
Rules(Rule(FilterActionType.SetNotificationSound, "chime", accountId: account.Id),
    Rule(FilterActionType.SetNotificationSound, "ping", accountId: accountB.Id));
var soundA = Message();
var soundB = Message();
soundA.MessageId = soundB.MessageId = "<shared@example.invalid>";
RuleEngine.ProcessIncoming(account, "INBOX", [soundA]);
RuleEngine.ProcessIncoming(accountB, "INBOX", [soundB]);
Check(RuleEngine.GetNotificationSound(soundA) == "chime" && RuleEngine.GetNotificationSound(soundB) == "ping", "notification sounds stay isolated when accounts share a Message-Id");
soundA.MessageId = "";
RuleEngine.ProcessIncoming(account, "INBOX", [soundA]);
Check(RuleEngine.GetNotificationSound(soundA) == "chime", "messages without Message-Id retain their rule sound");
Rules(senderRule);
RuleEngine.ProcessIncoming(account, "INBOX", [soundA]);
Check(RuleEngine.GetNotificationSound(soundA) == null && RuleEngine.GetNotificationOverride(soundA) == true, "a new rule pass clears obsolete arrival metadata");
Rules(Rule(FilterActionType.AddTag, "newsletter"), Rule(FilterActionType.RemoveTag, "newsletter", accountId: account.Id), Rule(FilterActionType.AddTag, "work", accountId: account.Id));
arrival = Process().Message;
Check(!arrival.Tags.Contains("newsletter") && arrival.Tags.Contains("work"), "tag conflicts resolve per tag while different tags accumulate");
Rules(Rule(FilterActionType.MarkRead, accountId: account.Id, stop: true), Rule(FilterActionType.AddTag, "blocked"), senderRule);
arrival = Process().Message;
Check(arrival.Flags.HasFlag(MailFlags.Seen) && !arrival.Flags.HasFlag(MailFlags.Muted) && !arrival.Tags.Contains("blocked"), "lower Stop cannot suppress an already-applied sender rule");
Rules(Rule(FilterActionType.MarkRead, accountId: account.Id, stop: true), Rule(FilterActionType.Flag, accountId: account.Id));
Check(!Process().Message.Flags.HasFlag(MailFlags.Flagged), "Stop suppresses remaining rules at the same priority");

Rules();
FilterRuleStore.UpsertSenderMoveRule(account.Id, "sender@example.invalid", MessageStore.LocalFolderPrefix + "Saved", true);
FilterRuleStore.UpsertSenderMoveRule(account.Id, "SENDER@example.invalid", "Archive", false);
Check(FilterRuleStore.Rules.Count == 1 && FilterRuleStore.Rules[0].Actions[0].Argument == "Archive", "sender move upsert replaces the previous family destination");
FilterRuleStore.UpsertSenderMuteRule(account.Id, "sender@example.invalid", true);
FilterRuleStore.UpsertSenderMuteRule(account.Id, "sender@example.invalid", false);
Check(FilterRuleStore.Rules.Count == 2 && FilterRuleStore.Rules.Any(r => r.Actions[0].Type == FilterActionType.Unmute), "sender mute upsert replaces mute with explicit unmute");
FilterRuleStore.RemoveSenderActionRule(account.Id, "sender@example.invalid", FilterActionType.Mute);
Check(FilterRuleStore.Rules.Count == 1, "sender removal clears both mute variants");
FilterRuleStore.RemoveSenderActionRule(account.Id, "sender@example.invalid", FilterActionType.MoveToLocalFolder);
Check(FilterRuleStore.Rules.Count == 0, "sender removal clears both move variants");
var local = FilterRuleStore.UpsertSenderMoveRule(account.Id, "sender@example.invalid", MessageStore.LocalFolderPrefix + "Saved", true);
Check(local.Actions[0].Argument == "Saved", "local sender move strips the local folder prefix once");
var olderSender = Rule(FilterActionType.Unmute, accountId: account.Id, sender: "sender@example.invalid");
olderSender.Conditions = [new FilterCondition { Field = FilterField.Subject, Operator = FilterOperator.Contains, Value = "check" }];
Rules(olderSender);
FilterRuleStore.UpsertSenderMuteRule(account.Id, "sender@example.invalid", true);
Check(Process().Message.Flags.HasFlag(MailFlags.Muted), "new explicit sender shortcut precedes older conditional sender peers");

var numeric = Rule(FilterActionType.MarkRead, accountId: account.Id);
numeric.Conditions = [new FilterCondition { Field = FilterField.SizeKb, Operator = FilterOperator.Equals, Value = "1.5" }];
Rules(numeric);
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
Check(Process().Message.Flags.HasFlag(MailFlags.Seen), "numeric equality uses invariant decimal parsing under Turkish culture");
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
numeric.Conditions[0].Value = "NaN";
Check(FilterRuleStore.Validate(numeric).Count > 0, "non-finite size thresholds are rejected");
var malformed = Rule(FilterActionType.MarkRead, accountId: account.Id);
malformed.Conditions = [new FilterCondition { Field = FilterField.Subject, Operator = FilterOperator.MatchesRegex, Value = "[" }];
Check(FilterRuleStore.Validate(malformed).Count > 0, "invalid regex is rejected before save");
var duplicateMoves = Rule(FilterActionType.MoveToRemoteFolder, "Archive", accountId: account.Id);
duplicateMoves.Actions.Add(new FilterAction { Type = FilterActionType.MoveToLocalFolder, Argument = "Saved" });
Check(FilterRuleStore.Validate(duplicateMoves).Count > 0, "a rule cannot queue two move destinations");
var exactAddress = Rule(FilterActionType.MarkRead, accountId: account.Id);
exactAddress.Conditions = [new FilterCondition { Field = FilterField.FromAddress, Operator = FilterOperator.Equals, Value = "sender@example.invalid" }];
Rules(exactAddress);
Check(Process().Message.Flags.HasFlag(MailFlags.Seen), "exact FromAddress ignores sender display name");

var cached = Message();
MessageStore.UpsertSummaries(account.Id, "INBOX", [cached]);
var previewRule = Rule(FilterActionType.MarkRead, accountId: account.Id, mailbox: "INBOX", sender: "sender@example.invalid");
Rules(previewRule);
var preview = RuleEngine.Preview(previewRule);
Check(preview.Matched == 1 && preview.Scanned == 1 && cached.IsUnread, "preview applies source and sender scope without mutating cached mail");
previewRule.SenderAddress = "other@example.invalid";
Check(RuleEngine.Preview(previewRule).Matched == 0, "preview respects exact sender mismatch");
var draft = FilterRuleStore.Clone(FilterRuleStore.Rules[0]);
draft.Name = "unsaved draft";
Check(FilterRuleStore.Rules[0].Name != draft.Name, "editor clone does not mutate the persisted rule");
var peerA = Rule(FilterActionType.Flag, accountId: account.Id);
var peerB = Rule(FilterActionType.MarkRead, accountId: account.Id);
Rules(peerA, senderRule, peerB);
FilterRuleStore.MoveRule(peerB.Id, true);
Check(FilterRuleStore.Rules[0].Id == peerB.Id && RuleEngine.Priority(FilterRuleStore.Rules[1]) >= 400, "peer reordering retains scope priorities");
FilterRuleStore.Load();
Check(FilterRuleStore.Rules.Count == 3 && FilterRuleStore.Rules[0].Id == peerB.Id, "scope fields and order survive storage reload");
string filtersPath = Path.Combine(AppPaths.UserData, FilterRuleStore.FiltersFileName);
string validFilters = File.ReadAllText(filtersPath);
File.WriteAllText(filtersPath, "{invalid");
FilterRuleStore.Load();
bool rejected = false;
try { FilterRuleStore.Save(peerA); } catch (InvalidOperationException) { rejected = true; }
Check(rejected && File.ReadAllText(filtersPath) == "{invalid", "unreadable filters are preserved when an edit is attempted");
File.WriteAllText(filtersPath, validFilters);
var recovery = Rule(FilterActionType.AddTag, "recovered", accountId: account.Id);
FilterRuleStore.Save(recovery);
Check(FilterRuleStore.Rules.Count == 4, "retry reload preserves recovered rules before adding a new rule");
validFilters = File.ReadAllText(filtersPath);
File.WriteAllText(filtersPath, "[null]");
FilterRuleStore.Load();
Check(FilterRuleStore.Rules.Count == 4, "semantically corrupt filter file does not replace live rules");
File.WriteAllText(filtersPath, validFilters);
FilterRuleStore.Load();
Check(FilterRules.LinkFor(account.Id, "INBOX/Sub folder", senderAddress: "a+b@example.invalid")
    .Contains("folder=INBOX%2FSub%20folder") && FilterRules.LinkFor(accountFolderId: parent.Id).Contains("accountFolder="), "filter deep links escape mailbox and sender arguments");

// Invoke the existing internal decision function; the test consumes production without exposing test APIs.
var allows = typeof(NotificationService).GetMethod("AllowsNotification", BindingFlags.NonPublic | BindingFlags.Static)!;
bool Allows(MailFolderData folder, MailMessageSummary message) => (bool)allows.Invoke(null, [account, folder, message])!;
var inbox = new MailFolderData { AccountId = account.Id, FullName = "INBOX", Role = FolderRole.Inbox, Selectable = true };
var trash = new MailFolderData { AccountId = account.Id, FullName = "Trash", Role = FolderRole.Trash, Selectable = true };
Settings.QuietHoursStart.Value = 0;
Settings.QuietHoursEnd.Value = 0;
Settings.NotifyOnNewMail.Value = true;
FolderSettingsStore.Save(FolderRole.Inbox, new FolderPreferences { MutedUntilUtc = DateTimeOffset.MaxValue }, account.Id, "INBOX");
AccountStore.GetSettings(account.Id).NotifyOnNewMail.Value = false;
Rules(senderRule);
arrival = Process().Message;
Check(Allows(inbox, arrival), "sender notify overrides account and folder mute preferences");
Check(!Allows(trash, arrival), "Trash remains suppressed even with sender notify");
Rules(Rule(FilterActionType.Unmute));
arrival = Process().Message;
Check(!Allows(inbox, arrival), "application notify rule cannot override explicit account mute");
Rules(Rule(FilterActionType.Mute, accountId: account.Id, sender: "sender@example.invalid"));
AccountStore.GetSettings(account.Id).NotifyOnNewMail.Value = true;
Check(!Allows(inbox, Process().Message), "sender mute overrides account notify");

// ListFilterService conflict resolution tests
var condUnread1 = new FilterConditionEntry { Condition = new FilterCondition { Field = FilterField.IsUnread, Operator = FilterOperator.Equals, Value = "false" } };
var condUnread2 = new FilterConditionEntry { Condition = new FilterCondition { Field = FilterField.IsUnread, Operator = FilterOperator.Equals, Value = "true" } };
var resolvedBooleans = ListFilterService.Resolve([condUnread1, condUnread2]);
Check(resolvedBooleans[0].Status == FilterConditionStatus.Overridden && resolvedBooleans[1].Status == FilterConditionStatus.Active, "later boolean filter overrides earlier one");

var condDateAfter1 = new FilterConditionEntry { Condition = new FilterCondition { Field = FilterField.ReceivedDate, Operator = FilterOperator.GreaterThan, Value = "2026-01-01" } };
var condDateAfter2 = new FilterConditionEntry { Condition = new FilterCondition { Field = FilterField.ReceivedDate, Operator = FilterOperator.GreaterThan, Value = "2026-02-01" } };
var resolvedDates = ListFilterService.Resolve([condDateAfter1, condDateAfter2]);
Check(resolvedDates[0].Status == FilterConditionStatus.Overridden && resolvedDates[1].Status == FilterConditionStatus.Active, "later date after bound overrides earlier after bound");

var condDateBefore = new FilterConditionEntry { Condition = new FilterCondition { Field = FilterField.ReceivedDate, Operator = FilterOperator.LessThan, Value = "2026-03-01" } };
var resolvedRange = ListFilterService.Resolve([condDateAfter1, condDateBefore]);
Check(resolvedRange[0].Status == FilterConditionStatus.Active && resolvedRange[1].Status == FilterConditionStatus.Active, "non-conflicting after and before bounds form an active range");

var condContradictoryBefore = new FilterConditionEntry { Condition = new FilterCondition { Field = FilterField.ReceivedDate, Operator = FilterOperator.LessThan, Value = "2025-12-01" } };
var resolvedContradiction = ListFilterService.Resolve([condDateAfter1, condContradictoryBefore]);
Check(resolvedContradiction[0].Status == FilterConditionStatus.Overridden && resolvedContradiction[1].Status == FilterConditionStatus.Active, "contradictory date range causes later bound to override earlier");

var testMsg = Message();
testMsg.DateUtc = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);
Check(ListFilterService.MatchesAllActive(testMsg, resolvedRange), "test message inside active range matches");
testMsg.DateUtc = new DateTime(2025, 12, 15, 0, 0, 0, DateTimeKind.Utc);
Check(!ListFilterService.MatchesAllActive(testMsg, resolvedRange), "test message outside active range does not match");

Console.WriteLine($"Completed {passed} filter checks. Data: {AppPaths.Root}");
