using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>One row of the chat list.</summary>
    public sealed class DialogItem : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        public long PhotoId { get; set; }

        /// <summary>Newest message already read, so a chat can open where it left off.</summary>
        public int ReadInboxMaxId { get; set; }

        /// <summary>
        /// The picture, once it has been fetched.
        ///
        /// Set after the list is already on screen, which is why this class has to
        /// raise a change - the rows are bound before any picture exists.
        /// </summary>
        private Windows.UI.Xaml.Media.ImageSource _avatar;
        public Windows.UI.Xaml.Media.ImageSource Avatar
        {
            get { return _avatar; }
            set { _avatar = value; Raise("Avatar"); }
        }

        public long PeerId { get; set; }
        public long AccessHash { get; set; }
        public string Kind { get; set; }

        // These change while the list is on screen, so each one tells its row about
        // it. Rebuilding the collection instead would flicker every few seconds and
        // throw away the avatars that had been fetched.

        private string _title;
        public string Title
        {
            get { return _title; }
            set { _title = value; Raise("Title"); Raise("Initials"); }
        }

        private string _preview;
        public string Preview
        {
            get { return _preview; }
            set { _preview = value; Raise("Preview"); }
        }

        private int _unread;
        public int UnreadCount
        {
            get { return _unread; }
            set { _unread = value; Raise("UnreadCount"); Raise("UnreadVisibility"); }
        }

        private bool _muted;
        public bool Muted
        {
            get { return _muted; }
            set { _muted = value; Raise("Muted"); Raise("MutedVisibility"); }
        }

        public Visibility UnreadVisibility
        {
            get { return UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility MutedVisibility
        {
            get { return Muted ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>
        /// Two letters standing in for a picture.
        ///
        /// Not a placeholder waiting to be replaced: plenty of chats have no photo
        /// set at all, and two letters identify one where an empty square says
        /// nothing.
        /// </summary>
        public string Initials
        {
            get
            {
                string name = (Title ?? "").Trim();
                if (name.Length == 0) return "?";

                string[] words = name.Split(new[] { ' ' },
                                            StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) return "?";
                if (words.Length == 1) return words[0].Substring(0, 1).ToUpper();

                return (words[0].Substring(0, 1) +
                        words[words.Length - 1].Substring(0, 1)).ToUpper();
            }
        }
    }

    /// <summary>
    /// The chat list.
    ///
    /// Read from the server on every visit rather than cached. The server owns the
    /// unread counts, and the Silverlight client learned the hard way that keeping a
    /// local tally of them is how badges end up wrong: it took three attempts before
    /// the answer turned out to be "ask the server again".
    ///
    /// Deliberately without folders, archive, paging, avatars or live updates. Each
    /// of those is worth having and none of them is worth blocking a list that can
    /// be looked at.
    /// </summary>
    public sealed partial class ChatsPage : Page
    {
        /// <summary>
        /// How many chats to ask for. Enough to fill the screen many times over,
        /// and one request rather than several.
        /// </summary>
        private const int PageSize = 40;

        private readonly ObservableCollection<DialogItem> _dialogs =
            new ObservableCollection<DialogItem>();

        public ChatsPage()
        {
            InitializeComponent();
            DialogList.ItemsSource = _dialogs;
        }

        /// <summary>
        /// How often to re-read the chat list while it is on screen.
        ///
        /// Longer than the interval inside a conversation: this fetches every chat,
        /// its unread count and its newest message, where a conversation fetches one
        /// history. Polling at all is a stand-in for the update stream, which is a
        /// piece of work in its own right - but a chat list that never changes while
        /// being looked at is wrong in a way anyone would notice.
        /// </summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

        private DispatcherTimer _poll;
        private bool _refreshing;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Load();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            // Nothing worth fetching for a list nobody is looking at.
            if (_poll != null) _poll.Stop();
        }

        private void StartPolling()
        {
            if (_poll != null) { _poll.Start(); return; }

            _poll = new DispatcherTimer();
            _poll.Interval = PollInterval;
            _poll.Tick += delegate { Refresh(); };
            _poll.Start();
        }

        /// <summary>
        /// Brings the list up to date without disturbing it.
        ///
        /// Rows already shown are matched by peer and updated in place, so an avatar
        /// already fetched stays put. The collection is rebuilt only when the order
        /// actually changes - which is what happens when a chat receives a message
        /// and moves to the top.
        /// </summary>
        private async void Refresh()
        {
            if (_refreshing) return;
            _refreshing = true;

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Messages.DialogPage page = await Messages.GetDialogPageAsync(
                    client, PageSize, 0, 0, null, TelegramService.Info);

                int now = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0,
                                                               DateTimeKind.Utc)).TotalSeconds;

                var ordered = new List<DialogItem>();
                bool anyNew = false;

                foreach (DialogEntry d in page.Entries)
                {
                    DialogItem item = Find(d.PeerId);

                    if (item == null)
                    {
                        item = ItemFor(d, now);
                        anyNew = true;
                    }
                    else
                    {
                        item.Title = d.Title;
                        item.Preview = Shorten(d.LastText);
                        item.UnreadCount = d.UnreadCount;
                        item.Muted = d.IsMuted(now);
                    }

                    ordered.Add(item);
                }

                if (anyNew || !SameOrder(ordered))
                {
                    _dialogs.Clear();
                    foreach (DialogItem item in ordered) _dialogs.Add(item);

                    if (anyNew) FetchAvatars(client);
                }
            }
            catch (Exception)
            {
                // A failed poll is not worth painting an error over a list that is
                // fine; the next one is ten seconds away.
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SettingsPage));
        }

        private DialogItem Find(long peerId)
        {
            foreach (DialogItem item in _dialogs)
                if (item.PeerId == peerId) return item;

            return null;
        }

        private bool SameOrder(List<DialogItem> ordered)
        {
            if (ordered.Count != _dialogs.Count) return false;

            for (int i = 0; i < ordered.Count; i++)
                if (!ReferenceEquals(ordered[i], _dialogs[i])) return false;

            return true;
        }

        private DialogItem ItemFor(DialogEntry d, int now)
        {
            return new DialogItem
            {
                PeerId = d.PeerId,
                AccessHash = d.AccessHash,
                Kind = d.Kind,
                Title = d.Title,
                Preview = Shorten(d.LastText),
                UnreadCount = d.UnreadCount,
                Muted = d.IsMuted(now),
                PhotoId = d.PhotoId,
                ReadInboxMaxId = d.ReadInboxMaxId,
            };
        }

        private async void Load()
        {
            SetBusy(true, "Loading chats...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync(
                    delegate (string s) { SetBusy(true, s); });

                Messages.DialogPage page = await Messages.GetDialogPageAsync(
                    client, PageSize, 0, 0, null, TelegramService.Info);

                int now = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0,
                                                               DateTimeKind.Utc)).TotalSeconds;

                _dialogs.Clear();
                foreach (DialogEntry d in page.Entries) _dialogs.Add(ItemFor(d, now));

                SetBusy(false, _dialogs.Count == 0
                    ? "No chats."
                    : _dialogs.Count + " chats" + (page.HasMore ? " (more available)" : ""));

                FetchAvatars(client);
                StartPolling();
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not load chats: " + (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        /// <summary>
        /// Fills in the pictures once the list is already usable.
        ///
        /// After the list is shown, never before it - an avatar is worth waiting no
        /// time at all for. One at a time rather than all at once, because this is a
        /// phone on a phone network and forty simultaneous downloads would compete
        /// with the messages the user actually came for.
        /// </summary>
        private async void FetchAvatars(MtprotoClient client)
        {
            var chats = new List<DialogItem>(_dialogs);

            foreach (DialogItem chat in chats)
            {
                if (chat.PhotoId == 0) continue;

                try
                {
                    Windows.UI.Xaml.Media.Imaging.BitmapImage image =
                        await AvatarCache.GetAsync(client, chat);

                    if (image != null) chat.Avatar = image;
                }
                catch (Exception)
                {
                    // A picture that will not come is not worth reporting.
                }
            }
        }

        private void Dialog_Click(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as DialogItem;
            if (item == null) return;

            // The item itself is passed rather than its fields in a query string:
            // both pages are in this app, and reassembling a peer from text is a
            // chance to lose the access hash.
            Frame.Navigate(typeof(ConversationPage), item);
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            string one = text.Replace(Environment.NewLine, " ").Replace('\n', ' ');
            return one.Length <= 80 ? one : one.Substring(0, 77) + "...";
        }

        private void SetBusy(bool busy, string status)
        {
            Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = status ?? "";
        }
    }
}
