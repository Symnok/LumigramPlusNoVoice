using System;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Lumigram.Mtproto;
using Lumigram.Phone;
using Lumigram.Tl;

namespace LumigramPlus.App
{
    /// <summary>
    /// The starting point of the WinRT client.
    ///
    /// It began as a spike asking two questions. The first - does Lumigram.Core run
    /// unchanged under the WinRT app model - was answered yes, on the device: the
    /// same protocol sources the Silverlight app and the desktop harness compile
    /// also negotiate an auth key and make an encrypted call from here.
    ///
    /// The second, whether ControlChannelTrigger can keep the app reachable while
    /// it is off screen, is deliberately not asked any more. This client is not
    /// trying to replace the Silverlight one: that keeps a live connection through
    /// its location subscription and is the right app for staying reachable. This
    /// one exists for what WinRT does better - video, file pickers, arbitrary
    /// attachments - and leaves background messaging to the app that already has it.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            InitializeComponent();
            // Re-read every time the page is shown, not only on first load: it is
            // navigated back to after signing in.
            Loaded += delegate { ShowSession(); };
        }

        /// <summary>
        /// Says whether there is a stored authorisation, so it is obvious whether
        /// signing in is still needed.
        /// </summary>
        private async void ShowSession()
        {
            SessionStore session = await SessionStore.LoadAsync();

            bool signedIn = session != null && session.SignedIn;

            SessionText.Text = signedIn ? "Signed in." : "Not signed in.";

            SessionDetail.Text = signedIn
                ? "Authorisation stored for " + session.Host + "."
                : session == null
                    ? "Sign in to use this account."
                    : "A key is stored but the account is not signed in yet.";

            SignInButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            SignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Forgets the authorisation on this phone.
        ///
        /// Local only for now - the session is not revoked on Telegram's side, so it
        /// stays listed under Devices there. That is deliberate at this stage rather
        /// than overlooked: it makes signing in again cheap while the client is being
        /// built, and it is worth being clear about which of the two this does.
        /// </summary>
        private async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            TelegramService.Disconnect();
            TelegramService.Session = null;

            await SessionStore.DeleteAsync();

            ShowSession();
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ShowSession();
        }

        private void SignIn_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(QrLoginPage));
        }

        private async void Core_Click(object sender, RoutedEventArgs e)
        {
            CoreButton.IsEnabled = false;

            try
            {
                Log("--- 1. Core under WinRT ---");

                var crypto = new PhoneCrypto();
                var transport = new PhoneTransport();
                var client = new MtprotoClient(crypto, transport, Log);

                // The receive loop and the keep-alive run on their own threads, so a
                // failure there never reaches the catch below - it just leaves the
                // screen looking like nothing happened.
                client.Faulted += delegate(Exception bg)
                {
                    Log("BACKGROUND FAULT: " + bg.GetType().Name + ": " + bg.Message);
                };

                Log("[1] connecting socket");
                DateTime started = DateTime.UtcNow;

                await client.ConnectAsync(TelegramServers.ProductionDc2Host,
                                          TelegramServers.DefaultPort);

                Log("[2] auth key ready in " +
                    (int)(DateTime.UtcNow - started).TotalMilliseconds + " ms");

                ClientInfo info = ClientInfo.Default;
                info.ApiId = Secrets.ApiId;
                info.ApiHash = Secrets.ApiHash;
                info.AppVersion = "Lumigram+ spike";

                var query = new TlWriter();
                query.WriteConstructor(TlConstructors.HelpGetNearestDc);

                Log("[3] invoking help.getNearestDc");

                TlReader result = await client.InvokeAsync(query.ToArray(), info);

                Log("[4] reply received");
                result.Expect(TlConstructors.NearestDc, "nearestDc");

                string country = result.ReadString();
                int thisDc = result.ReadInt();
                int nearestDc = result.ReadInt();

                Log("PASS  country=" + country + " this_dc=" + thisDc +
                    " nearest_dc=" + nearestDc);
                Log("Core runs unmodified under WinRT.");

                // Deliberately not disposed. Closing the client cancels the read the
                // receive loop always has in flight, and WinRT reports that as "the
                // operation identifier is not valid" - which looks exactly like a
                // failure printed under a passing test, and twice was read as one.
                // The real client keeps its connection anyway, so this matches it.
            }
            catch (Exception ex)
            {
                // The step markers above say how far it got; this says what stopped
                // it. Both are needed - the message alone has been ambiguous twice.
                Log("FAILED: " + ex.GetType().Name + ": " + ex.Message);
                Log("HRESULT " + ex.HResult.ToString("x8"));

                if (ex.InnerException != null)
                    Log("inner: " + ex.InnerException.GetType().Name + ": " +
                        ex.InnerException.Message);

                string stack = ex.StackTrace ?? "";
                Log(stack.Length > 400 ? stack.Substring(0, 400) : stack);
            }
            finally
            {
                CoreButton.IsEnabled = true;
            }
        }

        private void Log(string line)
        {
            if (Dispatcher.HasThreadAccess)
            {
                LogText.Text += (LogText.Text.Length > 0 ? Environment.NewLine : "") + line;
                return;
            }

            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                                              delegate { Log(line); });
        }
    }
}
