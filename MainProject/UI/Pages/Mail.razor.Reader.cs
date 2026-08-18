using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;

namespace MyLovelyMail.MainProject.UI.Pages
{
    // Reader pane: open-message body, attachments, tags and single-message actions.
    public partial class Mail
    {
        string? OpenBodyHtml { get; set; }
        bool OpenBodyLoading { get; set; }
        bool OpenBodyBlockedImages { get; set; }
        string? BodyError { get; set; }
        uint loadedBodyUid;
        uint remoteImagesAllowedUid;

        List<AttachmentInfo> OpenAttachments { get; set; } = [];
        string? SaveStatus { get; set; }
        bool ShowTagPicker { get; set; }
        string NewTagName { get; set; } = string.Empty;

        /// <summary>Re-renders the open message with remote images allowed for THIS message only.</summary>
        void AllowImagesOnce()
        {
            if (MailUiState.OpenMessage is not { } open) return;
            remoteImagesAllowedUid = open.Uid;
            loadedBodyUid = 0;
            _ = LoadOpenBodyIfNeededAsync();
        }

        /// <summary>
        /// Renders the opened message's cached body; downloads it first when missing. A uid guard
        /// drops stale results when the user opens another message mid-download.
        /// </summary>
        async Task LoadOpenBodyIfNeededAsync()
        {
            var account = MailUiState.SelectedAccount;
            var open = MailUiState.OpenMessage;
            if (account == null || open == null)
            {
                OpenBodyHtml = null;
                loadedBodyUid = 0;
                return;
            }
            if (loadedBodyUid == open.Uid && (OpenBodyHtml != null || OpenBodyLoading)) return;
            if (ResolveFolderOf(open) is not { } folderName) return;

            loadedBodyUid = open.Uid;
            OpenBodyHtml = null;
            BodyError = null;
            OpenBodyLoading = true;
            await InvokeAsync(StateHasChanged);

            bool allowRemote = open.Uid == remoteImagesAllowedUid;
            var rendered = MailBodyRenderer.Render(account, folderName, open, allowRemote);
            if (rendered == null)
            {
                try
                {
                    if (account.Protocol == IncomingProtocol.Imap && !folderName.StartsWith(MessageStore.LocalFolderPrefix))
                        await ImapSyncService.DownloadMessageAsync(account, folderName, open.Uid);
                    else if (account.Protocol == IncomingProtocol.Pop3)
                        await Pop3Service.DownloadMessageAsync(account, open.Uid);
                    rendered = MailBodyRenderer.Render(account, folderName, open, allowRemote);
                }
                catch (Exception ex)
                {
                    BodyError = ex.Message;
                }
            }

            if (MailUiState.OpenMessage?.Uid != open.Uid) return;

            OpenBodyHtml = rendered?.Html;
            OpenBodyBlockedImages = rendered?.RemoteImagesBlocked ?? false;
            OpenBodyLoading = false;
            OpenAttachments = open.HasAttachments ? AttachmentService.List(account, folderName, open) : [];
            MarkOpenAsRead(account, folderName, open);
            await InvokeAsync(StateHasChanged);
        }

