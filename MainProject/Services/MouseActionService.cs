using Microsoft.JSInterop;
using System.Diagnostics;
using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.UI.Pages;

namespace MyLovelyMail.MainProject.Services;

/// <summary>Routes side buttons, menu commands and toolbars through the existing mail operations and one input failure boundary.</summary>
public static class MouseActionService
{
    const bool IsLogEnabled = true;
    public const string CopyAction = "copy";
    public const string ReadAction = "Read";
    public const string TrashAction = "Trash";
    public const string FolderReadAction = "folder-read";
    public const string SyncAction = "sync";
    public const string AccountSyncAction = "account-sync";
    public const string ComposeAction = "compose";
    public const string ComposeToAction = "compose-to";
    public static Func<MouseMenuRequest, Task>? MenuRequested;
    public static Func<MouseMenuRequest, Task>? MailMenuRequested;
    public static string? LastStatus { get; private set; }
    public static event Action<string>? OnStatus;

    /// <summary>Receives declared commands from the shared input script.</summary>
    /// <param name="action">Declared command name.</param>
    /// <param name="payload">Plain text or serialized complete message identities.</param>
    [JSInvokable("MouseSideButton")]
    public static Task SideButtonAsync(string action, string payload) => RunAsync(action, payload);

    /// <summary>Requests a specific mail menu or the common fallback menu from its active owner.</summary>
    /// <param name="request">Pointer and plain-text context, never executable content.</param>
    [JSInvokable("MouseContextMenu")]
    public static async Task OpenContextMenuAsync(MouseMenuRequest request)
    {
        try
        {
            if (request.MessagePayload.Length > 0 && MailMenuRequested != null) await MailMenuRequested(request);
            else if (MenuRequested != null) await MenuRequested(request);
            else Log("Context menu requested before its host was ready.", LogLevel.Warning, Run: IsLogEnabled);
        }
        catch (Exception exception)
        {
            Log($"Could not open context menu: {exception}", LogLevel.Error, Run: IsLogEnabled);
            ReportStatus("Could not open the actions menu. See the log for details.");
        }
    }

    /// <summary>Publishes an input result; null dismisses it.</summary>
    /// <param name="status">Result text for the app-wide status host.</param>
    public static void ReportStatus(string? status)
    {
        LastStatus = status;
        OnStatus?.Invoke(status ?? string.Empty);
    }

    /// <summary>Dispatches a markup/JavaScript command without duplicating the typed mail execution path.</summary>
    /// <param name="action">Declared command.</param>
    /// <param name="payload">Text, account id or serialized message identities.</param>
    /// <returns>The actual result displayed by the status host.</returns>
    public static Task<string> RunAsync(string action, string payload) => RunAsync(action, async () =>
    {
        if (Enum.TryParse<MailMessageCommand>(action, ignoreCase: true, out var command) && Enum.IsDefined(command))
        {
            var request = payload.TrimStart().StartsWith('[')
                ? new MailMessageRequest { Messages = JsonSerializer.Deserialize<MailMessageIdentity[]>(payload) ?? [] }
                : JsonSerializer.Deserialize<MailMessageRequest>(payload) ?? new();
            return ExecuteMessages(command, request.Messages, request.Argument, request.DueUtc, includeSelection: true);
        }
        switch (action)
        {
            case CopyAction:
                return await ClipboardService.CopyAsync(payload) ? $"📋 Copied {payload.Length} characters." : "Could not copy the text.";
            case FolderReadAction:
                var folder = JsonSerializer.Deserialize<MailMessageIdentity>(payload);
                return ExecuteFolderRead(folder.AccountId, folder.FolderFullName);
            case SyncAction:
                return await CheckMailAsync();
            case AccountSyncAction:
                return await CheckMailAsync(payload);
            case ComposeAction:
                return OpenDraft(accountId: string.IsNullOrEmpty(payload) ? null : payload);
            case ComposeToAction:
                return OpenDraft(to: payload);
            default:
                throw new ArgumentException($"Unknown mouse action '{action}'.", nameof(action));
        }
    });

    /// <summary>Runs captured mail targets through the same execution and error handling for every input surface.</summary>
    /// <param name="command">Mail operation.</param>
    /// <param name="messages">Captured complete message identities.</param>
    /// <param name="argument">Optional tag or destination folder.</param>
    /// <param name="dueUtc">Optional snooze deadline.</param>
    /// <param name="includeSelection">Includes the current selection when a quick action targets one of its members.</param>
    /// <returns>Displayed operation result.</returns>
    public static Task<string> RunMessagesAsync(MailMessageCommand command, IEnumerable<MailMessageIdentity> messages,
        string? argument = null, DateTime? dueUtc = null, bool includeSelection = false)
    {
        var snapshot = messages.Distinct().ToArray();
        return RunAsync(command.ToString(), () => Task.FromResult(ExecuteMessages(command, snapshot, argument, dueUtc, includeSelection)));
    }

    /// <summary>Marks one folder's cached mail read through the existing batched operation.</summary>
    /// <param name="folder">Exact folder scope.</param>
    /// <returns>Displayed operation result.</returns>
    public static Task<string> ReadFolderAsync(MailFolderData folder) => RunAsync(FolderReadAction,
        () => Task.FromResult(ExecuteFolderRead(folder.AccountId, folder.FullName)));

    /// <summary>Resolves a folder owner once and returns the real batched read count.</summary>
    /// <param name="accountId">Owning account.</param>
    /// <param name="fullName">Exact folder path.</param>
    /// <returns>Result text.</returns>
    static string ExecuteFolderRead(string accountId, string fullName)
    {
        var account = AccountStore.GetById(accountId) ?? throw new InvalidOperationException("The folder's account no longer exists.");
        return $"Marked {MessageActions.SetFolderRead(account, fullName)} cached message(s) read.";
    }

