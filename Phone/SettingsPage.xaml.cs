using System;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Lumigram.Mtproto;
using Lumigram.Phone.Services;
using Lumigram.Tl;

namespace Lumigram.Phone
{
    public partial class SettingsPage : PhoneApplicationPage
    {
        public SettingsPage()
        {
            InitializeComponent();

            AboutText.Text = "MTProto 2.0 client, API layer " + TlConstructors.Layer +
                             ".\nTalks directly to Telegram - no intermediate server.";
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ShowAccount();
            ShowStorage();
            LoadPreferences();
            StartDiagnostics();
        }

        /// <summary>
        /// Reports where attachments can be saved, and why not when they cannot.
        /// </summary>
        /// <summary>
        /// Reflects stored settings into the controls.
        ///
        /// _loading guards the change handlers: setting IsChecked raises Checked,
        /// which would otherwise save and re-apply the background mode on every
        /// visit to this page.
        /// </summary>
        private bool _loading;

        private void LoadPreferences()
        {
            _loading = true;

            AppSettings s = AppSettings.Current;
            NotificationsBox.IsChecked = s.NotificationsEnabled;
            SoundBox.IsChecked = s.NotificationSound;

            BgDisabled.IsChecked = s.Background == BackgroundMode.Disabled;
            BgPeriodic.IsChecked = s.Background == BackgroundMode.Periodic;
            BgAlwaysOn.IsChecked = s.Background == BackgroundMode.AlwaysOn;

            BackgroundHint.Text = BackgroundControl.Describe(s.Background);
            BackgroundStatus.Text = "";

            _loading = false;
        }

        private void Notifications_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.NotificationsEnabled = NotificationsBox.IsChecked == true;
            AppSettings.Current.Save();
        }

        private void Sound_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Current.NotificationSound = SoundBox.IsChecked == true;
            AppSettings.Current.Save();
        }

        private void Background_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            BackgroundMode mode = BackgroundMode.Disabled;
            if (BgPeriodic.IsChecked == true) mode = BackgroundMode.Periodic;
            else if (BgAlwaysOn.IsChecked == true) mode = BackgroundMode.AlwaysOn;

            AppSettings.Current.Background = mode;
            AppSettings.Current.Save();

            BackgroundHint.Text = BackgroundControl.Describe(mode);

            // Report a refusal rather than leaving a setting that looks applied but
            // is not - the OS can decline a background agent outright.
            // Apply() compares against what is already in effect, so changing the
            // setting first and calling it is enough - and avoids tearing down a
            // live location subscription only to rebuild the same one.
            string error = null;
            if (mode == BackgroundMode.Periodic) error = BackgroundControl.StartPeriodic();
            BackgroundControl.Apply();

            BackgroundStatus.Text = error == null ? "" : "Not applied: " + error;
        }

        private async void ShowStorage()
        {
            StorageText.Text = "checking...";
            try { StorageText.Text = await GallerySave.DescribeStorageAsync(); }
            catch (Exception ex) { StorageText.Text = ex.Message; }
        }

        private async void ShowAccount()
        {
            PhoneSession session = TelegramService.Session;

            AccountText.Text = session != null && !string.IsNullOrEmpty(session.Phone)
                ? "+" + session.Phone
                : "signed in";

            ConnectionText.Text = session != null
                ? "datacenter " + session.Host
                : "";

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();
                string name = await Messages.GetSelfNameAsync(client, TelegramService.Info);
                if (!string.IsNullOrEmpty(name))
                    AccountText.Text = name + (session != null && !string.IsNullOrEmpty(session.Phone)
                        ? "  (+" + session.Phone + ")" : "");
            }
            catch (Exception)
            {
                // Only a display nicety - leave the phone number showing.
            }
        }

        private async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult confirm = MessageBox.Show(
                "Sign out of Telegram on this phone?\n\n" +
                "This ends the session on Telegram's servers and deletes the stored key.",
                "Sign out", MessageBoxButton.OKCancel);

            if (confirm != MessageBoxResult.OK) return;

            SignOutButton.IsEnabled = false;
            ExitButton.IsEnabled = false;
            BusyPanel.Visibility = Visibility.Visible;
            BusyText.Text = "Signing out...";

            bool revoked = await TelegramService.SignOutAsync();

            BusyPanel.Visibility = Visibility.Collapsed;

            if (!revoked)
            {
                // The local key is gone either way - the user asked to sign out, and
                // a network failure must not leave a credential on the phone. But the
                // server session may still be live, and only they can clear it.
                MessageBox.Show(
                    "The key on this phone has been deleted, but Telegram could not be " +
                    "reached to end the session.\n\n" +
                    "Remove it from another device under Settings > Devices.",
                    "Signed out locally", MessageBoxButton.OK);
            }

            NavigationService.Navigate(new Uri("/LoginPage.xaml", UriKind.Relative));
            // The destination clears the back stack itself; doing it here runs
            // before the navigation completes and has no effect.
        }

        private System.Windows.Threading.DispatcherTimer _testToastTimer;

        /// <summary>
        /// Fires a toast a few seconds from now, giving the user time to leave the
        /// app first.
        ///
        /// A toast raised while the app is on screen is discarded by the platform,
        /// so testing one from a button is otherwise impossible - which is exactly
        /// why "no notifications" was so hard to pin down. This separates the two
        /// halves: if this toast appears, delivery works and any remaining silence
        /// is the app not running in the background; if it does not appear, the
        /// problem is the toast itself and background execution is a red herring.
        /// </summary>
        /// <summary>
        /// The update counters, kept somewhere they can be found without being in
        /// the way. They are how a stalled poll, a wrong clock or a dead background
        /// subscription gets noticed at all, so they are worth keeping - just not
        /// across the top of every conversation.
        /// </summary>
        private void StartDiagnostics()
        {
            DiagText.Text = TelegramService.Diagnostics;

            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += delegate { DiagText.Text = TelegramService.Diagnostics; };
            timer.Start();
        }

        private void TestToast_Click(object sender, RoutedEventArgs e)
        {
            TestToastButton.IsEnabled = false;

            if (_testToastTimer == null)
            {
                _testToastTimer = new System.Windows.Threading.DispatcherTimer();
                _testToastTimer.Interval = TimeSpan.FromSeconds(8);
                _testToastTimer.Tick += delegate
                {
                    _testToastTimer.Stop();
                    TestToastButton.IsEnabled = true;

                    Notifier.ShowTest("Lumigram", "Test notification - notifications are working.");
                };
            }

            _testToastTimer.Stop();
            _testToastTimer.Start();

            TestToastHint.Text = "Press Start now. The toast fires in 8 seconds.";
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            AppExit.Quit();
        }
    }
}
