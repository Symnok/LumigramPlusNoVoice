using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Lumigram.Mtproto;
using Lumigram.Phone.Services;

namespace Lumigram.Phone
{
    /// <summary>
    /// Starting a chat with someone not already in the list.
    ///
    /// Accepts a username or a phone number and works out which is which, so the
    /// user does not have to pick a mode first. Looking someone up does not add
    /// them to the account's contacts - see Contacts.ResolveAsync for why that
    /// matters.
    /// </summary>
    public partial class NewChatPage : PhoneApplicationPage
    {
        private ResolvedPeer _found;

        public NewChatPage()
        {
            InitializeComponent();
            QueryBox.TextChanged += delegate { UpdateHint(); };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            QueryBox.Focus();
        }

        /// <summary>
        /// Shows how the input will be treated, before anything is sent.
        ///
        /// Worth doing: "1234" is a username and "19990001234" is a number, and a
        /// user who typed one meaning the other should be able to see that.
        /// </summary>
        private void UpdateHint()
        {
            string text = (QueryBox.Text ?? "").Trim();
            if (text.Length == 0) { KindHint.Text = ""; return; }

            KindHint.Text = Contacts.LooksLikePhone(text)
                ? "will search by phone: " + Contacts.NormalisePhone(text)
                : "will search by username: " + Contacts.NormaliseUsername(text);
        }

        private void QueryBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Find();
        }

        private void Find_Click(object sender, RoutedEventArgs e)
        {
            Find();
        }

        private async void Find()
        {
            string query = (QueryBox.Text ?? "").Trim();
            if (query.Length == 0) { StatusText.Text = "Enter a username or number."; return; }

            Busy.Visibility = Visibility.Visible;
            StatusText.Text = "";
            ResultPanel.Visibility = Visibility.Collapsed;
            FindButton.IsEnabled = false;
            _found = null;

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();
                _found = await Contacts.ResolveAsync(client, query, TelegramService.Info);

                ResultTitle.Text = _found.Title;

                string detail = _found.Kind;
                if (!string.IsNullOrEmpty(_found.Username)) detail += "  @" + _found.Username;
                if (!string.IsNullOrEmpty(_found.Phone)) detail += "  +" + _found.Phone;
                ResultDetail.Text = detail;

                ResultPanel.Visibility = Visibility.Visible;
            }
            catch (RpcException ex)
            {
                StatusText.Text = Explain(ex);
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }

            Busy.Visibility = Visibility.Collapsed;
            FindButton.IsEnabled = true;
        }

        /// <summary>Turns the server's error into something a person can act on.</summary>
        private static string Explain(RpcException ex)
        {
            string t = ex.ErrorType ?? "";

            if (t.Contains("USERNAME_NOT_OCCUPIED")) return "No account uses that username.";
            if (t.Contains("USERNAME_INVALID")) return "That is not a valid username.";
            if (t.Contains("PHONE_NOT_OCCUPIED")) return "No Telegram account on that number.";
            if (t.Contains("PHONE_NUMBER_INVALID")) return "That phone number is not valid.";
            if (t.StartsWith("FLOOD_WAIT_")) return "Too many lookups. Try again later.";

            return t.Length > 0 ? t : ex.Message;
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            if (_found == null) return;

            NavigationService.Navigate(new Uri(
                "/ConversationPage.xaml?peer=" + _found.PeerId +
                "&hash=" + _found.AccessHash +
                "&kind=" + _found.Kind +
                "&title=" + Uri.EscapeDataString(_found.Title ?? ""), UriKind.Relative));
        }
    }
}
