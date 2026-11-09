using System.Globalization;
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
        // Fine-tuning card
        nameof(GlobalSettings.ScrollbarWidthPx),
        nameof(GlobalSettings.ScrollbarColorHex),
        nameof(GlobalSettings.SidebarColumnPercent),
        nameof(GlobalSettings.ListColumnPercent),
        nameof(GlobalSettings.ReaderColumnPercent),
        nameof(GlobalSettings.PinnedReaderActions),
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
        nameof(GlobalSettings.ReaderTextScalePercent),
        nameof(GlobalSettings.WindowX),
        nameof(GlobalSettings.WindowY),
        nameof(GlobalSettings.WindowWidth),
        nameof(GlobalSettings.WindowHeight),
        nameof(GlobalSettings.SavedSearches),
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

    #region Fine-Tuning (Scrollbar & Column Widths)

    static readonly int[] ScrollbarWidthPresets = [4, 6, 8, 10];

    static readonly (string Name, string Hex)[] ScrollbarColorPresets =
    [
        ("Bright Light Green (Default)", "#4ADE80"),
        ("Emerald Mint", "#34D399"),
        ("Sky Cyan", "#38BDF8"),
        ("Soft Violet", "#A78BFA"),
        ("Sakura Rose", "#F472B6"),
        ("Warm Amber", "#FBBF24")
    ];

    static bool AreColumnWidthsDefault =>
        Math.Abs(GlobalSettings.SidebarColumnPercent.Value - GlobalSettings.DefaultSidebarColumnPercent) < 0.01 &&
        Math.Abs(GlobalSettings.ListColumnPercent.Value - GlobalSettings.DefaultListColumnPercent) < 0.01 &&
        Math.Abs(GlobalSettings.ReaderColumnPercent.Value - GlobalSettings.DefaultReaderColumnPercent) < 0.01;

    static void SetScrollbarWidth(string text)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int px))
        {
            SetAndSave(GlobalSettings.ScrollbarWidthPx, Math.Clamp(px, 3, 18));
            Events.MainEvents.Trigger("OnStyleChanged");
        }
    }

    static void ResetScrollbarWidthDefault()
    {
        SetAndSave(GlobalSettings.ScrollbarWidthPx, GlobalSettings.DefaultScrollbarWidthPx);
        Events.MainEvents.Trigger("OnStyleChanged");
    }

    static void SetScrollbarColor(string hex)
    {
        string trimmed = (hex ?? string.Empty).Trim();
        if (!trimmed.StartsWith('#')) trimmed = "#" + trimmed;
        if (trimmed.Length is 7 or 9)
        {
            SetAndSave(GlobalSettings.ScrollbarColorHex, trimmed.ToUpperInvariant());
            Events.MainEvents.Trigger("OnStyleChanged");
        }
    }

    static void ResetScrollbarColorDefault()
    {
        SetAndSave(GlobalSettings.ScrollbarColorHex, GlobalSettings.DefaultScrollbarColorHex);
        Events.MainEvents.Trigger("OnStyleChanged");
    }

    static void SetSingleColumnPercent(int index, string text)
    {
        string normalized = (text ?? string.Empty).Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return;

        double s = GlobalSettings.SidebarColumnPercent.Value;
        double l = GlobalSettings.ListColumnPercent.Value;
        double r = GlobalSettings.ReaderColumnPercent.Value;

        if (index == 0)
        {
            s = Math.Clamp(value, 12.0, 45.0);
            r = Math.Max(24.0, 100.0 - s - l);
            l = 100.0 - s - r;
        }
        else if (index == 1)
        {
            l = Math.Clamp(value, 18.0, 58.0);
            r = Math.Max(24.0, 100.0 - s - l);
            s = Math.Clamp(100.0 - l - r, 12.0, 45.0);
        }
        else
        {
            r = Math.Clamp(value, 24.0, 68.0);
            l = Math.Max(18.0, 100.0 - s - r);
            s = Math.Clamp(100.0 - l - r, 12.0, 45.0);
        }

        GlobalSettings.SidebarColumnPercent.Set(s);
        GlobalSettings.ListColumnPercent.Set(l);
        GlobalSettings.ReaderColumnPercent.Set(r);
        SettingsManager.SaveSettings();
        Events.MainEvents.Trigger("OnStyleChanged");
    }

    static void ResetColumnWidthDefaults()
    {
        GlobalSettings.SidebarColumnPercent.Set(GlobalSettings.DefaultSidebarColumnPercent);
        GlobalSettings.ListColumnPercent.Set(GlobalSettings.DefaultListColumnPercent);
        GlobalSettings.ReaderColumnPercent.Set(GlobalSettings.DefaultReaderColumnPercent);
        SettingsManager.SaveSettings();
        Events.MainEvents.Trigger("OnStyleChanged");
    }

    static void ResetAllFineTuningDefaults()
    {
        GlobalSettings.ScrollbarWidthPx.Set(GlobalSettings.DefaultScrollbarWidthPx);
        GlobalSettings.ScrollbarColorHex.Set(GlobalSettings.DefaultScrollbarColorHex);
        GlobalSettings.SidebarColumnPercent.Set(GlobalSettings.DefaultSidebarColumnPercent);
        GlobalSettings.ListColumnPercent.Set(GlobalSettings.DefaultListColumnPercent);
        GlobalSettings.ReaderColumnPercent.Set(GlobalSettings.DefaultReaderColumnPercent);
        SettingsManager.SaveSettings();
        Events.MainEvents.Trigger("OnStyleChanged");
    }

    #endregion

    static void SetSyncInterval(string text)
    {
        if (int.TryParse(text, out int minutes) && minutes > 0)
            SetAndSave(GlobalSettings.SyncIntervalMinutes, minutes);
    }

    static string SyncHealthLine(string accountId)
    {
        var health = SyncHealthService.For(accountId);
        if (health.IsFailing) return $"⚠️ failed {health.ConsecutiveFailures}× — {health.LastErrorMessage}";

        string route = health.LastRoute is { Length: > 0 } last ? $" · {last}" : string.Empty;
        return health.LastSuccessUtc is { } success ? $"synced {success.ToLocalTime():dd MMM HH:mm}{route}" : "not synced yet";
    }

    static void SetFollowSystemTheme(bool follow)
    {
        SetAndSave(GlobalSettings.FollowSystemTheme, follow);
        ThemeManager.ApplyFromSettings();
    }

    static void SetFollowSystemMotion(bool follow) => SetAndSave(GlobalSettings.FollowSystemMotion, follow);

    static void SetReduceMotion(bool reduce) => SetAndSave(GlobalSettings.ReduceMotion, reduce);

    static void SetBackgroundFolderRefresh(string text)
    {
        const int MaxFolders = 10;
        if (int.TryParse(text, out int parsed))
            SetAndSave(GlobalSettings.BackgroundFolderRefreshCount, Math.Clamp(parsed, 0, MaxFolders));
    }

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

    static string StoreCheckLine =>
        StoreCheckService.LastReport is not { } report
            ? "Never scanned. Looks for cached mail the app cannot see: folders without their info file, bodies with no index row, zero-byte and leftover files."
            : report.FindingCount == 0
                ? $"Clean — {report.FoldersScanned} folder(s) checked {report.RanAtUtc.ToLocalTime():dd MMM HH:mm}."
                : $"{report.FindingCount} finding(s) in {report.FoldersScanned} folder(s), {report.RanAtUtc.ToLocalTime():dd MMM HH:mm} — "
                  + string.Join(", ", report.CountsByDefect.Select(static c => $"{c.Value}× {c.Key}"))
                  + $". {Bytes(report.InvisibleFolderBytes + report.StrandedFileBytes)} the cache budget never counts.";

    static string Bytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB"
        : $"{bytes} bytes";

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

    static string TorAutoStartHint =>
        TorProcess.BundledExecutablePath != null
            ? "Runs the tor inside the app on a free port, before any tor that is already running such as Orbot. Off leaves Tor-only accounts to a tor that is already running."
            : "Runs a tor of the app's own on a free port. Off means a Tor-only account waits until you start one yourself.";

    static string BridgeText =>
        GlobalSettings.TorBridgeLines.Value.Replace(TorBridges.LineSeparator, '\n');

    static void SetBridges(string text)
    {
        SetAndSave(GlobalSettings.TorBridgeLines,
            string.Join(TorBridges.LineSeparator,
                text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
    }

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

    async Task RemoveAccount(string accountId)
    {
        var account = AccountStore.GetById(accountId);
        if (account == null) return;
        bool confirmed = await ModalService.ConfirmAsync(
            "🗑️ Remove Account",
            $"Remove \"{account.EmailAddress}\" from this device?",
            confirmLabel: "🗑️ Remove",
            isDanger: true);
        if (!confirmed) return;

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
