using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>Where a newsletter says it can be left: a web page, a mail to send, or both.</summary>
    public sealed record UnsubscribeTargets(string? HttpUrl, string? MailtoAddress, string? MailtoSubject);

    /// <summary>
    /// Reads the List-Unsubscribe header out of the cached .eml — the same file the Source view
    /// walks — and splits it into its http and mailto forms. Nothing here contacts anyone: the
    /// header is only parsed, and acting on it stays an explicit user click (no RFC 8058
    /// one-click POST, which senders would see as an unsubscribe the user never asked for).
    /// </summary>
    public static class UnsubscribeService
    {
        const string HeaderName = "List-Unsubscribe";

        /// <summary>Parsed targets, or null when the message carries no List-Unsubscribe header.</summary>
        public static UnsubscribeTargets? Read(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            try
            {
                if (MessageStore.TryLoadMimeMessage(account.Id, folderFullName, summary.Uid) is not { } message) return null;
                string? header = message.Headers[HeaderName];
                return header == null ? null : Parse(header);
            }
            catch (Exception ex)
            {
                Log($"Could not read the unsubscribe header of uid {summary.Uid}: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        /// <summary>Header form: "&lt;https://...&gt;, &lt;mailto:x@y?subject=unsubscribe&gt;" in any order, either part optional.</summary>
        internal static UnsubscribeTargets? Parse(string header)
        {
            string? httpUrl = null, mailtoAddress = null, mailtoSubject = null;

            foreach (string part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string value = part.Trim().Trim('<', '>').Trim();
                // The same gate the opener uses, so a chip can never offer a link that would be refused.
                if (ExternalLinkService.IsOpenable(value) && !value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    httpUrl ??= value;
                }
                else if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    string target = value["mailto:".Length..];
                    int queryStart = target.IndexOf('?');
                    string address = (queryStart < 0 ? target : target[..queryStart]).Trim();
                    // Seen in the wild: "<mailto:?subject=...>" with no address at all. An empty
                    // recipient must not become a chip that opens a draft with a blank To field.
                    if (address.Contains('@'))
                    {
                        mailtoAddress ??= address;
                        if (queryStart >= 0)
                            mailtoSubject ??= ReadSubjectParameter(target[(queryStart + 1)..]);
                    }
                }
            }

            return httpUrl == null && mailtoAddress == null ? null : new UnsubscribeTargets(httpUrl, mailtoAddress, mailtoSubject);
        }

        static string? ReadSubjectParameter(string query) =>
            query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(static p => p.StartsWith("subject=", StringComparison.OrdinalIgnoreCase))
                .Select(static p => Uri.UnescapeDataString(p["subject=".Length..]))
                .FirstOrDefault();
    }
}
