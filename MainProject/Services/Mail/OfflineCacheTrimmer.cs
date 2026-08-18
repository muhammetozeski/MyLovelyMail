using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Deletes the cached .eml bodies of messages older than
    /// <see cref="AccountSettings.OfflineKeepDays"/>, keeping every summary (the list stays
    /// complete — an old message simply downloads its body again when opened). 0 days = keep
    /// everything, which is the default. Runs after a sync pass, never on the UI path.
    /// </summary>
    public static class OfflineCacheTrimmer
    {
        public static void TrimAll()
        {
            foreach (var account in AccountStore.Accounts)
            {
                int keepDays = AccountStore.GetSettings(account.Id).OfflineKeepDays.Value;
                if (keepDays <= 0) continue;

                var cutoffUtc = DateTime.UtcNow.AddDays(-keepDays);
                int removed = 0;
                foreach (var folder in MessageStore.GetFolders(account.Id))
                {
                    foreach (var summary in MessageStore.GetSummaries(account.Id, folder.FullName))
                    {
                        if (summary.DateUtc >= cutoffUtc) continue;
                        if (MessageStore.DeleteCachedBody(account.Id, folder.FullName, summary.Uid)) removed++;
                    }
                }
                if (removed > 0)
                    Log($"Offline trim: dropped {removed} cached bodies older than {keepDays} days for {account.EmailAddress}.");
            }
        }
    }
}
