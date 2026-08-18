using System.Text.RegularExpressions;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Spots a promise of an attachment in what the user actually TYPED. The quoted history is
    /// skipped — replies carry the original message below, and scanning it would fire the warning
    /// on every reply to a mail that once mentioned a file, which is how a warning gets ignored.
    /// </summary>
    public static partial class AttachmentIntentService
    {
        /// <summary>Words in both languages the user writes in, matched whole — a bare substring
        /// search would fire "cv" inside "service" and "ekli" inside "eklinde".</summary>
        [GeneratedRegex(@"\b(attached|attaching|attachments?|enclosed|cv|resume|ekte|ektedir|ekli|ekleyerek|ili[şs]tirdim)\b", RegexOptions.IgnoreCase)]
        private static partial Regex TriggerPattern();

        /// <summary>The promising word, or null when nothing in the new text mentions an attachment.</summary>
        public static string? FindTrigger(string subject, string body)
        {
            var subjectHit = TriggerPattern().Match(subject ?? string.Empty);
            if (subjectHit.Success) return subjectHit.Value;

            foreach (string line in (body ?? string.Empty).Split('\n'))
            {
                string text = line.TrimEnd('\r');
                // Everything from here down is the quoted original, which the user did not write.
                if (MailBodyRenderer.PlainQuoteStart().IsMatch(text)) break;
                if (text.TrimStart().StartsWith('>')) continue;

                var hit = TriggerPattern().Match(text);
                if (hit.Success) return hit.Value;
            }
            return null;
        }
    }
}
