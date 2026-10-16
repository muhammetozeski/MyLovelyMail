using Microsoft.AspNetCore.Components;
using MyLovelyMail.MainProject.Constants;
using MyLovelyMail.MainProject.Constants.ThemeConstants;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Services.Tor;
using MyLovelyMail.MainProject.Stores;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;

namespace MyLovelyMail.MainProject.UI.Pages;

public partial class Settings
{
    protected override void OnInitialized()
    {
        base.OnInitialized();
        CredentialVault.OnVaultStateChanged += HandleVaultChanged;
        AccountStore.OnAccountsChanged += HandleVaultChanged;
        _ = WatchTorBootstrapAsync();
    }

    void HandleVaultChanged() => InvokeAsync(StateHasChanged);

    readonly CancellationTokenSource pageAlive = new();

    /// <summary>
    /// Tor reports its progress on its own schedule and raises no event this page could subscribe
    /// to, so the route line is re-read once a second — and repainted ONLY when the sentence
    /// actually changed, which is a handful of times across a whole bootstrap and never once the
    /// route is up.
    /// </summary>
    async Task WatchTorBootstrapAsync()
    {
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
        string? previous = TorService.BootstrapLine;
        try
        {
            while (await ticker.WaitForNextTickAsync(pageAlive.Token))
            {
                string? current = TorService.BootstrapLine;
                if (current == previous) continue;
                previous = current;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
            // The page went away; nothing to report.
        }
    }

    public void Dispose()
    {
        CredentialVault.OnVaultStateChanged -= HandleVaultChanged;
        AccountStore.OnAccountsChanged -= HandleVaultChanged;
        pageAlive.Cancel();
        pageAlive.Dispose();
    }

    static void SetAndSave<T>(Setting<T> setting, T value)
    {
        setting.Set(value);
        SettingsManager.SaveSettings();
    }

    /// <summary>
    /// Keys the "Everything else" card must NOT render, for one of three reasons: a hand-written
    /// control above already owns them, they are window/search state no one edits by hand, or —
    /// CredentialVaultMode — a plain dropdown would be actively harmful. Written with nameof so a
    /// renamed field breaks the build instead of quietly orphaning an entry here and showing the
    /// setting twice.
    /// </summary>
    static readonly HashSet<string> KeysWithTheirOwnControl =
    [
        // Appearance card
        nameof(GlobalSettings.Theme),
        nameof(GlobalSettings.FollowSystemTheme),
        nameof(GlobalSettings.FollowSystemMotion),
        nameof(GlobalSettings.ReduceMotion),
        nameof(GlobalSettings.UiScalePercent),
        nameof(GlobalSettings.ShowUnreadBadge),
        nameof(GlobalSettings.MessageListDensity),
        // Window & startup card
        nameof(GlobalSettings.CloseToTray),
        nameof(GlobalSettings.StartMinimized),
        nameof(GlobalSettings.StartWithWindows),
        // Notifications, sync, reading, composing, diagnostics cards
        nameof(GlobalSettings.NotifyOnNewMail),
        nameof(GlobalSettings.SyncIntervalMinutes),
        nameof(GlobalSettings.BackgroundFolderRefreshCount),
        nameof(GlobalSettings.UseImapIdle),
        nameof(GlobalSettings.FoldQuotedText),
        nameof(GlobalSettings.ReplyQuoteStyle),
        nameof(GlobalSettings.Signature),
        nameof(GlobalSettings.UndoSendSeconds),
        nameof(GlobalSettings.EnableLogging),
        // Its control is the reader toolbar's A- / A+ / reset, not a row here.
        nameof(GlobalSettings.ReaderTextScalePercent),
        // Remembered window bounds and saved searches: state, not preferences.
        nameof(GlobalSettings.WindowX),
        nameof(GlobalSettings.WindowY),
        nameof(GlobalSettings.WindowWidth),
        nameof(GlobalSettings.WindowHeight),
        nameof(GlobalSettings.SavedSearches),
        // The vault card switches this through CredentialVault, which re-encrypts every stored
        // password on the way. A dropdown would flip the mode without that, losing all of them.
        nameof(GlobalSettings.CredentialVaultMode),
        // Tor card
        nameof(GlobalSettings.TorSocksHost),
        nameof(GlobalSettings.TorSocksPort),
        nameof(GlobalSettings.TorAutoStart),
        nameof(GlobalSettings.TorExecutablePath),
    ];

    static IEnumerable<ISetting> UncardedSettings =>
        SettingsManager.GetAllSettings()
            .Where(static s => !KeysWithTheirOwnControl.Contains(s.Key))
            .OrderBy(static s => s.KeyHumanReadable, StringComparer.CurrentCulture);

    static void SetSyncInterval(string text)
    {
        if (int.TryParse(text, out int minutes) && minutes > 0)
            SetAndSave(GlobalSettings.SyncIntervalMinutes, minutes);
    }

    /// <summary>Last sync outcome of the account, or the error that is keeping it from syncing.</summary>
    static string SyncHealthLine(string accountId)
    {
        var health = SyncHealthService.For(accountId);
        if (health.IsFailing) return $"⚠️ failed {health.ConsecutiveFailures}× — {health.LastErrorMessage}";

        // The route belongs beside the time: "synced ten minutes ago" and "synced ten minutes ago
        // on the third fallback route" are different states of the same mailbox, and only one of
        // them is fine.
        string route = health.LastRoute is { Length: > 0 } last ? $" · {last}" : string.Empty;
        return health.LastSuccessUtc is { } success ? $"synced {success.ToLocalTime():dd MMM HH:mm}{route}" : "not synced yet";
    }

    /// <summary>Switching this on re-picks the palette immediately, so the effect is visible without restarting.</summary>
    static void SetFollowSystemTheme(bool follow)
    {
        SetAndSave(GlobalSettings.FollowSystemTheme, follow);
        ThemeManager.ApplyFromSettings();
    }

    /// <summary>While this is on the manual switch is disabled, so only one of the two can be the answer.</summary>
    static void SetFollowSystemMotion(bool follow) => SetAndSave(GlobalSettings.FollowSystemMotion, follow);

    static void SetReduceMotion(bool reduce) => SetAndSave(GlobalSettings.ReduceMotion, reduce);

    /// <summary>Clamped: a negative count is meaningless and a big one turns every sync into a full-mailbox pass.</summary>
    static void SetBackgroundFolderRefresh(string text)
    {
        const int MaxFolders = 10;
        if (int.TryParse(text, out int parsed))
            SetAndSave(GlobalSettings.BackgroundFolderRefreshCount, Math.Clamp(parsed, 0, MaxFolders));
    }

    /// <summary>Clamped so a typo cannot shrink the window to an unreadable size or blow it up past the screen.</summary>
    static void SetUiScale(string text)
    {
        const int MinScalePercent = 50;
        const int MaxScalePercent = 200;
        if (int.TryParse(text, out int percent))
            SetAndSave(GlobalSettings.UiScalePercent, Math.Clamp(percent, MinScalePercent, MaxScalePercent));
    }

    static void SetUndoSendSeconds(string text)
    {
        if (int.TryParse(text, out int seconds) && seconds >= 0)
            SetAndSave(GlobalSettings.UndoSendSeconds, seconds);
    }

    static readonly string[] DensityOptions = ["Cozy", "Comfortable", "Compact"];

    static void SetLogging(bool enabled)
    {
        SetAndSave(GlobalSettings.EnableLogging, enabled);
        Logger.ActivateLogging = enabled;
    }

    static void SetDensity(string density)
    {
        SetAndSave(GlobalSettings.MessageListDensity, density);
        Events.MainEvents.Trigger("OnStyleChanged");
    }

    #region Cached mail check

    bool StoreCheckRunning { get; set; }

    /// <summary>The last scan in one line. Reads the persisted report, so it survives a restart.</summary>
    static string StoreCheckLine =>
        StoreCheckService.LastReport is not { } report
            ? "Never scanned. Looks for cached mail the app cannot see: folders without their info file, bodies with no index row, zero-byte and leftover files."
            : report.FindingCount == 0
                ? $"Clean — {report.FoldersScanned} folder(s) checked {report.RanAtUtc.ToLocalTime():dd MMM HH:mm}."
                : $"{report.FindingCount} finding(s) in {report.FoldersScanned} folder(s), {report.RanAtUtc.ToLocalTime():dd MMM HH:mm} — "
                  + string.Join(", ", report.CountsByDefect.Select(static c => $"{c.Value}× {c.Key}"))
                  + $". {Bytes(report.InvisibleFolderBytes + report.StrandedFileBytes)} the cache budget never counts.";

    /// <summary>Scaled to the size it actually is: rounding a few hundred bytes to "0.0 MB" reads as "nothing", which is the opposite of the point.</summary>
    static string Bytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB"
        : $"{bytes} bytes";

    /// <summary>
    /// Off the UI thread: the scan is file I/O over the whole cache, and on a large mailbox doing
    /// it inline would freeze the window for as long as it takes.
    /// </summary>
    async Task RunStoreCheck()
    {
        StoreCheckRunning = true;
        StateHasChanged();
        try
        {
            await Task.Run(StoreCheckService.Run);
        }
        catch (Exception ex)
        {
            Log($"Store check failed: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            StoreCheckRunning = false;
        }
    }

    #endregion

    #region Tor

    bool TorBusy { get; set; }
    string? TorMessage { get; set; }

    static List<string> TorOnlyAccounts =>
        [.. AccountStore.Accounts.Where(static a => a.TorOnly).Select(static a => a.EmailAddress)];

    static string TorExecutableLine =>
        TorProcess.Find() is { } found ? $"Found via {found.FoundBy}: {found.Path}" : "None found yet.";

    /// <summary>Where the app carries its own tor, that tor comes first and a running Orbot is the fallback; elsewhere a running tor comes first.</summary>
    static string TorAutoStartHint =>
        TorProcess.BundledExecutablePath != null
            ? "Runs the tor inside the app on a free port, before any tor that is already running such as Orbot. Off leaves Tor-only accounts to a tor that is already running."
            : "Runs a tor of the app's own on a free port. Off means a Tor-only account waits until you start one yourself.";

    /// <summary>The setting stores separator-joined lines; the box shows real ones.</summary>
    static string BridgeText =>
        GlobalSettings.TorBridgeLines.Value.Replace(TorBridges.LineSeparator, '\n');

    static void SetBridges(string text)
    {
        SetAndSave(GlobalSettings.TorBridgeLines,
            string.Join(TorBridges.LineSeparator,
                text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
    }

    /// <summary>Whether each configured transport has a plugin, said before the start rather than after it fails.</summary>
    static string TorBridgeLine
    {
        get
        {
            var bridges = TorBridges.Configured;
            if (bridges.Count == 0) return string.Empty;

            var problems = ((IEnumerable<dynamic>)TorBridges.Describe(TorProcess.Find()?.Path))
                .Where(entry => entry.problem != null).ToList();

            return problems.Count == 0
                ? $"✅ {bridges.Count} bridge line(s); every transport has its plugin."
                : "⚠️ " + string.Join(" ", problems.Select(entry => (string)entry.problem));
        }
    }

    static string TorRouteLine =>
        TorService.BootstrapLine is { } starting ? starting
            : TorService.Current is { } endpoint
            ? $"In use: {endpoint}." + (TorService.LastVerification is { IsTor: true } ? " Verified as Tor." : string.Empty)
            : TorService.LastError is { Length: > 0 } error ? $"No route. Last attempt: {error}"
            : "No route open yet — one is found or started on the first connection of a Tor-only account.";

    static void SetTorHost(string host) => SetAndSave(GlobalSettings.TorSocksHost, host.Trim());

    static void SetTorPort(string text)
    {
        if (int.TryParse(text, out int port) && port is > 0 and < 65536)
            SetAndSave(GlobalSettings.TorSocksPort, port);
    }

    static void SetTorExecutable(string path) => SetAndSave(GlobalSettings.TorExecutablePath, path.Trim().Trim('"'));

    /// <summary>
    /// The real check: a Tor RESOLVE over the SOCKS port, which a general-purpose SOCKS5 proxy has
    /// to reject. "Something is listening on 9050" is not the same claim and is not worth printing.
    /// </summary>
    async Task CheckTorAsync()
    {
        TorBusy = true;
        TorMessage = "Building a circuit…";
        StateHasChanged();
        try
        {
            var verification = await TorService.VerifyAsync(CancellationToken.None);
            TorMessage = (verification.IsTor ? "✅ " : "❌ ") + verification.Detail;
        }
        catch (Exception ex)
        {
            TorMessage = $"❌ {ex.Message}";
        }
        finally
        {
            TorBusy = false;
        }
    }

    void StopTor()
    {
        TorProcess.Stop();
        TorMessage = "🛑 The tor this app started was stopped. The next Tor-only connection starts one again.";
    }

    #endregion

    string NewMasterPassword { get; set; } = string.Empty;
    string BundlePath { get; set; } = string.Empty;
    string BundlePassphrase { get; set; } = string.Empty;
    string? MigrationStatus { get; set; }

    void ExportBundle()
    {
        try
        {
            string path = MigrationService.ExportBundle(BundlePath.Trim(), BundlePassphrase);
            MigrationStatus = $"✅ Exported to {path}";
        }
        catch (Exception ex)
        {
            MigrationStatus = $"❌ {ex.Message}";
        }
    }

    void ImportBundle()
    {
        try
        {
            bool vaultMerged = MigrationService.ImportBundle(BundlePath.Trim(), BundlePassphrase);
            MigrationStatus = vaultMerged
                ? "✅ Bundle imported — accounts, settings and passwords restored."
                : "⚠️ Files imported, but the passphrase did not open the password vault (re-enter account passwords or retry).";
        }
        catch (Exception ex)
        {
            MigrationStatus = $"❌ {ex.Message}";
        }
    }

    void SwitchVaultToMaster()
    {
        CredentialVault.SwitchToMasterPassword(NewMasterPassword);
        NewMasterPassword = string.Empty;
    }

    static void SwitchVaultToDeviceBound() => CredentialVault.SwitchToDeviceBound();

    void RemoveAccount(string accountId)
    {
        CredentialVault.RemovePassword(accountId);
        AccountStore.Remove(accountId);
    }

    void ApplyTheme(AppTheme theme)
    {
        GlobalSettings.Theme.Set(theme.Name);
        SettingsManager.SaveSettings();
        ThemeManager.Apply(theme);
    }

    static string ThemePreviewStyle(AppTheme theme) =>
        $"background:linear-gradient(160deg, {theme.BackgroundBase.ToRgbaHex(true)}, {theme.BackgroundDeep.ToRgbaHex(true)});" +
        $"border-color:{(ThemeManager.Current == theme ? AppColors.Primary.ToRgbaHex(true) : AppColors.GlassBorderDefault.ToRgbaHex(true))};";
}
