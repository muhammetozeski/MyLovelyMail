using MyLovelyMail.MainProject.Constants;

namespace MyLovelyMail
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new MainPage()) { Title = AppConstants.AppNameHumanReadable };
            OnWindowCreated(window);
            return window;
        }

        /// <summary>Lets a desktop platform attach its window behavior; a phone has no window to hide, move or resize.</summary>
        /// <param name="window">The one app window, before it is shown.</param>
        partial void OnWindowCreated(Window window);
    }
}
