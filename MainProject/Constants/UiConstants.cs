namespace MyLovelyMail.MainProject.Constants
{
    public static class UiConstants
    {
        public static class Delays
        {
            public const int TooltipAutoCloseMs = 3000;
            public const int AuthErrorClearMs = 4000;
            public const int SplashMinimumMs = 0 * 1000;
        }

        public static class AuthErrorKeywords
        {
            public const string Network = "network";
            public const string Socket = "socket";
            public const string InvalidEmail = "invalid-email";
            public const string BadlyFormatted = "badly formatted";
            public const string WeakPassword = "weak-password";
            public const string EmailInUse = "email-already-in-use";
            public const string Exists = "exists";
            public const string RequiresRecentLogin = "requires-recent-login";
            public const string RecentLogin = "recent login";
        }

        public static class AuthMessages
        {
            public const string Network = "Connection problem occurred. Please check your internet.";
            public const string InvalidEmail = "You entered an invalid email address.";
            public const string WeakPassword = "The password is too weak.";
            public const string EmailInUse = "This email address is already in use.";
            public const string DefaultInvalid = "Invalid email or password.";
            public const string AuthFailed = "Authentication failed. Please check your credentials.";
            public const string DeleteFailed = "Account deletion failed. Please try again.";
            public const string ReauthRequired = "For your security, please sign in again and then delete your account.";
        }

        public static class SignInPrompt
        {
            public const string Icon = "🔒";
            public const string Title = "Save your progress";
            public const string GuestModeLabel = "Continue as guest";
            public const string CallToAction = "Sign in to keep your data safe across devices.";
            public const string ConfirmLabel = "Sign in";
            public const string DismissLabel = "Maybe later";

            public static class Reasons
            {
                public const string ProgressSaved = "You're playing as a guest, so this progress only lives on this device.";
            }
        }
    }
}
