using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;

namespace MyLovelyMail.MainProject.UI.Pages
{
    // Compose pane: new/reply/forward draft lifecycle, idle autosave, send.
    public partial class Mail
    {
        bool Sending { get; set; }
        string? SendError { get; set; }
        System.Timers.Timer? draftAutosaveTimer;
        DateTime? DraftSavedAt { get; set; }

        static void StartCompose()
        {
            if (MailUiState.SelectedAccount is { } account)
                MailUiState.OpenCompose(ComposeService.BuildNew(account));
        }

        async Task SendActiveDraftAsync()
        {
            if (MailUiState.ActiveCompose is not { } draft) return;
            Sending = true;
            SendError = null;
            StateHasChanged();
            try
            {
                await ComposeService.SendAsync(draft);
                SoundService.Play("success");
                MailUiState.CloseCompose();
            }
            catch (Exception ex)
            {
                SendError = ex.Message;
            }
            finally
            {
                Sending = false;
            }
        }

        /// <summary>Any keystroke in the compose fields re-arms a ~3s idle autosave.</summary>
        void NoteComposeActivity()
        {
            draftAutosaveTimer?.Dispose();
            draftAutosaveTimer = new System.Timers.Timer(3000) { AutoReset = false };
            draftAutosaveTimer.Elapsed += (_, _) => SaveActiveDraft();
            draftAutosaveTimer.Start();
        }

        void SaveActiveDraft()
        {
            if (MailUiState.ActiveCompose is not { } draft) return;
            if (draft.To.Length + draft.Subject.Length + draft.Body.Length == 0) return;
            ComposeService.SaveDraft(draft);
            DraftSavedAt = DateTime.Now;
            InvokeAsync(StateHasChanged);
        }

        void SaveAndCloseCompose()
        {
            draftAutosaveTimer?.Dispose();
            SaveActiveDraft();
            DraftSavedAt = null;
            MailUiState.CloseCompose();
        }

        void DiscardCompose()
        {
            draftAutosaveTimer?.Dispose();
            if (MailUiState.ActiveCompose is { } draft)
                ComposeService.DeleteDraft(draft);
            DraftSavedAt = null;
            MailUiState.CloseCompose();
        }
    }
}
