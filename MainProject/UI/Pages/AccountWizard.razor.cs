using MyLovelyMail.MainProject.Constants;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Stores;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;
using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail.MainProject.UI.Pages
{
    public partial class AccountWizard
    {
        public const string RoutePath = "/Settings/AddAccount";

        /// <summary>Swatches offered in the wizard; the preselected one rotates so each new account differs.</summary>
        internal static string[] AccountColors => AppColors.IdentityPalette;

        string SelectedColorHex { get; set; } = AccountColors[AccountStore.Accounts.Count % AccountColors.Length];

        ProviderPreset? SelectedPreset { get; set; }
        bool presetPickedManually;

        string Email { get; set; } = string.Empty;
        string DisplayName { get; set; } = string.Empty;
        string Password { get; set; } = string.Empty;
        string Username { get; set; } = string.Empty;

        IncomingProtocol Protocol { get; set; } = IncomingProtocol.Imap;
        string IncomingHost { get; set; } = string.Empty;
        string IncomingPortText { get; set; } = "993";
        string Pop3FetchLimitText { get; set; } = GlobalSettings.Pop3FetchLimit.Value.ToString();

        /// <summary>UI face of "limit = 0": on disables the number field and downloads everything.</summary>
        bool Pop3FetchEverything { get; set; }
        ConnectionSecurity IncomingSecurity { get; set; } = ConnectionSecurity.SslOnConnect;
        string SmtpHost { get; set; } = string.Empty;
        string SmtpPortText { get; set; } = "465";
        ConnectionSecurity SmtpSecurity { get; set; } = ConnectionSecurity.SslOnConnect;

        bool Busy { get; set; }
        string BusyAction { get; set; } = string.Empty;
        string? TestMessage { get; set; }
        bool TestSucceeded { get; set; }

        bool FormLooksComplete =>
            Email.Contains('@') && Password.Length > 0 &&
            IncomingHost.Length > 0 && SmtpHost.Length > 0;

        void OnEmailChanged(string value)
        {
            Email = value;
            if (!presetPickedManually && ProviderPresets.GuessFromEmail(value) is { } guessed)
                ApplyPreset(guessed);
        }

        void PickPreset(ProviderPreset preset)
        {
            presetPickedManually = true;
            ApplyPreset(preset);
        }

        void PickProtocol(IncomingProtocol protocol)
        {
            Protocol = protocol;
            if (SelectedPreset != null)
                ApplyPreset(SelectedPreset);
        }

        void ApplyPreset(ProviderPreset preset)
        {
            SelectedPreset = preset;
            if (Protocol == IncomingProtocol.Imap)
            {
                IncomingHost = preset.ImapHost;
                IncomingPortText = preset.ImapPort.ToString();
                IncomingSecurity = preset.ImapSecurity;
            }
            else
            {
                IncomingHost = preset.Pop3Host;
                IncomingPortText = preset.Pop3Port.ToString();
                IncomingSecurity = preset.Pop3Security;
            }
            SmtpHost = preset.SmtpHost;
            SmtpPortText = preset.SmtpPort.ToString();
            SmtpSecurity = preset.SmtpSecurity;
        }

        MailAccountData BuildAccount() => new()
        {
            DisplayName = DisplayName.Trim(),
            EmailAddress = Email.Trim(),
            Protocol = Protocol,
            IncomingHost = IncomingHost.Trim(),
            IncomingPort = int.TryParse(IncomingPortText, out int incomingPort) ? incomingPort : 993,
            IncomingSecurity = IncomingSecurity,
            IncomingUsername = string.IsNullOrWhiteSpace(Username) ? Email.Trim() : Username.Trim(),
            SmtpHost = SmtpHost.Trim(),
            SmtpPort = int.TryParse(SmtpPortText, out int smtpPort) ? smtpPort : 465,
            SmtpSecurity = SmtpSecurity,
            ColorHex = SelectedColorHex
        };

        async Task TestConnectionAsync()
        {
            Busy = true;
            BusyAction = "test";
            TestMessage = null;
            StateHasChanged();

            var account = BuildAccount();
            try
            {
                await Services.ResiliencePolicy.RunNetwork(async ct =>
                {
                    if (account.Protocol == IncomingProtocol.Imap)
                    {
                        using var imap = await MailConnections.OpenImapAsync(account, ct, Password);
                        await imap.DisconnectAsync(true, ct);
                    }
                    else
                    {
                        using var pop3 = await MailConnections.OpenPop3Async(account, ct, Password);
                        await pop3.DisconnectAsync(true, ct);
                    }
                    using var smtp = await MailConnections.OpenSmtpAsync(account, ct, Password);
                    await smtp.DisconnectAsync(true, ct);
                });
                TestSucceeded = true;
                TestMessage = "✅ Connected! Both receiving and sending servers accepted the credentials.";
            }
            catch (Exception ex)
            {
                TestSucceeded = false;
                TestMessage = $"❌ {ex.Message}";
            }
            finally
            {
                Busy = false;
                BusyAction = string.Empty;
            }
        }

        async Task SaveAsync()
        {
            if (!CredentialVault.IsUnlocked)
            {
                TestSucceeded = false;
                TestMessage = "❌ The credential vault is locked. Unlock it in Settings first.";
                return;
            }

            Busy = true;
            BusyAction = "save";
            StateHasChanged();

            var account = BuildAccount();
            CredentialVault.SetPassword(account.Id, Password);
            AccountStore.Save(account);

            if (Protocol == IncomingProtocol.Pop3)
            {
                int fetchLimit = Pop3FetchEverything ? 0
                    : int.TryParse(Pop3FetchLimitText, out int parsed) && parsed > 0 ? parsed : GlobalSettings.Pop3FetchLimit.Value;
                var accountSettings = AccountStore.GetSettings(account.Id);
                if (accountSettings.Pop3FetchLimit.Value != fetchLimit)
                {
                    accountSettings.Pop3FetchLimit.Value = fetchLimit;
                    accountSettings.Save();
                }
            }

            // First sync runs in the background; the mail screen fills in as results land.
            _ = SyncScheduler.SyncNowAsync();

            Busy = false;
            Navigation.NavigateTo(NavigationConstants.Mail.Link);
            await Task.CompletedTask;
        }
    }
}
