using Android.App;
using Android.Content;
using AndroidUri = Android.Net.Uri;

namespace MyLovelyMail.MainProject.Services
{
    public static partial class ExternalLinkService
    {
        /// <summary>
        /// A view intent for the link: the phone's browser takes http and https, its mail app takes mailto.
        /// NewTask because the app context has no activity stack of its own to open it in; an
        /// ActivityNotFoundException means nothing on the phone can open that kind of link.
        /// </summary>
        static partial void OpenOnPlatform(Uri target, ref bool handedOver)
        {
            var intent = new Intent(Intent.ActionView, AndroidUri.Parse(target.AbsoluteUri)).AddFlags(ActivityFlags.NewTask);
            Application.Context.StartActivity(intent);
            handedOver = true;
        }
    }
}