    /// <summary>Uses the scheduler's existing protocol, health and retry logic for global or account checks.</summary>
    /// <param name="accountId">Null checks all enabled accounts; an id explicitly checks only that account.</param>
    /// <returns>Result text.</returns>
    static async Task<string> CheckMailAsync(string? accountId = null)
    {
        if (AccountStore.Accounts.Count == 0)
        {
            NavigationBridge.TryNavigate(AccountWizard.RoutePath);
            return "Add an account to start receiving mail.";
        }
        if (accountId != null && AccountStore.GetById(accountId) == null)
            throw new InvalidOperationException("The account no longer exists.");
        ReportStatus("Checking mail.");
        await SyncScheduler.SyncNowAsync(accountId: accountId);
        return accountId != null && SyncHealthService.For(accountId) is { IsFailing: true } failed
            ? $"Mail check failed: {failed.LastErrorMessage}" : "Mail check finished.";
    }

    /// <summary>Uses the existing draft builder and preserves a draft that is already being written.</summary>
    /// <param name="accountId">Explicit sending account; null uses the selected account.</param>
    /// <param name="to">Optional recipient for an address shortcut.</param>
    /// <returns>Displayed navigation result.</returns>
    static string OpenDraft(string? accountId = null, string? to = null)
    {
        var account = accountId == null ? MailUiState.SelectedAccount ?? AccountStore.Accounts.FirstOrDefault() : AccountStore.GetById(accountId);
        if (account == null) { NavigationBridge.TryNavigate(AccountWizard.RoutePath); return "Add a mail account first."; }
        if (accountId == null && to == null && MailUiState.ActiveCompose is { } current && current.Account?.Id == account.Id)
            MailUiState.ShowPane(MailUiState.PaneOccupant.Compose);
        else
        {
            if (MailUiState.SelectedAccount?.Id != account.Id) MailUiState.SelectAccount(account, restoreFolder: true);
            var draft = ComposeService.BuildNew(account);
            draft.To = to ?? string.Empty;
            if (to != null) MailUiState.ClosePerson();
            MailUiState.OpenCompose(draft);
        }
        NavigationBridge.TryNavigate(NavigationConstants.Mail.Link);
        return "Draft opened.";
    }

    /// <summary>Maintains UI selection and reader state around the existing grouped mail executor.</summary>
    /// <param name="command">Requested mail operation.</param>
    /// <param name="keys">Captured identities.</param>
    /// <param name="argument">Tag or destination.</param>
    /// <param name="dueUtc">Snooze deadline.</param>
    /// <param name="includeSelection">Expands an explicitly clicked row to the current selected messages.</param>
    /// <returns>Result text.</returns>
    static string ExecuteMessages(MailMessageCommand command, MailMessageIdentity[] keys, string? argument = null, DateTime? dueUtc = null, bool includeSelection = false)
    {
        if (includeSelection && keys.Any(MailUiState.SelectedMessages.Contains)) keys = [.. keys.Concat(MailUiState.SelectedMessages).Distinct()];
        bool filing = command is MailMessageCommand.Trash or MailMessageCommand.Archive or MailMessageCommand.Spam
            or MailMessageCommand.Restore or MailMessageCommand.Move;
        bool includesReader = filing && MailUiState.OpenMessageKey is { } opened && keys.Contains(opened);
        int changed = MessageActions.Execute(command, keys, argument, dueUtc);
        if (includesReader && changed > 0) MailUiState.CloseMessage();
        else if (MailUiState.OpenMessageKey is { } current && MessageStore.GetSummary(current.AccountId, current.FolderFullName, current.Uid) is { } updated
            && !ReferenceEquals(updated, MailUiState.OpenMessage)) MailUiState.OpenMessageInReader(updated, current);
        if (filing || command is MailMessageCommand.Snooze or MailMessageCommand.Wake)
            MailUiState.SelectMessages(keys, selected: false);
        return filing ? $"{command}: filing requested for {changed} message(s)." : $"{command}: {changed} message(s) changed.";
    }

    /// <summary>Logs and awaits one complete input action, reporting failure without treating it as success.</summary>
    /// <param name="action">Operation name for logs and errors.</param>
    /// <param name="execute">Complete operation; lower-level recovery stays with its existing owner.</param>
    /// <returns>Displayed success or failure result.</returns>
    public static async Task<string> RunAsync(string action, Func<Task<string>> execute)
    {
        var stopwatch = Stopwatch.StartNew();
        Log($"Input action started: action={action}.", Run: IsLogEnabled);
        string status;
        try { status = await execute(); }
        catch (Exception exception)
        {
            Log($"Input action failed: action={action}; {exception}", LogLevel.Error, Run: IsLogEnabled);
            status = $"Could not complete {action}: {exception.Message}";
        }
        finally { Log($"Input action finished: action={action}; elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}.", Run: IsLogEnabled); }
        ReportStatus(status);
        return status;
    }
}

/// <summary>Plain context for the common menu; passwords and executable markup are never included.</summary>
public sealed class MouseMenuRequest
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Selection { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public string MessagePayload { get; set; } = string.Empty;
    public string PrimaryPayload { get; set; } = string.Empty;
    public bool IsMailContent { get; set; }
    public bool CanActivate { get; set; }
    public bool CanSelect { get; set; }
    public bool CanReset { get; set; }
}
