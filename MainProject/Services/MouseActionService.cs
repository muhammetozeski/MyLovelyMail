using Microsoft.JSInterop;

namespace MyLovelyMail.MainProject.Services
{
    /// <summary>
    /// What the mouse's side buttons do. In a browser they are Back and Forward; in a one-window
    /// mail client that only ever lands somewhere the user did not ask for, so the script that
    /// swallows the navigation hands the click here instead.
    /// <para>
    /// Actions are named in markup with a <c>data-mouse4</c> / <c>data-mouse5</c> attribute, so a
    /// new surface gains a side-button behavior by writing one attribute — no wiring per element.
    /// </para>
    /// </summary>
    public static class MouseActionService
    {
        /// <summary>Copy the payload (an address, a path, a subject) to the clipboard.</summary>
        public const string CopyAction = "copy";

        /// <summary>Raised with a short line for the UI to show. Nothing happens silently.</summary>
        public static event Action<string>? OnStatus;

        /// <summary>Called from mouse-buttons.js. Static, so no DotNetObjectReference has to be kept alive.</summary>
        [JSInvokable("MouseSideButton")]
        public static Task SideButtonAsync(string action, string payload) => RunAsync(action, payload);

        /// <summary>The action itself, reachable from the debug API so no real mouse is needed to test it.</summary>
        public static async Task<string> RunAsync(string action, string payload)
        {
            string status = action switch
            {
                CopyAction when payload.Length > 0 => await CopyAsync(payload),
                CopyAction => "Nothing to copy here",
                _ => $"No side-button action named '{action}'"
            };
            OnStatus?.Invoke(status);
            return status;
        }

        static async Task<string> CopyAsync(string payload) =>
            await ClipboardService.CopyAsync(payload)
                ? $"📋 Copied {payload}"
                : "❌ Could not reach the clipboard";
    }
}
