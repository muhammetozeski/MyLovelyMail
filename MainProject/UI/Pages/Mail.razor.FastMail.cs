using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Services.Mail.FastMail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.UI.Pages;

public partial class Mail
{
    readonly HashSet<string> fastMailDeletionPending = [];

    internal async Task DeleteFastMailAccountAsync(MailAccountData account)
    {
        if (account.Protocol != IncomingProtocol.FastMail || !fastMailDeletionPending.Add(account.Id)) return;
        try
        {
            var recovery = FastMailRecovery.FindRecoveryAccount();
            bool firstConfirm = await ModalService.ConfirmAsync(
                "Delete Fast Mail Account?",
                $"Delete Fast Mail account {account.EmailAddress} and its local data? An encrypted recovery script will be put in the Recycle Bin.",
                confirmLabel: "Continue",
                cancelLabel: "Cancel",
                isDanger: true);
            if (!firstConfirm) return;

            bool secondConfirm = await ModalService.ConfirmAsync(
                "Confirm Encrypted Recovery",
                $"Confirm deletion again. Recovery will require the password of normal account {recovery.EmailAddress} as it is now. Continue?",
                confirmLabel: "🗑️ Delete Fast Mail",
                cancelLabel: "Cancel",
                isDanger: true);
            if (!secondConfirm) return;

            if (MailUiState.ActiveCompose is { } draft && draft.Account?.Id == account.Id)
            {
                ComposeService.SaveDraft(draft);
                MailUiState.CloseCompose();
            }
            if (OutboxService.Current?.Draft.Account?.Id == account.Id) OutboxService.Undo();
            if (OutboxService.LastFailure?.Draft.Account?.Id == account.Id) OutboxService.DismissFailed();
            if (MailUiState.SelectedAccount?.Id == account.Id)
                MailUiState.SelectAccount(AccountStore.Accounts.FirstOrDefault(a => a.Id != account.Id), restoreFolder: true);
            await FastMailRecovery.DeleteAsync(account, recovery);
            SaveStatus = "Fast Mail deleted. Its encrypted recovery script is in the Recycle Bin.";
            MouseActionService.ReportStatus(SaveStatus);
        }
        catch (Exception ex)
        {
            SaveStatus = "Could not complete encrypted deletion: " + ex.Message;
            Log($"Encrypted Fast Mail deletion failed: {ex.GetType().Name}.", LogLevel.Error);
            await ModalService.AlertAsync("Fast Mail Deletion Failed", SaveStatus);
        }
        finally { fastMailDeletionPending.Remove(account.Id); }
    }
}
