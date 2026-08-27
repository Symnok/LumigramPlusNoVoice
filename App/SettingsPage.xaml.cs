using System;
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
            NotificationsSwitch.IsOn = AppSettings.Notifications;

            // Shown only when something is wrong. Toasts fail silently by design,
            // so a working app and a refused one look identical without this.
            string trouble = Notifications.LastError ?? Notifications.NotifierState();
            ToastText.Text = trouble == null ? "" : "Toasts unavailable: " + trouble;
            _loading = false;

            AccountText.Text = TelegramService.Session != null
                ? "Signed in. Authorisation stored for " + TelegramService.Session.Host + "."
                : "Not signed in.";
        }

        private async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            AccountText.Text = "Signing out...";

            bool revoked = await TelegramService.SignOutAsync();

            if (!revoked)
            {
                // The key is gone from this phone either way, but the session may
                // still be live on Telegram's side - and only the user can clear it
                // from another device.
                var dialog = new Windows.UI.Popups.MessageDialog(
                    "The key on this phone has been deleted, but Telegram could not " +
                    "be reached to end the session. Remove it from another device " +
                    "under Settings, Devices.",
                    "Signed out on this phone");

                await dialog.ShowAsync();
            }

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

        private void Notifications_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            AppSettings.Notifications = NotificationsSwitch.IsOn;
        }

        private void AutoLoad_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            AppSettings.AutoLoadPhotos = AutoLoadSwitch.IsOn;
        }
    }
}
