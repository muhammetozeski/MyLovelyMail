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

namespace MyLovelyMail.MainProject.Diagnostics;

public static partial class DebugApi
{
    /// <summary>Handles the settings endpoints using the application's existing services.</summary>
    /// <param name="request">The accepted local request, including its method and body.</param>
    /// <param name="query">Decoded query values shared with the route dispatcher.</param>
    /// <returns>The response object serialized by the listener.</returns>
    static async Task<object?> HandleSettingsAsync(HttpListenerRequest request, NameValueCollection query)
    {
        switch (request.HttpMethod, request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant())
        {

                case ("POST", "/reader-scale"):
                {
                    GlobalSettings.ReaderTextScalePercent.Value = int.Parse(RequireQueryValue(query, "percent"));
                    SettingsManager.SaveSettings();
                    return new { GlobalSettings.ReaderTextScalePercent.Value };
                }

                case ("GET", "/motion"):
                    return new
                    {
                        reduceMotion = GlobalSettings.ReduceMotion.Value,
                        followSystem = GlobalSettings.FollowSystemMotion.Value,
                        systemReduced = MotionPreference.SystemPrefersReduced,
                        effective = MotionPreference.IsCalm,
                        css = AppStyles.BuildCalmMotionLayer()
                    };

                // ?reduce= sets the app switch and stops following the system, since the switch
                // means nothing while the OS is the source of the answer; ?follow= sets that
                // choice on its own.
                case ("POST", "/motion"):
                {
                    if (query["follow"] is { Length: > 0 } follow)
                        GlobalSettings.FollowSystemMotion.Value = follow == "true";
                    if (query["reduce"] is { Length: > 0 } reduce)
                    {
                        GlobalSettings.FollowSystemMotion.Value = false;
                        GlobalSettings.ReduceMotion.Value = reduce == "true";
                    }
                    SettingsManager.SaveSettings();
                    return new { GlobalSettings.ReduceMotion.Value, followSystem = GlobalSettings.FollowSystemMotion.Value, effective = MotionPreference.IsCalm, css = AppStyles.BuildCalmMotionLayer() };
                }

                // Every registered setting with its value and whether it still follows the default.
                // ?accountId= switches to that account's inherited set. This is what makes "does a
                // control exist for every key" a checkable question instead of an eyeballed one.
                case ("GET", "/settings"):
                {
                    var all = query["accountId"] is { Length: > 0 } settingsAccount
                        ? AccountStore.GetSettings(settingsAccount).GetAllSettings()
                        : SettingsManager.GetAllSettings();
                    return all
                        .OrderBy(static s => s.Key, StringComparer.Ordinal)
                        .Select(static s => new
                        {
                            s.Key,
                            value = s.Value.ToString(),
                            @default = s.DefaultValue.ToString(),
                            s.IsDefault,
                            type = s.Value.GetType().Name
                        });
                }

                // Writes through ISetting.TrySetFromText — the editor's path, not the file loader's
                // — so a rejected value can be observed as "nothing changed" rather than inferred.
                case ("POST", "/settings/set"):
                {
                    string key = RequireQueryValue(query, "key");
                    string value = RequireQueryValue(query, "value");
                    string? targetAccount = query["accountId"];

                    var owner = targetAccount is { Length: > 0 }
                        ? AccountStore.GetSettings(targetAccount).GetAllSettings()
                        : SettingsManager.GetAllSettings();
                    var setting = owner.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Unknown setting '{key}'.");

                    bool applied = setting.TrySetFromText(value);
                    if (applied)
                    {
                        if (targetAccount is { Length: > 0 }) AccountStore.GetSettings(targetAccount).Save();
                        else SettingsManager.SaveSettings();
                    }
                    return new { setting.Key, applied, value = setting.Value.ToString(), setting.IsDefault };
                }

                // The other half of /settings/set: back to the shipped default globally, back to
                // inheriting on an account. Without it a probe cannot undo the override it made.
                case ("POST", "/settings/reset"):
                {
                    string key = RequireQueryValue(query, "key");
                    string? targetAccount = query["accountId"];

                    var owner = targetAccount is { Length: > 0 }
                        ? AccountStore.GetSettings(targetAccount).GetAllSettings()
                        : SettingsManager.GetAllSettings();
                    var setting = owner.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Unknown setting '{key}'.");

                    setting.ResetToDefault();
                    if (targetAccount is { Length: > 0 }) AccountStore.GetSettings(targetAccount).Save();
                    else SettingsManager.SaveSettings();
                    return new { setting.Key, value = setting.Value.ToString(), setting.IsDefault };
                }

                // Answers "where would this account open right now" without selecting anything, so
                // the restore — including the phantom-folder fallback — is checkable while the app
                // is in use. ?pretend= substitutes a remembered name the server does not have.
                case ("GET", "/start-folder"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    var folders = MessageStore.GetFolders(accountId);
                    string? remembered = FolderMemoryStore.FolderOf(accountId);
                    string? pretend = query["pretend"];
                    var resolved = FolderMemoryStore.ResolveStartFolder(pretend is { Length: > 0 } ? pretend : remembered, folders);
                    return new { remembered, pretended = pretend, opensIn = resolved?.FullName };
                }

                case ("POST", "/accounts"):
                {
                    var body = await JsonSerializer.DeserializeAsync<AddAccountRequest>(request.InputStream, Json)
                        ?? throw new InvalidOperationException("Empty request body.");
                    var account = new MailAccountData
                    {
                        EmailAddress = body.Email,
                        DisplayName = body.DisplayName.Length > 0 ? body.DisplayName : body.Email,
                        Protocol = body.Protocol,
                        IncomingHost = body.IncomingHost,
                        IncomingPort = body.IncomingPort,
                        IncomingSecurity = body.IncomingSecurity,
                        IncomingUsername = body.Username.Length > 0 ? body.Username : body.Email,
                        SmtpHost = body.SmtpHost,
                        SmtpPort = body.SmtpPort,
                        SmtpSecurity = body.SmtpSecurity,
                        TorOnly = body.TorOnly
                    };
                    CredentialVault.SetPassword(account.Id, body.Password);
                    AccountStore.Save(account);
                    _ = SyncScheduler.SyncNowAsync();
                    return new { account.Id };
                }

                case ("POST", "/accounts/order"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    AccountStore.Move(accountId, int.Parse(RequireQueryValue(query, "delta")));
                    return new { order = AccountStore.Accounts.Select(a => new { a.EmailAddress, a.SortOrder }) };
                }

                // Flips the account's Enabled flag through the same store path the settings page
                // uses, so the paused rendering can be checked without opening that page.
                case ("POST", "/accounts/enabled"):
                {
                    var account = RequireAccount(RequireQueryValue(query, "accountId"));
                    account.Enabled = RequireQueryValue(query, "enabled") == "true";
                    AccountStore.Save(account);
                    return new { account.EmailAddress, account.Enabled };
                }

                // The same flip for the Tor-only flag, through the same steps the settings page
                // takes — including dropping the IDLE loop, whose connection was opened under the
                // previous answer and would otherwise keep using it.
                case ("POST", "/accounts/tor-only"):
                {
                    var account = RequireAccount(RequireQueryValue(query, "accountId"));
                    account.TorOnly = RequireQueryValue(query, "torOnly") == "true";
                    AccountStore.Save(account);
                    ImapIdleService.Drop(account.Id);
                    return new { account.EmailAddress, account.TorOnly, idleLoops = ImapIdleService.RunningAccountIds };
                }

                case ("DELETE", "/accounts"):
                {
                    AccountStore.Remove(RequireQueryValue(query, "accountId"));
                    return new { ok = true };
                }

            default:
                throw new InvalidOperationException($"Unknown endpoint: {request.HttpMethod} {request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant()}");
        }
    }
}