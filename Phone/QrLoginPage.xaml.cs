using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Lumigram.Mtproto;
using Lumigram.Phone.Services;
using Lumigram.Qr;
using Lumigram.Tl;

namespace Lumigram.Phone
{
    /// <summary>
    /// Signing in by QR code, for when a login code never arrives.
    ///
    /// Nothing is typed and no code is delivered: the server issues a token, this
    /// page renders it, another signed-in Telegram scans it, and the server pushes
    /// updateLoginToken back here. It does require a second device that is already
    /// signed in.
    ///
    /// Tokens expire in well under a minute, so the page re-requests and redraws
    /// rather than leaving a dead code on screen - a stale QR that silently stops
    /// working is worse than no QR.
    /// </summary>
    public partial class QrLoginPage : PhoneApplicationPage
    {
        private bool _running;
        private volatile bool _tokenAccepted;
        private volatile bool _checkRequested;
        private int _exports;
        private int _updates;
        private MtprotoClient _client;

        public QrLoginPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_running) return;
            _running = true;
            Run();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _running = false;
        }

        private void Status(string text, bool busy)
        {
            Dispatcher.BeginInvoke(delegate
            {
                StatusText.Text = text ?? "";
                Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            });
        }

        private async void Run()
        {
            Status("Connecting...", true);

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync(
                    delegate (string s) { Status(s, true); });

                _client = client;
                client.UpdateReceived += OnUpdate;

                while (_running)
                {
                    _exports++;
                    Diag();

                    QrLoginStep step = await QrLogin.ExportTokenAsync(
                        client, TelegramService.Info.ApiId, TelegramService.Info.ApiHash, TelegramService.Info);

                    if (step.Status == QrLoginStatus.Migrate)
                    {
                        client = await MigrateAndImportAsync(client, step);
                        if (client == null) return;         // signed in, or handed off
                        continue;
                    }

                    if (step.Status == QrLoginStatus.Success)
                    {
                        Done();
                        return;
                    }

                    Draw(step.Url);
                    Status("Waiting for a scan - expires in " + step.SecondsRemaining + "s", true);

                    // Poll. The updateLoginToken push is not reliable here - it was
                    // never observed arriving - so re-exporting is what actually
                    // detects the scan. Each export may return a *different* token,
                    // so the screen is redrawn whenever it changes; leaving a stale
                    // code up is what produces AUTH_TOKEN_EXPIRED for whoever scans
                    // it a moment later.
                    int limit = Math.Max(10, step.SecondsRemaining);
                    int waited = 0;
                    bool finished = false;

                    while (_running && waited < limit && !finished)
                    {
                        // Poll every two seconds, but check sooner if the push does
                        // arrive or the user says they have scanned it.
                        for (int t = 0; t < 2 && !_tokenAccepted && !_checkRequested; t++)
                        {
                            await Task.Delay(1000);
                            waited++;
                        }
                        _tokenAccepted = false;
                        _checkRequested = false;

                        _exports++;
                        QrLoginStep poll = await Collect(client);
                        if (poll == null) return;             // signed in, or handed off

                        if (poll.Status == QrLoginStatus.Migrate)
                        {
                            client = await MigrateAndImportAsync(client, poll);
                            if (client == null) return;     // signed in, or handed off
                            finished = true;
                            break;
                        }

                        if (poll.Token != null &&
                            QrLoginStep.Base64Url(poll.Token) != QrLoginStep.Base64Url(step.Token))
                        {
                            step = poll;
                            Draw(step.Url);
                            waited = 0;
                            limit = Math.Max(10, step.SecondsRemaining);
                        }

                        Status("Waiting for a scan - " + Math.Max(0, limit - waited) + "s", true);
                        Diag();
                    }

                    if (!_running) return;
                    if (finished) continue;

                    Status("Refreshing code...", true);
                }
            }
            catch (Exception ex)
            {
                Status(Describe(ex), false);
            }
        }

        /// <summary>
        /// Moves to the datacenter the server named and *imports the same token*.
        ///
        /// This is the step that was missing. An account lives on exactly one
        /// datacenter; a token accepted anywhere else comes back as
        /// loginTokenMigrateTo, and the token has to be carried over and imported
        /// there. Exporting a fresh token instead abandons the authorisation that
        /// was just created - which is why the first scan appeared to work on the
        /// scanning device, left a dead session behind, and never signed this one
        /// in. It also explains two linked sessions for one login.
        ///
        /// Returns null when the flow is finished (signed in, or gone to the
        /// password screen), otherwise the client to keep using.
        /// </summary>
        private async Task<MtprotoClient> MigrateAndImportAsync(MtprotoClient client, QrLoginStep step)
        {
            // Establish the new datacenter *before* giving up the old one. The
            // handshake costs seconds on this hardware and the token expires in
            // seconds, so doing it the other way round loses the race - the token is
            // dead by the time it can be imported, and the authorisation the scan
            // created is orphaned.
            MtprotoClient moved;
            try
            {
                moved = await TelegramService.ConnectSeparateAsync(step.DcId,
                    delegate (string s2) { Status(s2, true); });
            }
            catch (Exception ex)
            {
                Status(Describe(ex), false);
                return client;
            }

            Status("Signing in on datacenter " + step.DcId + "...", true);

            // Ask the original datacenter for a token now, rather than reusing one
            // that has been ageing through the handshake above.
            byte[] token = step.Token;
            try
            {
                QrLoginStep refreshed = await QrLogin.ExportTokenAsync(
                    client, TelegramService.Info.ApiId, TelegramService.Info.ApiHash);
                if (refreshed.Status == QrLoginStatus.Migrate && refreshed.Token != null)
                    token = refreshed.Token;
                else if (refreshed.Status == QrLoginStatus.Success)
                {
                    // The original datacenter completed it after all.
                    Done();
                    return null;
                }
            }
            catch (RpcException)
            {
                // Fall back to the token we already have.
            }

            try
            {
                QrLoginStep imported = await QrLogin.ImportTokenAsync(
                    moved, token, TelegramService.Info);

                if (imported.Status == QrLoginStatus.Success)
                {
                    TelegramService.Adopt(moved, step.DcId);
                    _client = moved;
                    Done();
                    return null;
                }
            }
            catch (RpcException ex)
            {
                string t = ex.ErrorType ?? "";

                if (t.Contains("SESSION_PASSWORD_NEEDED"))
                {
                    TelegramService.Adopt(moved, step.DcId);
                    _client = moved;
                    Dispatcher.BeginInvoke(delegate
                    {
                        NavigationService.Navigate(
                            new Uri("/LoginPage.xaml?stage=password", UriKind.Relative));
                    });
                    return null;
                }

                if (t.Contains("AUTH_TOKEN_EXPIRED"))
                {
                    // Start over on the correct datacenter. A code shown there needs
                    // no migrate at all, so the next scan completes directly.
                    Status("Code expired during transfer - showing a new one", false);
                    TelegramService.Adopt(moved, step.DcId);
                    _client = moved;
                    moved.UpdateReceived += OnUpdate;
                    return moved;
                }

                Status(Describe(ex), false);
            }

            return moved;
        }

        /// <summary>
        /// Re-exports after a scan, to collect the authorisation. Returns null when
        /// the flow is over - either signed in, or handed to the password screen.
        /// </summary>
        private async Task<QrLoginStep> Collect(MtprotoClient client)
        {
            try
            {
                QrLoginStep step = await QrLogin.ExportTokenAsync(
                    client, TelegramService.Info.ApiId, TelegramService.Info.ApiHash);

                if (step.Status == QrLoginStatus.Success)
                {
                    Done();
                    return null;
                }
                return step;
            }
            catch (RpcException ex)
            {
                if ((ex.ErrorType ?? "").Contains("SESSION_PASSWORD_NEEDED"))
                {
                    // The token was accepted; only two-step verification is left.
                    Dispatcher.BeginInvoke(delegate
                    {
                        NavigationService.Navigate(
                            new Uri("/LoginPage.xaml?stage=password", UriKind.Relative));
                    });
                    return null;
                }
                Status(Describe(ex), false);
                return null;
            }
        }

        private void OnUpdate(TlObject pushed)
        {
            _updates++;
            Diag();
            if (QrLogin.IsTokenAccepted(pushed)) _tokenAccepted = true;
        }

        /// <summary>
        /// Shows what the page is actually doing. Without this, "the scan worked but
        /// nothing happened" is indistinguishable from a dead loop, a missed push,
        /// or a silent exception - and there is no console on a phone.
        /// </summary>
        private void Diag()
        {
            Dispatcher.BeginInvoke(delegate
            {
                DiagText.Text = "tokens: " + _exports + "   updates received: " + _updates;
            });
        }

        /// <summary>
        /// Checks immediately rather than waiting for the token to expire.
        ///
        /// The push is not reliable enough to be the only trigger: if
        /// updateLoginToken never arrives, the page would sit there for the full
        /// expiry even though the account is already authorised. Re-exporting after
        /// a scan returns the authorisation directly.
        /// </summary>
        private void Check_Click(object sender, RoutedEventArgs e)
        {
            _checkRequested = true;
            Status("Checking...", true);
        }

        private static string Describe(Exception ex)
        {
            var rpc = ex as RpcException;
            if (rpc == null) return ex.Message;

            string t = rpc.ErrorType ?? "";
            if (t.StartsWith("FLOOD_WAIT_")) return "Too many attempts. Try again later.";
            return t.Length > 0 ? t : ex.Message;
        }

        /// <summary>
        /// Renders the QR into a WriteableBitmap at whole-pixel scale.
        ///
        /// Scaling must be an integer: a fractional factor blurs module edges, and a
        /// camera reads the result far less reliably.
        /// </summary>
        private void Draw(string url)
        {
            bool[,] modules = QrCode.Encode(url);
            int size = modules.GetLength(0);
            const int quiet = 4;

            int target = 300;
            int scale = Math.Max(1, target / (size + 2 * quiet));
            int pixels = (size + 2 * quiet) * scale;

            var bitmap = new WriteableBitmap(pixels, pixels);
            int[] buffer = bitmap.Pixels;

            const int white = unchecked((int)0xFFFFFFFF);
            const int black = unchecked((int)0xFF000000);

            for (int y = 0; y < pixels; y++)
            {
                int row = y / scale - quiet;
                for (int x = 0; x < pixels; x++)
                {
                    int col = x / scale - quiet;
                    bool dark = row >= 0 && col >= 0 && row < size && col < size && modules[row, col];
                    buffer[y * pixels + x] = dark ? black : white;
                }
            }

            bitmap.Invalidate();

            Dispatcher.BeginInvoke(delegate
            {
                QrImage.Source = bitmap;
                QrImage.Width = pixels;
                QrImage.Height = pixels;
            });
        }

        private void Done()
        {
            TelegramService.MarkSignedIn();
            Dispatcher.BeginInvoke(delegate
            {
                NavigationService.Navigate(new Uri("/ChatsPage.xaml", UriKind.Relative));
                // ChatsPage clears the back stack on arrival.
            });
        }

        private void UseCode_Click(object sender, RoutedEventArgs e)
        {
            NavigationService.Navigate(new Uri("/LoginPage.xaml", UriKind.Relative));
        }
    }
}
