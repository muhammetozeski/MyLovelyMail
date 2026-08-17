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
            var window = new Window(new MainPage()) { Title = "My Lovely Mail" };
            TrayService.AttachWindow(window);
            return window;
        }
    }
}
