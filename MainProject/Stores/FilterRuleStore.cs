using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores;

/// <summary>Ordered rules in UserData/filters.json. Scope priority precedes user order.</summary>
public static class FilterRuleStore
{
    public const string FiltersFileName = "filters.json";
    static string FiltersPath => Path.Combine(AppPaths.UserData, FiltersFileName);
    static readonly object gate = new();
    static List<FilterRule> rules = [];
    static bool readFailed;
    public static event Action? OnRulesChanged;
    public static IReadOnlyList<FilterRule> Rules { get { lock (gate) return rules.ToArray(); } }

    public static void Load()
    {
        lock (gate)
        {
            try
            {
                var loaded = File.Exists(FiltersPath)
                    ? JsonSerializer.Deserialize<List<FilterRule>>(File.ReadAllText(FiltersPath), JsonDefaults.Indented) ?? [] : [];
                if (loaded.Any(rule => rule == null || rule.Id == null || rule.Name == null || rule.AccountId == null
                    || rule.AccountFolderId == null || rule.FolderFullName == null || rule.SenderAddress == null
                    || rule.Conditions == null || rule.Actions == null
                    || rule.Conditions.Any(c => c == null || c.Value == null)
                    || rule.Actions.Any(a => a == null || a.Argument == null)))
                    throw new InvalidDataException("A filter contains missing fields.");
                foreach (var rule in loaded)
                    foreach (var condition in rule.Conditions)
                    {
                        // Preserve the effective meaning of older operator-agnostic fields.
                        if (condition.Field == FilterField.HasAttachment) condition.Operator = FilterOperator.Equals;
                        if (condition.Field == FilterField.SizeKb && condition.Operator is not (FilterOperator.Equals or FilterOperator.GreaterThan or FilterOperator.LessThan))
                            condition.Operator = FilterOperator.GreaterThan;
                    }
                rules = loaded;
                readFailed = false;
            }
            catch (Exception ex)
            {
                readFailed = true;
                Log($"Could not read filters: {ex}", LogLevel.Error);
            }
        }
    }

    static void Persist(List<FilterRule> updated)
    {
        if (readFailed) throw new InvalidOperationException("Filters could not be read. The existing file has been preserved.");
        AtomicFile.WriteAllText(FiltersPath, JsonSerializer.Serialize(updated, JsonDefaults.Indented));
        rules = updated;
    }

    public static void Save(FilterRule rule) => SaveCore(rule, firstAtPriority: false);

    static void SaveCore(FilterRule rule, bool firstAtPriority)
    {
        var errors = Validate(rule);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        lock (gate)
        {
            EnsureReadable();
            var updated = new List<FilterRule>(rules);
            int index = updated.FindIndex(r => r.Id == rule.Id);
            var snapshot = Clone(rule);
            snapshot.Name = snapshot.Name.Trim();
            snapshot.SenderAddress = snapshot.SenderAddress.Length > 0 && MimeKit.MailboxAddress.TryParse(snapshot.SenderAddress, out var sender)
                ? sender.Address : snapshot.SenderAddress.Trim();
            if (firstAtPriority)
            {
                updated.RemoveAll(r => r.Id == rule.Id);
                int first = updated.FindIndex(r => Services.Mail.RuleEngine.Priority(r) == Services.Mail.RuleEngine.Priority(snapshot));
                updated.Insert(first >= 0 ? first : updated.Count, snapshot);
            }
            else if (index >= 0) updated[index] = snapshot;
            else updated.Add(snapshot);
            Persist(updated);
        }
        OnRulesChanged?.Invoke();
    }

    public static FilterRule Clone(FilterRule rule) => JsonSerializer.Deserialize<FilterRule>(JsonSerializer.Serialize(rule))!;

    public static bool CanMoveRule(FilterRule rule, bool up)
    {
        var peers = Rules.Where(r => Services.Mail.RuleEngine.Priority(r) == Services.Mail.RuleEngine.Priority(rule)).ToArray();
        int index = Array.FindIndex(peers, r => r.Id == rule.Id);
        return index >= 0 && (up ? index > 0 : index < peers.Length - 1);
    }

    static void EnsureReadable()
    {
        if (!readFailed) return;
        Load();
        if (readFailed) throw new InvalidOperationException("Filters could not be read. The existing file has been preserved.");
    }

