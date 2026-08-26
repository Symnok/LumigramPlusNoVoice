using System;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LumigramPlus.App
{
    /// <summary>
    /// Bare WinRT application shell.
    ///
    /// No session restore, no navigation, no state: this exists to host one page
    /// that answers two questions and is then thrown away.
    /// </summary>
    public sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            // Without this the hardware Back button closes the app from any page.
            // WinRT does not wire it to navigation for you - a Silverlight app gets
            // that behaviour by default, which is why its absence here reads as the
            // app crashing on Back rather than as a missing handler.
            Windows.Phone.UI.Input.HardwareButtons.BackPressed += OnBackPressed;
        }

        private void OnBackPressed(object sender,
                                   Windows.Phone.UI.Input.BackPressedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null || !frame.CanGoBack) return;

            // Unhandled means "close the app", so this has to be claimed explicitly
            // whenever there is somewhere to go back to.
            e.Handled = true;
            frame.GoBack();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }

            if (frame.Content == null) frame.Navigate(typeof(MainPage), e.Arguments);
            Window.Current.Activate();

            // Straight to the chats when there is already an authorisation. Landing
            // on a menu every launch is a thing to get past, not a thing to look at.
            var ignored = OpenChatsIfSignedInAsync(frame);
        }

        private static async System.Threading.Tasks.Task OpenChatsIfSignedInAsync(Frame frame)
        {
            try
            {
                SessionStore session = await SessionStore.LoadAsync();
                if (session == null || !session.SignedIn) return;

                TelegramService.Session = session;
                frame.Navigate(typeof(ChatsPage));
            }
            catch (Exception)
            {
                // Whatever went wrong, the menu is still there behind this.
            }
        }
    }
}
