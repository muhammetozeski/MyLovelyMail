using Microsoft.AspNetCore.Components.Forms;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;
using MyLovelyMail.MainProject.Services.Mail;
using Timer = System.Timers.Timer;

namespace MyLovelyMail.MainProject.UI.Pages
{
    // Compose pane: new/reply/forward draft lifecycle, attachments, idle autosave, send.
    public partial class Mail
    {
        const int MaxComposeAttachments = 20;
        const long MaxComposeAttachmentBytes = 50 * 1024 * 1024;

        /// <summary>Datalist element id shared by the To and Cc fields.</summary>
        const string ContactListId = "mail-contacts";

        /// <summary>Addresses offered while typing a recipient, harvested from the account's own mail.</summary>
        List<Contact> KnownContacts =>
            MailUiState.SelectedAccount is { } account ? ContactIndexService.Suggest(account.Id) : [];

        string? SendError { get; set; }
        System.Timers.Timer? draftAutosaveTimer;
        System.Timers.Timer? undoCountdownTimer;
        DateTime? DraftSavedAt { get; set; }

        /// <summary>Hands the draft to the outbox and closes the pane; the undo bar takes over.
        /// The draft is saved first so a failed submit still leaves it in Drafts.</summary>
        /// <summary>What the pre-send checks found; empty means nothing is worth stopping for.</summary>
        IReadOnlyList<SendWarning> SendWarnings { get; set; } = [];

        /// <summary>
        /// Rebuilds the quote at another style from the message the draft is answering, keeping
        /// whatever was typed ABOVE the attribution line. Rebuilding from the source is the only
        /// safe direction: re-parsing the edited body would have to guess which of its lines the
        /// user wrote.
        /// </summary>
        void RequoteDraft(ComposeDraft draft, QuoteStyle style)
        {
            if (draft.Account is not { } account || draft.SourceUid == 0) return;
            if (MessageStore.GetSummary(account.Id, draft.SourceFolder, draft.SourceUid) is not { } source) return;

            const string AttributionMarker = "\n\nOn ";
            int cut = draft.Body.IndexOf(AttributionMarker, StringComparison.Ordinal);
            string typed = cut >= 0 ? draft.Body[..cut] : draft.Body;
            draft.Body = typed + ComposeService.QuoteBody(account, draft.SourceFolder, source, style);
            NoteComposeActivity();
        }

        void QueueActiveDraftSend()
        {
            if (MailUiState.ActiveCompose is not { } draft) return;

            SendWarnings = SendGuardService.Inspect(draft);
            if (SendWarnings.Count > 0) return;

            ForceQueueActiveDraftSend();
        }

        /// <summary>Sends past the pre-send warnings (or straight through when there were none).</summary>
        void ForceQueueActiveDraftSend()
        {
            if (MailUiState.ActiveCompose is not { } draft) return;
            SendWarnings = [];
            draftAutosaveTimer?.Dispose();
            ComposeService.SaveDraft(draft);
            MailUiState.CloseCompose();
            OutboxService.Enqueue(draft);
        }

        static void UndoPendingSend()
        {
            if (OutboxService.Undo() is { } draft)
                MailUiState.OpenCompose(draft);
        }

        /// <summary>Reopens the failed draft for editing and takes the failure bar down.</summary>
        static void EditFailedSend()
        {
            if (OutboxService.LastFailure is not { } failure) return;
            MailUiState.OpenCompose(failure.Draft);
            OutboxService.DismissFailed();
        }

        static int UndoSecondsLeft(PendingSend pending) =>
            Math.Max(0, (int)Math.Ceiling((pending.DueUtc - DateTime.UtcNow).TotalSeconds));

        /// <summary>Ticks the undo bar's countdown once a second while a send is pending.</summary>
        void HandleOutboxChanged()
        {
            if (OutboxService.Current != null && undoCountdownTimer == null)
            {
                undoCountdownTimer = new System.Timers.Timer(1000) { AutoReset = true };
                undoCountdownTimer.Elapsed += (_, _) => InvokeAsync(StateHasChanged);
                undoCountdownTimer.Start();
            }
            else if (OutboxService.Current == null && undoCountdownTimer != null)
            {
                undoCountdownTimer.Dispose();
                undoCountdownTimer = null;
            }
            InvokeAsync(StateHasChanged);
        }

        async Task OnComposeFilesSelectedAsync(InputFileChangeEventArgs e)
        {
            if (MailUiState.ActiveCompose is not { } draft) return;
            try
            {
                foreach (var file in e.GetMultipleFiles(MaxComposeAttachments))
                {
                    await using var source = file.OpenReadStream(MaxComposeAttachmentBytes);
                    await ComposeService.AttachFileAsync(draft, source, file.Name);
                }
                SendError = null;
            }
            catch (IOException)
            {
                SendError = $"A file is too big — the limit is {MaxComposeAttachmentBytes / (1024 * 1024)} MB.";
            }
            SendWarnings = [];
            NoteComposeActivity();
            StateHasChanged();
        }

        void RemoveComposeAttachment(string path)
        {
            if (MailUiState.ActiveCompose is not { } draft) return;
            ComposeService.RemoveAttachment(draft, path);
            NoteComposeActivity();
        }

        static void StartCompose()
        {
            if (MailUiState.SelectedAccount is { } account)
                MailUiState.OpenCompose(ComposeService.BuildNew(account));
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
            SendWarnings = [];
            draftAutosaveTimer?.Dispose();
            if (MailUiState.ActiveCompose is { } draft)
                ComposeService.DeleteDraft(draft);
            DraftSavedAt = null;
            MailUiState.CloseCompose();
        }
    }
}