    public static void Remove(string ruleId)
    {
        lock (gate)
        {
            EnsureReadable();
            var updated = rules.Where(r => r.Id != ruleId).ToList();
            if (updated.Count == rules.Count) return;
            Persist(updated);
        }
        OnRulesChanged?.Invoke();
    }

    /// <summary>Reorders only peers in the same scope priority; scope precedence cannot be dragged away.</summary>
    public static void MoveRule(string ruleId, bool up)
    {
        lock (gate)
        {
            EnsureReadable();
            int index = rules.FindIndex(r => r.Id == ruleId);
            if (index < 0) return;
            int priority = Services.Mail.RuleEngine.Priority(rules[index]);
            int target = index + (up ? -1 : 1);
            while (target >= 0 && target < rules.Count && Services.Mail.RuleEngine.Priority(rules[target]) != priority)
                target += up ? -1 : 1;
            if (target < 0 || target >= rules.Count) return;
            var updated = new List<FilterRule>(rules);
            (updated[index], updated[target]) = (updated[target], updated[index]);
            Persist(updated);
        }
        OnRulesChanged?.Invoke();
    }

    public static string ActionFamily(FilterActionType type) => type switch
    {
        FilterActionType.MoveToLocalFolder or FilterActionType.MoveToRemoteFolder => "move",
        FilterActionType.MarkRead or FilterActionType.MarkUnread => "read",
        FilterActionType.MarkImportant or FilterActionType.ClearImportant => "important",
        FilterActionType.Flag or FilterActionType.Unflag => "star",
        FilterActionType.Mute or FilterActionType.Unmute => "notification",
        FilterActionType.AddTag or FilterActionType.RemoveTag => "tag",
        _ => type.ToString()
    };

