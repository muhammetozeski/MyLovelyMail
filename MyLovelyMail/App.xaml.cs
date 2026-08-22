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
            TrayService.AttachWindow(window);
            return window;
        }
    }
}
