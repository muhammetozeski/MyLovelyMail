using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>What the empty-folder panel offers to do about it. None renders no button.</summary>
    public enum EmptyFolderAction
    {
        None,

        /// <summary>The account is paused; the button un-pauses it and the sync resumes.</summary>
        EnableAccount,

        /// <summary>Ask the server for this folder again (<see cref="ImapSyncService.KickFolderSync"/>).</summary>
        Fetch,

        /// <summary>
        /// Throw the folder's cache away and refill it (<see cref="ImapSyncService.KickFolderResync"/>).
        /// NOT a backfill: <see cref="ImapSyncService.BackfillFolderAsync"/> returns 0 when
        /// OldestFetchedUid is not above 1, so a backfill on an empty cache does nothing at all.
        /// </summary>
        Refill
    }

    /// <summary>Why this folder shows nothing, in the words the panel prints.</summary>
    public sealed record FolderEmptyExplanation(string Headline, string Detail, EmptyFolderAction Action, string ActionLabel = "");

    /// <summary>
    /// Names the reason a folder is showing nothing. A folder with no mail in it is the one empty
    /// state a mail client cannot be vague about: "This folder is empty" is a claim about the
    /// SERVER, and the app was printing it for a refused fetch, a paused account and a folder it
    /// had never asked about — three cases where the true answer is "I don't know yet".
    /// <para>
    /// Pure and cache-only: every fact comes from <see cref="MailFolderData"/>,
    /// <see cref="AccountStore"/>, <see cref="SyncHealthService"/> and
    /// <see cref="ImapSyncService.LastFolderFailure"/>, so this never touches the network and is
    /// safe to call from a render.
    /// </para>
    /// </summary>
    public static class FolderEmptyReason
    {
        /// <summary>The unchanged wording, for the case where the folder really is just empty.</summary>
        public const string PlainlyEmpty = "This folder is empty";

        public static FolderEmptyExplanation Explain(MailFolderData folder)
        {
            var account = AccountStore.GetById(folder.AccountId);
            if (account == null)
                return new FolderEmptyExplanation("This account no longer exists",
                    "Its folders are still on screen, but nothing can be fetched for them.", EmptyFolderAction.None);

            // A local folder has no server to disagree with, so none of the branches below apply.
            if (folder.IsLocal)
                return new FolderEmptyExplanation(PlainlyEmpty,
                    "This folder lives on this machine only — nothing is fetched into it.", EmptyFolderAction.None);

            if (!account.Enabled)
                return new FolderEmptyExplanation("This account is paused",
                    "Paused accounts stay configured but are skipped by every sync, so this folder is never fetched.",
                    EmptyFolderAction.EnableAccount, "▶️ Resume this account");

            // The folder's own failure first: it is about THIS folder, while account health may be
            // reporting something the scheduled pass hit in the Inbox half an hour ago.
            if (ImapSyncService.LastFolderFailure(folder.AccountId, folder.FullName) is { } failure)
                return new FolderEmptyExplanation("Last fetch failed",
                    $"{failure.Message} (at {failure.WhenUtc.ToLocalTime():HH:mm})", EmptyFolderAction.Fetch, "🔄 Try again");

            var health = SyncHealthService.For(folder.AccountId);
            if (health.IsFailing)
                return new FolderEmptyExplanation("This account is not syncing",
                    $"{health.LastErrorMessage} — {health.ConsecutiveFailures} failed attempt(s)"
                    + (health.LastErrorUtc is { } when ? $", last at {when.ToLocalTime():HH:mm}" : string.Empty),
                    EmptyFolderAction.Fetch, "🔄 Try again");

            // POP3 has no folders in the protocol at all, so anything but the Inbox can only ever
            // be a local file — the app cannot fill it and should not imply the server might.
            if (account.Protocol == IncomingProtocol.Pop3 && folder.FullName != MessageStore.InboxFullName)
                return new FolderEmptyExplanation("POP3 only ever fills the Inbox",
                    "The protocol has no server folders, so nothing is fetched into this one.", EmptyFolderAction.None);

            if (folder.LastSyncedUtc == null)
                return new FolderEmptyExplanation("Never fetched yet",
                    "This folder has not been asked for since the account was added.", EmptyFolderAction.Fetch, "⬇️ Fetch it now");

            int cached = MessageStore.GetSummaries(folder.AccountId, folder.FullName).Count;
            if (folder.TotalCount > 0 && cached == 0)
                return new FolderEmptyExplanation($"The server has {folder.TotalCount} message(s) here, none are cached",
                    "The last fetch reported a count but landed no mail — refilling asks for the folder from scratch.",
                    EmptyFolderAction.Refill, "♻️ Refill from the server");

            // Genuinely empty, and now it says so with the evidence rather than as an assertion.
            return new FolderEmptyExplanation(PlainlyEmpty,
                $"The server reports 0 message(s) here, checked at {folder.LastSyncedUtc.Value.ToLocalTime():HH:mm}.",
                EmptyFolderAction.None);
        }

        /// <summary>Runs the explanation's action. Kept here so the panel does not have to know which kick each case needs.</summary>
        public static void RunAction(MailFolderData folder, EmptyFolderAction action)
        {
            if (AccountStore.GetById(folder.AccountId) is not { } account) return;

            switch (action)
            {
                case EmptyFolderAction.EnableAccount:
                    account.Enabled = true;
                    AccountStore.Save(account);
                    break;

                case EmptyFolderAction.Fetch:
                    ImapSyncService.KickFolderSync(account, folder.FullName);
                    break;

                case EmptyFolderAction.Refill:
                    ImapSyncService.KickFolderResync(account, folder.FullName);
                    break;
            }
        }
    }
}