    /// <summary>One reusable rule per sender/account/action family; a new destination replaces the previous choice.</summary>
    public static FilterRule UpsertSenderActionRule(string accountId, string senderAddress, FilterAction action)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(senderAddress))
            throw new ArgumentException("Choose an account and sender address.");
        string family = ActionFamily(action.Type);
        var existing = Rules.FirstOrDefault(r => r.AccountId == accountId &&
            r.SenderAddress.Equals(senderAddress.Trim(), StringComparison.OrdinalIgnoreCase) && r.Conditions.Count == 0 &&
            r.AccountFolderId.Length == 0 && r.FolderFullName.Length == 0 && r.Actions.Count == 1 &&
            ActionFamily(r.Actions[0].Type) == family && (family != "tag" || r.Actions[0].Argument == action.Argument));
        var rule = existing == null ? new FilterRule() : Clone(existing);
        rule.Name = $"Sender {senderAddress.Trim()}: {family}";
        rule.AccountId = accountId;
        rule.SenderAddress = senderAddress.Trim();
        rule.Enabled = true;
        rule.Actions = [new FilterAction { Type = action.Type, Argument = action.Argument }];
        // The direct sender command is the newest explicit choice; do not leave it shadowed
        // by an earlier conditional rule at the same sender priority.
        SaveCore(rule, firstAtPriority: true);
        return rule;
    }

    public static FilterRule UpsertSenderMoveRule(string accountId, string senderAddress, string targetFolder, bool isLocal) =>
        UpsertSenderActionRule(accountId, senderAddress, new FilterAction {
            Type = isLocal ? FilterActionType.MoveToLocalFolder : FilterActionType.MoveToRemoteFolder,
            Argument = isLocal && targetFolder.StartsWith(MessageStore.LocalFolderPrefix, StringComparison.Ordinal)
                ? targetFolder[MessageStore.LocalFolderPrefix.Length..] : targetFolder });

    public static FilterRule UpsertSenderMuteRule(string accountId, string senderAddress, bool muted) =>
        UpsertSenderActionRule(accountId, senderAddress, new FilterAction { Type = muted ? FilterActionType.Mute : FilterActionType.Unmute });

    public static void RemoveSenderActionRule(string accountId, string senderAddress, FilterActionType type)
    {
        foreach (var rule in Rules.Where(r => r.AccountId == accountId &&
            r.SenderAddress.Equals(senderAddress.Trim(), StringComparison.OrdinalIgnoreCase) && r.Conditions.Count == 0 &&
            r.AccountFolderId.Length == 0 && r.FolderFullName.Length == 0 && r.Actions.Count == 1 &&
            ActionFamily(r.Actions[0].Type) == ActionFamily(type))) Remove(rule.Id);
    }

    public static IReadOnlyList<string> Validate(FilterRule rule)
    {
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(rule.Name)) errors.Add("Enter a rule name.");
        if (rule.Actions.Count == 0) errors.Add("Add at least one action.");
        if (rule.Conditions.Count == 0 && rule.AccountId.Length == 0 && rule.AccountFolderId.Length == 0 && rule.SenderAddress.Length == 0)
            errors.Add("Application rules need at least one condition.");
        if (rule.FolderFullName.Length > 0 && rule.AccountId.Length == 0) errors.Add("A mailbox folder needs an account.");
        if (rule.AccountId.Length > 0 && AccountStore.GetById(rule.AccountId) == null) errors.Add("Choose an existing account.");
        if (rule.FolderFullName.Length > 0 && !MessageStore.GetFolders(rule.AccountId).Any(f => f.Selectable && f.FullName == rule.FolderFullName))
            errors.Add("Choose an existing source mailbox folder.");
        if (rule.AccountFolderId.Length > 0 && !AccountFolderStore.Folders.Any(f => f.Id == rule.AccountFolderId)) errors.Add("Choose an existing account folder.");
        if (rule.SenderAddress.Length > 0 && !MimeKit.MailboxAddress.TryParse(rule.SenderAddress, out _)) errors.Add("Enter a valid sender address.");
        foreach (var condition in rule.Conditions)
        {
            bool numeric = condition.Field == FilterField.SizeKb;
            if (numeric && (!double.TryParse(condition.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) || number < 0))
                errors.Add("Size must be a non-negative number in kilobytes (use a decimal point).");
            if (numeric && condition.Operator is not (FilterOperator.Equals or FilterOperator.GreaterThan or FilterOperator.LessThan)) errors.Add("Size supports equals, greater than or less than.");
            if (!numeric && condition.Field != FilterField.HasAttachment && condition.Operator is FilterOperator.GreaterThan or FilterOperator.LessThan)
                errors.Add("Text fields do not support numeric comparisons.");
            if (condition.Field == FilterField.HasAttachment && (condition.Operator != FilterOperator.Equals || !bool.TryParse(condition.Value, out _)))
                errors.Add("Attachment conditions use Equals with true or false.");
            if (condition.Field != FilterField.HasAttachment && string.IsNullOrWhiteSpace(condition.Value)) errors.Add("Enter a condition value.");
            if (condition.Operator == FilterOperator.MatchesRegex)
            {
                try { _ = new Regex(condition.Value, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)); }
                catch (ArgumentException) { errors.Add("The regular expression is invalid."); }
            }
        }
        foreach (var action in rule.Actions)
        {
            if (action.Type is FilterActionType.MoveToLocalFolder or FilterActionType.MoveToRemoteFolder or FilterActionType.AddTag or FilterActionType.RemoveTag or FilterActionType.SetNotificationSound
                && string.IsNullOrWhiteSpace(action.Argument)) errors.Add($"{action.Type} needs a value.");
            if (action.Type is FilterActionType.MoveToLocalFolder or FilterActionType.MoveToRemoteFolder && action.Argument.Length > 0)
            {
                bool local = action.Type == FilterActionType.MoveToLocalFolder;
                string destination = local ? MessageStore.LocalFolderPrefix + action.Argument : action.Argument;
                foreach (var account in AccountStore.Accounts.Where(a => Services.Mail.RuleEngine.AppliesToScope(rule, a, rule.FolderFullName)))
                {
                    if (!local && account.Protocol != IncomingProtocol.Imap)
                        errors.Add("Server-folder moves require accounts that support server folders; use a local destination for this scope.");
                    if (!MessageStore.GetFolders(account.Id).Any(f => f.Selectable && f.FullName == destination && (f.IsLocal || MessageStore.IsLocalFolder(f.FullName)) == local))
                        errors.Add($"The destination '{action.Argument}' is unavailable in {account.EmailAddress}.");
                }
            }
        }
        if (rule.Actions.Count(a => ActionFamily(a.Type) == "move") > 1) errors.Add("Choose one move destination per rule.");
        return errors.Distinct().ToArray();
    }
}
