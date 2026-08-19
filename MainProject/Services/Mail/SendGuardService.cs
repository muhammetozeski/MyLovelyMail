using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>One thing worth a second look before a draft leaves. <paramref name="Code"/> is what tests assert on; <paramref name="Text"/> is what the user reads.</summary>
    public readonly record struct SendWarning(string Code, string Text);

    /// <summary>
    /// Everything the app checks before handing a draft to SMTP, in one list. Compose used to
    /// stop only for a missing attachment and let the rest through; an empty subject or a
    /// reply-all that quietly grew to forty recipients cannot be taken back once the server
    /// accepts them, and undo-send only buys a few seconds.
    /// <para>
    /// Every check is a pure function of the draft (plus the contact index), so the whole thing
    /// is exercisable without a compose window.
    /// </para>
    /// </summary>
    public static class SendGuardService
    {
        /// <summary>Codes, so the UI and the tests never depend on the wording.</summary>
        public const string NoSubjectCode = "no-subject";
        public const string ManyRecipientsCode = "many-recipients";
        public const string DuplicateRecipientCode = "duplicate-recipient";
        public const string DomainTypoCode = "domain-typo";
        public const string AttachmentCode = "attachment";
        public const string AttachmentSizeCode = "attachment-too-large";
        public const string SendingIdentityCode = "sending-identity";

        /// <summary>
        /// Base64 grows a file by roughly a third, and the ceiling a server enforces is on the
        /// ENCODED size — measuring the raw bytes would pass a message the server then refuses.
        /// </summary>
        const double Base64Inflation = 1.37;

        const long BytesPerMegabyte = 1024 * 1024;

        /// <summary>Above this many addressees the send is worth a second look; a reply-all reaches it without anyone deciding to.</summary>
        const int ManyRecipientsThreshold = 10;

        /// <summary>How many contacts to weigh a domain against — enough to know a domain is really yours.</summary>
        const int ContactSampleSize = 200;

        /// <summary>A domain must appear at least this often in the contact index before a near-miss counts as a typo of it.</summary>
        const int DomainFamiliarityThreshold = 3;

        /// <summary>Warnings for this draft, in the order they are worth reading. Empty = nothing to stop for.</summary>
        public static IReadOnlyList<SendWarning> Inspect(ComposeDraft draft)
        {
            List<SendWarning> warnings = [];

            if (string.IsNullOrWhiteSpace(draft.Subject))
                warnings.Add(new SendWarning(NoSubjectCode, "This message has no subject."));

            List<string> to = SplitAddresses(draft.To);
            List<string> cc = SplitAddresses(draft.Cc);
            int recipientCount = to.Count + cc.Count;
            if (recipientCount > ManyRecipientsThreshold)
                warnings.Add(new SendWarning(ManyRecipientsCode, $"This goes to {recipientCount} people."));

            string? duplicate = to.FirstOrDefault(address => cc.Contains(address, StringComparer.OrdinalIgnoreCase));
            if (duplicate != null)
                warnings.Add(new SendWarning(DuplicateRecipientCode, $"{duplicate} is in both To and Cc."));

            if (FindDomainTypo(draft.Account?.Id, [.. to, .. cc]) is { } typo)
                warnings.Add(new SendWarning(DomainTypoCode, $"Did you mean @{typo.Known} instead of @{typo.Typed}?"));

            if (draft.AttachmentPaths.Count == 0
                && AttachmentIntentService.FindTrigger(draft.Subject, draft.Body) is { } trigger)
                warnings.Add(new SendWarning(AttachmentCode, $"\"{trigger}\" is in the text, but nothing is attached."));

            // A reply that leaves from the wrong address is not a typo the recipient can ignore;
            // it tells them which of your mailboxes to answer.
            if (draft.Account is { } sender && draft.ArrivedAtAddress.Length > 0
                && !SplitAddresses(draft.ArrivedAtAddress).Contains(sender.EmailAddress, StringComparer.OrdinalIgnoreCase))
                warnings.Add(new SendWarning(SendingIdentityCode,
                    $"This leaves from {sender.EmailAddress}, but the mail arrived at {draft.ArrivedAtAddress}."));

            if (FindOversizedAttachments(draft) is { } oversized)
                warnings.Add(new SendWarning(AttachmentSizeCode, oversized));

            return warnings;
        }

        /// <summary>
        /// The message the user should see when the attachments will not fit, or null when they
        /// will. Nothing here asks the server: the ceiling is a number the user sets, which keeps
        /// this a pure function of the draft like every other check.
        /// <para>
        /// Without it, ComposeService hands every staged file to the builder and lets the server
        /// decide — and a server that drops the connection mid-DATA instead of answering 552 lands
        /// the message in the outbox, where the periodic flush retries the same too-big mail
        /// forever with nothing explaining why.
        /// </para>
        /// </summary>
        static string? FindOversizedAttachments(ComposeDraft draft)
        {
            if (draft.AttachmentPaths.Count == 0) return null;

            int ceilingMb = draft.Account is { } account
                ? AccountStore.GetSettings(account.Id).MaxAttachmentTotalMb.Value
                : Settings.MaxAttachmentTotalMb.Value;
            if (ceilingMb <= 0) return null;

            long rawBytes = draft.AttachmentPaths
                .Where(File.Exists)
                .Sum(path => new FileInfo(path).Length);
            double encodedMb = rawBytes * Base64Inflation / BytesPerMegabyte;
            return encodedMb > ceilingMb
                ? $"{encodedMb:0.0} MB of attachments, over this account's {ceilingMb} MB limit."
                : null;
        }

        /// <summary>Splits a header field into bare addresses; display names and empty entries are dropped.</summary>
        static List<string> SplitAddresses(string? field) =>
            [.. (field ?? string.Empty)
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static entry => entry.Contains('<') ? entry[(entry.IndexOf('<') + 1)..].TrimEnd('>').Trim() : entry)
                .Where(static address => address.Contains('@'))];

        /// <summary>
        /// A domain one edit away from one the user writes to often, that they have never written
        /// to before. Both halves matter: without the familiarity floor every new correspondent at
        /// a similar domain would be flagged, and without the "never seen" half, a company that
        /// genuinely owns both company.co and company.com would warn on every send.
        /// </summary>
        static (string Typed, string Known)? FindDomainTypo(string? accountId, List<string> recipients)
        {
            // No account means no contact history to compare against, so there is nothing to say.
            if (string.IsNullOrEmpty(accountId)) return null;

            var knownDomains = ContactIndexService.Suggest(accountId, string.Empty, ContactSampleSize)
                .Select(static contact => DomainOf(contact.Address))
                .Where(static domain => domain.Length > 0)
                .GroupBy(static domain => domain, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static g => g.Key, static g => g.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (string typedDomain in recipients.Select(DomainOf).Where(static d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (knownDomains.ContainsKey(typedDomain)) continue;
                foreach (var (known, seenCount) in knownDomains)
                {
                    if (seenCount < DomainFamiliarityThreshold) continue;
                    if (EditDistanceIsOne(typedDomain, known)) return (typedDomain, known);
                }
            }
            return null;
        }

        static string DomainOf(string address)
        {
            int at = address.LastIndexOf('@');
            return at < 0 ? string.Empty : address[(at + 1)..].Trim();
        }

        /// <summary>True when one insert, delete or substitution turns one string into the other. Cheaper and clearer than a full edit-distance matrix for the only distance we act on.</summary>
        static bool EditDistanceIsOne(string first, string second)
        {
            if (Math.Abs(first.Length - second.Length) > 1) return false;
            if (first.Equals(second, StringComparison.OrdinalIgnoreCase)) return false;

            int i = 0, j = 0;
            bool spent = false;
            while (i < first.Length && j < second.Length)
            {
                if (char.ToLowerInvariant(first[i]) == char.ToLowerInvariant(second[j])) { i++; j++; continue; }
                if (spent) return false;
                spent = true;
                if (first.Length == second.Length) { i++; j++; }
                else if (first.Length > second.Length) i++;
                else j++;
            }
            return true;
        }
    }
}
