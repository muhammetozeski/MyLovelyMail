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

namespace MyLovelyMail.MainProject.ZTests;

public static partial class DebugApi
{
    /// <summary>Handles the storage endpoints using the application's existing services.</summary>
    /// <param name="request">The accepted local request, including its method and body.</param>
    /// <param name="query">Decoded query values shared with the route dispatcher.</param>
    /// <returns>The response object serialized by the listener.</returns>
    static async Task<object?> HandleStorageAsync(HttpListenerRequest request, NameValueCollection query)
    {
        switch (request.HttpMethod, request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant())
        {

                // ?dry=true measures without deleting, which is the only safe way to check the
                // budget stage against a real cache. GET returns the persisted receipt instead.
                case ("POST", "/trim"):
                    return OfflineCacheTrimmer.TrimAll(query["dry"] == "true");

                case ("GET", "/trim"):
                    return new { lastReport = OfflineCacheTrimmer.LastReport };

                // A fresh scan of the message cache. Read-only by construction, like /rule-preview:
                // it opens files and never writes, moves or deletes one, so it is safe to point at
                // a real cache. GET /store/check?last=true reads the persisted report instead.
                case ("GET", "/store/check") when query["last"] == "true":
                    return new { lastReport = StoreCheckService.LastReport };

                case ("GET", "/store/check"):
                    return StoreCheckService.Run();

                // Rehearses the size stage against a real cache by lending one account a budget for
                // the duration of a dry run, then putting its setting back exactly as it was.
                case ("POST", "/trim-rehearsal"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    int budgetMb = int.Parse(RequireQueryValue(query, "mb"));
                    // Keep-days is lendable too: the AGE stage is the one that reaches mail
                    // filed into a local folder, which the size stage may never get to.
                    var keepDays = AccountStore.GetSettings(accountId).OfflineKeepDays;
                    bool keepWasOverridden = keepDays.IsOverridden;
                    int keepPrevious = keepDays.Value;
                    if (query["keepDays"] is { Length: > 0 } lentDays) keepDays.Value = int.Parse(lentDays);
                    var budget = AccountStore.GetSettings(accountId).OfflineMaxCacheMb;
                    bool wasOverridden = budget.IsOverridden;
                    int previous = budget.Value;
                    budget.Value = budgetMb;
                    try
                    {
                        return OfflineCacheTrimmer.TrimAll(dryRun: true);
                    }
                    finally
                    {
                        if (wasOverridden) budget.Value = previous; else budget.ClearOverride();
                        if (keepWasOverridden) keepDays.Value = keepPrevious; else keepDays.ClearOverride();
                    }
                }

                case ("GET", "/export"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    var account = RequireAccount(accountId);
                    var summary = RequireSummary(accountId, folder, uid);
                    if (!MessageStore.HasFullMessage(accountId, folder, uid))
                        await ImapSyncService.DownloadMessageAsync(account, folder, uid);
                    string exportPath = query["format"] == "html"
                        ? MessageExportService.ExportHtml(account, folder, summary)
                        : MessageExportService.ExportEml(account, folder, summary);
                    return new { path = exportPath };
                }

            default:
                throw new InvalidOperationException($"Unknown endpoint: {request.HttpMethod} {request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant()}");
        }
    }
}