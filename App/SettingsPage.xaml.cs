using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LumigramPlus.App
{
    /// <summary>Where the handful of choices live.</summary>
    public sealed partial class SettingsPage : Page
    {
        /// <summary>
        /// Set while the switch is being put into its stored position.
        ///
        /// Assigning IsOn raises Toggled, so without this the act of showing the
        /// current setting would write it straight back - harmless here, and the
        /// kind of loop that is not harmless once a setting does something on
        /// change.
        /// </summary>
        private bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _loading = true;
            AutoLoadSwitch.IsOn = AppSettings.AutoLoadPhotos;
            _loading = false;

            AccountText.Text = TelegramService.Session != null
                ? "Signed in. Authorisation stored for " + TelegramService.Session.Host + "."
                : "Not signed in.";
        }

        private async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            TelegramService.Disconnect();
            TelegramService.Session = null;

            await SessionStore.DeleteAsync();

            Frame.Navigate(typeof(QrLoginPage));
            Frame.BackStack.Clear();
        }

        /// <summary>
        /// Closes the app rather than leaving it suspended.
        ///
        /// Worth having on a phone with an authorisation key in memory: suspending
        /// keeps the process and everything in it, where this ends both.
        /// </summary>
        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            TelegramService.Disconnect();
            Application.Current.Exit();
        }

        private void AutoLoad_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            AppSettings.AutoLoadPhotos = AutoLoadSwitch.IsOn;
        }
    }
}
