using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>One thing that does not add up about who sent a message. <paramref name="Code"/> is what tests assert on; <paramref name="Text"/> is what the user reads.</summary>
    public readonly record struct AuthFinding(string Code, string Text);

    /// <summary>
    /// Reads the authentication verdicts a receiving server already stamped into the message and
    /// says only what is WRONG. The reader printed the display name and address with no scrutiny,
    /// while every cached .eml carried Authentication-Results, Return-Path and Reply-To that
    /// nothing had ever looked at.
    /// <para>
    /// There is deliberately no "pass" chip. A dkim=pass line can be prepended by anyone, so a
    /// green badge would launder a forged header — worse than showing nothing. A clean message
    /// shows no strip at all.
    /// </para>
    /// </summary>
    public static class MessageAuthService
    {
        public const string SpfFailCode = "spf-fail";
        public const string DkimFailCode = "dkim-fail";
        public const string DmarcFailCode = "dmarc-fail";
        public const string UnauthenticatedCode = "unauthenticated";
        public const string ReturnPathMismatchCode = "return-path-mismatch";
        public const string ReplyToElsewhereCode = "reply-to-elsewhere";
        public const string NameClaimsAddressCode = "name-claims-address";

        const string AuthenticationResultsHeader = "Authentication-Results";
        const string ReceivedSpfHeader = "Received-SPF";

        /// <summary>
        /// What is wrong with this message's provenance, or an empty list when there is nothing to
        /// say. Pure: takes the parsed MIME and touches nothing else.
        /// </summary>
        public static IReadOnlyList<AuthFinding> Inspect(MimeMessage message)
        {
            List<AuthFinding> findings = [];

            // ONLY the topmost of each header: everything below it travelled with the message and
            // can say whatever the sender wanted it to say.
            string? authResults = TopmostHeader(message, AuthenticationResultsHeader);
            string? receivedSpf = TopmostHeader(message, ReceivedSpfHeader);

            if (authResults == null && receivedSpf == null)
                findings.Add(new AuthFinding(UnauthenticatedCode, "No server checked where this came from."));
            else
            {
                string verdicts = $"{authResults} {receivedSpf}".ToLowerInvariant();
                if (verdicts.Contains("spf=fail") || verdicts.Contains("spf=softfail") || verdicts.Contains("fail (") )
                    findings.Add(new AuthFinding(SpfFailCode, "The sending server is not authorised for this domain (SPF)."));
                if (verdicts.Contains("dkim=fail"))
                    findings.Add(new AuthFinding(DkimFailCode, "The signature does not match the message (DKIM)."));
                if (verdicts.Contains("dmarc=fail"))
                    findings.Add(new AuthFinding(DmarcFailCode, "The domain's own policy rejects this message (DMARC)."));
            }

            var from = message.From.Mailboxes.FirstOrDefault();
            if (from != null)
            {
                string fromDomain = DomainOf(from.Address);

                if (AddressOf(message.Headers[HeaderId.ReturnPath]) is { Length: > 0 } returnPath
                    && !DomainsAlign(DomainOf(returnPath), fromDomain))
                    findings.Add(new AuthFinding(ReturnPathMismatchCode, $"Bounces go to {returnPath}, not to {from.Address}."));

                if (message.ReplyTo.Mailboxes.FirstOrDefault() is { } replyTo
                    && !DomainsAlign(DomainOf(replyTo.Address), fromDomain))
                    findings.Add(new AuthFinding(ReplyToElsewhereCode, $"A reply would go to {replyTo.Address}."));

                // Exact and unarguable: the display name spells out an address at another domain.
                if (AddressInsideName(from.Name) is { } claimed && !DomainsAlign(DomainOf(claimed), fromDomain))
                    findings.Add(new AuthFinding(NameClaimsAddressCode, $"The name says {claimed} but the message is from {from.Address}."));
            }

            return findings;
        }

        /// <summary>
        /// Findings for a cached message, or an empty list when there is nothing to say. Local
        /// folders and the account's own mail are skipped: drafts, sent mail and imported .eml were
        /// never stamped by a receiving server, so "unauthenticated" would fire on all of them and
        /// the strip would become wallpaper.
        /// </summary>
        public static IReadOnlyList<AuthFinding> Read(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            if (folderFullName.StartsWith(MessageStore.LocalFolderPrefix)) return [];
            if (summary.FromAddress.Equals(account.EmailAddress, StringComparison.OrdinalIgnoreCase)) return [];

            try
            {
                return MessageStore.TryLoadMimeMessage(account.Id, folderFullName, summary.Uid) is { } message
                    ? Inspect(message)
                    : [];
            }
            catch (Exception ex)
            {
                Log($"Could not read the authentication headers of uid {summary.Uid}: {ex.Message}", LogLevel.Warning);
                return [];
            }
        }

        static string? TopmostHeader(MimeMessage message, string field) =>
            message.Headers.FirstOrDefault(h => h.Field.Equals(field, StringComparison.OrdinalIgnoreCase))?.Value;

        /// <summary>Bare address out of a header value, which usually arrives wrapped in angle brackets.</summary>
        static string AddressOf(string? headerValue) =>
            (headerValue ?? string.Empty).Trim().Trim('<', '>').Trim();

        static string DomainOf(string address)
        {
            int at = address.LastIndexOf('@');
            return at < 0 ? string.Empty : address[(at + 1)..].Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Whether two domains count as the same party. A plain suffix test, so mail.github.com
        /// aligns with github.com — no public-suffix guessing, which would need a list this app
        /// cannot keep current and would be wrong in exactly the cases that matter.
        /// </summary>
        static bool DomainsAlign(string first, string second)
        {
            if (first.Length == 0 || second.Length == 0) return true;
            return first == second
                || first.EndsWith('.' + second, StringComparison.OrdinalIgnoreCase)
                || second.EndsWith('.' + first, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>An e-mail address spelled out inside a display name, or null when there is none.</summary>
        static string? AddressInsideName(string? displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return null;
            string? token = displayName
                .Split([' ', '<', '>', '(', ')', ',', '"'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(static part => part.Contains('@') && part.LastIndexOf('@') < part.Length - 1);
            return token?.Trim('.', ':', ';');
        }
    }
}
