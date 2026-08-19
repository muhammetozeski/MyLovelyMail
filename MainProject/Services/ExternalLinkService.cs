using System.Diagnostics;

namespace MyLovelyMail.MainProject.Services
{
    /// <summary>
    /// The one place that hands a URL to the operating system's default handler. Mail is full of
    /// links the app must never follow on its own, so every caller has to be a deliberate user
    /// action; keeping the shell call here means that rule is auditable from a single file.
    /// </summary>
    public static class ExternalLinkService
    {
        /// <summary>Schemes the app is willing to hand over. Anything else (file:, javascript:, …) is refused.</summary>
        static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

        /// <summary>
        /// Whether this app would hand <paramref name="url"/> to the shell. Callers that render a
        /// link control ask this first, so a chip never appears for something the opener refuses.
        /// </summary>
        public static bool IsOpenable(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var target)
            && AllowedSchemes.Contains(target.Scheme, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Opens <paramref name="url"/> in whatever the user has set as default. Returns false and
        /// logs when the URL is malformed, uses a scheme outside <see cref="AllowedSchemes"/>, or
        /// the shell refuses it — the caller decides what to show.
        /// </summary>
        public static bool TryOpen(string? url, out string failureReason)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var target))
            {
                failureReason = "That link is not a valid address.";
                Log($"Refused to open a malformed external link: {url}", LogLevel.Warning);
                return false;
            }

            if (!AllowedSchemes.Contains(target.Scheme, StringComparer.OrdinalIgnoreCase))
            {
                failureReason = $"Links of type '{target.Scheme}' are not opened.";
                Log($"Refused to open external link with scheme '{target.Scheme}'.", LogLevel.Warning);
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
                failureReason = string.Empty;
                Log($"Opened an external {target.Scheme} link in the default handler.");
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                Log($"Opening an external link failed: {ex.Message}", LogLevel.Warning);
                return false;
            }
        }
    }
}
