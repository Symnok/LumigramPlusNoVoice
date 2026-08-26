using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Phone.Controls;
using Lumigram.Mtproto;
using Lumigram.Phone.Services;
using Lumigram.Tl;

namespace Lumigram.Phone
{
    /// <summary>
    /// Sign-in: phone number, then the code, then two-step verification if the
    /// account has it.
    ///
    /// The three stages share one authorisation key. That is not a convenience -
    /// a login code is bound to the authorisation that requested it, so starting a
    /// fresh connection between stages would invalidate the code the user is
    /// holding and produce a PHONE_CODE_INVALID they cannot explain.
    /// </summary>
    public partial class LoginPage : PhoneApplicationPage
    {
        private string _phone;
        private string _phoneCodeHash;

        public LoginPage()
        {
            InitializeComponent();
        }

        private void ShowStage(StackPanel stage, string title)
        {
            PhonePanel.Visibility = Visibility.Collapsed;
            CodePanel.Visibility = Visibility.Collapsed;
            PasswordPanel.Visibility = Visibility.Collapsed;
            BusyPanel.Visibility = Visibility.Collapsed;

            if (stage != null) stage.Visibility = Visibility.Visible;
            if (title != null) StageTitle.Text = title;
            ErrorText.Text = "";
        }

        private void Busy(string message)
        {
            PhonePanel.Visibility = Visibility.Collapsed;
            CodePanel.Visibility = Visibility.Collapsed;
            PasswordPanel.Visibility = Visibility.Collapsed;
            BusyPanel.Visibility = Visibility.Visible;
            BusyText.Text = message;
            ErrorText.Text = "";
        }

        private void Fail(StackPanel back, string title, string message)
        {
            ShowStage(back, title);
            ErrorText.Text = message;
        }

        /// <summary>
        /// Turns a server error into something a person can act on. The raw codes
        /// are meaningless to a user, and a few of them have a specific remedy.
        /// </summary>
        private static string Explain(Exception ex)
        {
            var rpc = ex as RpcException;
            if (rpc == null) return ex.Message;

            string t = rpc.ErrorType ?? "";
            if (t.Contains("PHONE_NUMBER_INVALID")) return "That phone number is not valid.";
            if (t.Contains("PHONE_CODE_INVALID")) return "That code is not correct.";
            if (t.Contains("PHONE_CODE_EXPIRED")) return "The code expired. Request a new one.";
            if (t.Contains("PASSWORD_HASH_INVALID")) return "That password is not correct.";
            if (t.Contains("PHONE_NUMBER_BANNED")) return "This number is banned from Telegram.";
            if (t.StartsWith("FLOOD_WAIT_"))
            {
                int seconds;
                if (int.TryParse(t.Substring("FLOOD_WAIT_".Length), out seconds))
                    return "Too many attempts. Wait " + FormatWait(seconds) + " and try again.";
                return "Too many attempts. Try again later.";
            }
            return t.Length > 0 ? t : ex.Message;
        }

        private static string FormatWait(int seconds)
        {
            if (seconds < 60) return seconds + " seconds";
            if (seconds < 3600) return (seconds / 60) + " minutes";
            return (seconds / 3600) + " hours";
        }

        private async void SendCode_Click(object sender, RoutedEventArgs e)
        {
            string phone = (PhoneBox.Text ?? "").Trim().TrimStart('+').Replace(" ", "");
            if (phone.Length < 5)
            {
                ErrorText.Text = "Enter a phone number.";
                return;
            }

            _phone = phone;
            Busy("Connecting...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync(Busy);
                await SendCodeAsync(client);
            }
            catch (Exception ex)
            {
                Fail(PhonePanel, "sign in", Explain(ex));
            }
        }

        private async Task SendCodeAsync(MtprotoClient client)
        {
            Busy("Requesting a code...");

            var q = new TlWriter(64);
            q.WriteConstructor(TlConstructors.AuthSendCode)
             .WriteString(_phone)
             .WriteInt(TelegramService.Info.ApiId)
             .WriteString(TelegramService.Info.ApiHash)
             .WriteConstructor(TlConstructors.CodeSettings)
             .WriteInt(0);

            TlReader r;
            try
            {
                r = await client.InvokeAsync(q.ToArray(), TelegramService.Info);
            }
            catch (RpcException ex)
            {
                // An account lives on exactly one datacenter, and only that one will
                // authenticate it. The server names which.
                if ((ex.ErrorType ?? "").StartsWith("PHONE_MIGRATE_"))
                {
                    int dc;
                    string suffix = ex.ErrorType.Substring("PHONE_MIGRATE_".Length);
                    if (int.TryParse(suffix, out dc))
                    {
                        MtprotoClient migrated = await TelegramService.MigrateAsync(dc, Busy);
                        await SendCodeAsync(migrated);
                        return;
                    }
                }
                throw;
            }

            r.Expect(TlConstructors.AuthSentCode, "auth.sentCode");
            r.ReadInt();                                   // flags

            uint codeType = r.ReadConstructor();
            string via = codeType == TlConstructors.AuthSentCodeTypeApp ? "the Telegram app"
                       : codeType == TlConstructors.AuthSentCodeTypeSms ? "SMS"
                       : "another channel";
            int length = r.ReadInt();
            _phoneCodeHash = r.ReadString();

            PhoneSession session = TelegramService.Session;
            if (session != null)
            {
                session.Phone = _phone;
                session.PhoneCodeHash = _phoneCodeHash;
                session.Save();
            }

            ShowStage(CodePanel, "enter code");
            CodeHint.Text = "A " + length + "-digit code was sent via " + via + ".";
            CodeBox.Focus();
        }