        /// <summary>Applies the opened-equals-read behavior, honoring the configured delay.</summary>
        static void MarkOpenAsRead(MailAccountData account, string folderName, MailMessageSummary open)
        {
            int delaySeconds = GlobalSettings.MarkAsReadDelaySeconds.Value;
            if (delaySeconds <= 0)
            {
                MessageActions.MarkRead(account, folderName, open);
                return;
            }
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                if (MailUiState.OpenMessage?.Uid == open.Uid)
                    MessageActions.MarkRead(account, folderName, open);
            });
        }

        /// <summary>Shared guard of the single-message actions: runs the action with the selected
        /// account and the message's real folder, or does nothing when either is missing.</summary>
        void WithAccountAndFolder(MailMessageSummary message, Action<MailAccountData, string> action)
        {
            if (MailUiState.SelectedAccount is { } account && ResolveFolderOf(message) is { } folderName)
                action(account, folderName);
        }

        void ToggleTagOnOpen(MailMessageSummary message, string tagName) =>
            WithAccountAndFolder(message, (account, folderName) => MessageActions.ToggleTag(account, folderName, message, tagName));

        void AddNewTag(MailMessageSummary message)
        {
            string tagName = NewTagName.Trim();
            if (tagName.Length == 0) return;
            TagStore.ColorOf(tagName); // registers the new tag's chip color before first render
            ToggleTagOnOpen(message, tagName);
            NewTagName = string.Empty;
        }

        /// <summary>Guard-plus-status shell of the save/export buttons: resolves the message's account
        /// and folder, runs the save, and puts the returned path (or the failure) into <see cref="SaveStatus"/>.</summary>
        async Task ReportSaveAsync(MailMessageSummary message, Func<MailAccountData, string, Task<string>> save, string successText = "Saved to")
        {
            if (MailUiState.SelectedAccount is not { } account || ResolveFolderOf(message) is not { } folderName) return;
            try
            {
                SaveStatus = $"💾 {successText} {await save(account, folderName)}";
            }
            catch (Exception ex)
            {
                SaveStatus = $"❌ {ex.Message}";
            }
        }

        async Task SaveAttachmentAsync(int attachmentIndex)
        {
            if (MailUiState.OpenMessage is { } open)
                await ReportSaveAsync(open, (account, folderName) => AttachmentService.SaveAsync(account, folderName, open, attachmentIndex));
        }

        async Task SaveAllAttachmentsAsync()
        {
            if (MailUiState.OpenMessage is { } open)
                await ReportSaveAsync(open, (account, folderName) => AttachmentService.SaveAllAsync(account, folderName, open), "All attachments saved to");
        }

        void StartReply(MailMessageSummary message, bool replyAll) =>
            WithAccountAndFolder(message, (account, folderName) =>
                MailUiState.OpenCompose(ComposeService.BuildReply(account, folderName, message, replyAll)));

        void StartForward(MailMessageSummary message) =>
            WithAccountAndFolder(message, (account, folderName) =>
                MailUiState.OpenCompose(ComposeService.BuildForward(account, folderName, message)));

        void ToggleOpenFlagged(MailMessageSummary message) =>
            WithAccountAndFolder(message, (account, folderName) => MessageActions.ToggleFlagged(account, folderName, message));

        void ToggleOpenImportant(MailMessageSummary message) =>
            WithAccountAndFolder(message, (account, folderName) => MessageActions.ToggleImportant(account, folderName, message));

        /// <summary>Saves the open message to Downloads as .eml (raw MIME) or .html (rendered snapshot).</summary>
        Task ExportOpen(MailMessageSummary message, bool asHtml) =>
            ReportSaveAsync(message, (account, folderName) => Task.FromResult(asHtml
                ? MessageExportService.ExportHtml(account, folderName, message)
                : MessageExportService.ExportEml(account, folderName, message)));

        void DeleteOpen(MailMessageSummary message) =>
            WithAccountAndFolder(message, (account, folderName) =>
            {
                MessageActions.Delete(account, folderName, message);
                MailUiState.CloseMessage();
            });

        void OpenMessage(MailMessageSummary message)
        {
            // A click while composing looked dead: the compose pane owns the right side, so the
            // reader never appeared. Autosave the draft and close compose, then open the message.
            if (MailUiState.ActiveCompose != null)
                SaveAndCloseCompose();
            MailUiState.FocusMessage(message);
            MailUiState.OpenMessageInReader(message);
        }

        /// <summary>Rows in the local Drafts folder reopen in the compose pane instead of the reader.</summary>
        void OpenMessageOrDraft(MailMessageSummary message)
        {
            if (MailUiState.SelectedAccount is { } account
                && MailUiState.SelectedFolder is { IsLocal: true, Role: FolderRole.Drafts }
                && ComposeService.LoadDraft(account, message) is { } draft)
            {
                MailUiState.OpenCompose(draft);
                return;
            }
            OpenMessage(message);
        }
    }
}
