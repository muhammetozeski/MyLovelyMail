using System.Runtime.CompilerServices;
using System.Text;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Teaches .NET the legacy single-byte code pages that mail still arrives in.
    /// <para>
    /// .NET Core ships only Unicode, ASCII and Latin-1; every other page lives in
    /// System.Text.Encoding.CodePages and stays unreachable until its provider is registered.
    /// Without it <c>Encoding.GetEncoding("iso-8859-9")</c> throws, MimeKit falls back to Latin-1,
    /// and a Turkish message reads "Sayýn AÞ Dijitalleþme" instead of "Sayın AŞ Dijitalleşme" —
    /// the same byte swap hits Greek, Cyrillic, Hebrew and every windows-125x sender.
    /// </para>
    /// <para>
    /// It is a module initializer because MimeKit caches its charset lookups: whatever it resolved
    /// on first use is what every later message gets, so registering after the first parse changes
    /// nothing. Running before any code in this assembly is the only placement that always wins,
    /// and it covers every host (MAUI, web, the debug API) without each having to remember.
    /// </para>
    /// </summary>
    internal static class MailTextEncodings
    {
        // CA2255 warns that module initializers belong in application code. This assembly IS the
        // application - the heads around it are thin shells - and a host that forgets to call a
        // Register() method brings the mojibake silently back, so the guarantee is worth the rule.
#pragma warning disable CA2255
        [ModuleInitializer]
#pragma warning restore CA2255
        internal static void Register() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}