        private async void SignIn_Click(object sender, RoutedEventArgs e)
        {
            string code = (CodeBox.Text ?? "").Trim();
            if (code.Length == 0)
            {
                ErrorText.Text = "Enter the code.";
                return;
            }

            Busy("Signing in...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync(Busy);

                var q = new TlWriter(64);
                q.WriteConstructor(TlConstructors.AuthSignIn)
                 .WriteInt(1)                              // flags: phone_code present
                 .WriteString(_phone)
                 .WriteString(_phoneCodeHash)
                 .WriteString(code);

                TlReader r = await client.InvokeAsync(q.ToArray(), TelegramService.Info);
                r.Expect(TlConstructors.AuthAuthorization, "auth.authorization");

                Done();
            }
            catch (RpcException ex)
            {
                if ((ex.ErrorType ?? "").Contains("SESSION_PASSWORD_NEEDED"))
                {
                    ShowStage(PasswordPanel, "password");
                    PasswordBoxControl.Focus();
                    return;
                }
                Fail(CodePanel, "enter code", Explain(ex));
            }
            catch (Exception ex)
            {
                Fail(CodePanel, "enter code", Explain(ex));
            }
        }

        private async void CheckPassword_Click(object sender, RoutedEventArgs e)
        {
            string password = PasswordBoxControl.Password ?? "";
            if (password.Length == 0)
            {
                ErrorText.Text = "Enter your password.";
                return;
            }

            // ~11 s of PBKDF2 on this hardware, so say so rather than appear stuck.
            Busy("Checking password...\nThis takes about 15 seconds.");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync(Busy);

                var q = new TlWriter(16);
                q.WriteConstructor(TlConstructors.AccountGetPassword);
                TlReader r = await client.InvokeAsync(q.ToArray(), TelegramService.Info);

                r.Expect(TlConstructors.AccountPassword, "account.password");
                int flags = r.ReadInt();
                if ((flags & 4) == 0)
                {
                    Fail(PasswordPanel, "password", "This account has no password set.");
                    return;
                }

                uint algo = r.ReadConstructor();
                if (algo != TlConstructors.PasswordKdfAlgoSha256Pbkdf2)
                {
                    Fail(PasswordPanel, "password",
                         "This account uses a password method this app does not support.");
                    return;
                }

                byte[] salt1 = r.ReadBytes();
                byte[] salt2 = r.ReadBytes();
                int g = r.ReadInt();
                byte[] p = r.ReadBytes();
                byte[] srpB = r.ReadBytes();
                long srpId = r.ReadLong();

                SrpProof proof = await Task.Run(delegate
                {
                    return Srp.ComputeProof(TelegramService.Crypto, password,
                                            salt1, salt2, g, p, srpB);
                });

                var check = new TlWriter(600);
                check.WriteConstructor(TlConstructors.AuthCheckPassword)
                     .WriteConstructor(TlConstructors.InputCheckPasswordSrp)
                     .WriteLong(srpId)
                     .WriteBytes(proof.A)
                     .WriteBytes(proof.M1);

                r = await client.InvokeAsync(check.ToArray());
                r.Expect(TlConstructors.AuthAuthorization, "auth.authorization");

                Done();
            }
            catch (Exception ex)
            {
                Fail(PasswordPanel, "password", Explain(ex));
            }
        }

        private void Done()
        {
            TelegramService.MarkSignedIn();
            NavigationService.Navigate(new Uri("/ChatsPage.xaml", UriKind.Relative));
            // The destination clears the back stack itself; doing it here runs
            // before the navigation completes and has no effect.
        }

        private void Qr_Click(object sender, RoutedEventArgs e)
        {
            NavigationService.Navigate(new Uri("/QrLoginPage.xaml", UriKind.Relative));
        }

        /// <summary>
        /// QR sign-in sends the user here when the token was accepted but the
        /// account has two-step verification, so the page can open straight on the
        /// password stage.
        /// </summary>
        protected override void OnNavigatedTo(System.Windows.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Sign-in is a root too: Back from here leaves the app rather than
            // returning to a signed-out session's pages. Arriving from the QR screen
            // for a password is the one case worth keeping reachable, and that path
            // navigates forward again anyway.
            while (NavigationService.CanGoBack) NavigationService.RemoveBackEntry();

            string stage;
            if (NavigationContext.QueryString.TryGetValue("stage", out stage) && stage == "password")
            {
                ShowStage(PasswordPanel, "password");
                PasswordBoxControl.Focus();
            }
        }

        private void BackToPhone_Click(object sender, RoutedEventArgs e)
        {
            ShowStage(PhonePanel, "sign in");
        }
    }
}
