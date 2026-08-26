using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Lumigram.Mtproto;
using Lumigram.Phone.Services;

namespace Lumigram.Phone
{
    /// <summary>A dialog shaped for the list template.</summary>
    public sealed class DialogItem : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        /// <summary>The picture, once it has been fetched. Null until then.</summary>
        private ImageSource _avatar;
        public ImageSource Avatar
        {
            get { return _avatar; }
            set { _avatar = value; Raise("Avatar"); }
        }

        public long PhotoId { get; set; }
        public int PhotoDcId { get; set; }

        /// <summary>Date of the newest message, needed to ask for the next page.</summary>
        public int TopMessageDate { get; set; }

        /// <summary>True when this came from the archive rather than the main list.</summary>
        public bool Archived { get; set; }

        /// <summary>
        /// What the server said about this chat, kept so folder rules can be applied
        /// without copying every field a rule might read onto this class.
        /// </summary>
        public DialogEntry Entry { get; set; }

        /// <summary>Shown behind the picture, and instead of it when there is none.</summary>
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

        public long PeerId { get; set; }
        public long AccessHash { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Preview { get; set; }
        public int UnreadCount { get; set; }

        /// <summary>Muted on the account, so nothing here should announce it.</summary>
        public bool Muted { get; set; }

        /// <summary>Shown beside the title so a muted chat is recognisable at rest.</summary>
        public Visibility MutedVisibility
        {
            get { return Muted ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>
        /// Newest message seen for this chat. Used to decide whether an arriving
        /// message is genuinely new - the poll re-delivers the same messages
        /// whenever a difference overlaps, so counting blindly inflates the badge.
        /// </summary>
        public int TopMessageId { get; set; }

        public Visibility UnreadVisibility
        {
            get { return UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }
    }

    public partial class ChatsPage : PhoneApplicationPage
    {
        private readonly ObservableCollection<DialogItem> _dialogs = new ObservableCollection<DialogItem>();
        private bool _loaded;

        private DialogItem _menuTarget;
        private bool _reloadPending;

        public ChatsPage()
        {
            InitializeComponent();
            DialogList.ItemsSource = _dialogs;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Clear any selection, or returning from a conversation immediately
            // re-navigates into the same one.
            DialogList.SelectedItem = null;

            // The chat list is the root of the app: Back from here must leave, not
            // walk back through sign-in. Clearing has to happen *here* rather than in
            // the page that navigated away - doing it there runs before the
            // navigation completes, so the entries come back.
            while (NavigationService.CanGoBack) NavigationService.RemoveBackEntry();

            TelegramService.MessagesReceived += OnMessages;
            TelegramService.ConnectionLost += OnConnectionLost;
            TelegramService.Reconnected += OnReconnected;
            TelegramService.SignedOutRemotely += OnSignedOut;
            TelegramService.ChatRead += OnChatRead;
            Notifier.Banner += OnBanner;

            // The notifier announces from the service, where messages arrive; this
            // is only the lookup that turns a peer id into a name for the toast.
            Notifier.NameSource = NameFor;

            TelegramService.ClockSkewDetected += OnClockSkew;

            // The correction usually happens on the first reply after connecting,
            // which is before this page is listening. Once per run of the app, not
            // once per visit: a wrong clock is worth saying, and worth saying once.
            ShowClockWarning();
            AppState.OpenPeerId = 0;

            // Reload when coming back from a conversation. Opening a chat calls
            // messages.readHistory, so the server's unread counts have changed - and
            // the ChatRead notification fired while this page was unsubscribed,
            // because navigating away detaches the handlers. The server owns these
            // numbers, so re-reading them is both simpler and more correct than
            // trying to keep a local copy in step.
            if (!_loaded || e.NavigationMode == NavigationMode.Back) Load();

        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            TelegramService.MessagesReceived -= OnMessages;
            TelegramService.ConnectionLost -= OnConnectionLost;
            TelegramService.Reconnected -= OnReconnected;
            TelegramService.SignedOutRemotely -= OnSignedOut;
            TelegramService.ChatRead -= OnChatRead;
            Notifier.Banner -= OnBanner;
            TelegramService.ClockSkewDetected -= OnClockSkew;
        }

        private void SetBusy(bool busy, string status)
        {
            Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = status ?? "";
        }

        /// <summary>
        /// How many chats to ask for at a time.
        ///
        /// Enough that most people never need a second page, and small enough that
        /// the first one arrives quickly - it carries every title, every unread
        /// count and the newest message of each chat.
        /// </summary>

        /// <summary>
        /// Every chat read so far, whichever folder is on screen.
        ///
        /// The bound collection holds only what the selected tab shows, so the full
        /// list is kept here and filtered into it. Folders are rules over these
        /// chats rather than separate lists - only the archive is genuinely separate,
        /// and it is fetched rather than filtered.
        /// </summary>
        private readonly List<DialogItem> _all = new List<DialogItem>();

        private List<ChatFolder> _folders = new List<ChatFolder>();

        /// <summary>-1 for the main list, Folders.ArchiveFolderId for the archive,
        /// otherwise the id of a folder.</summary>
        private int _selectedFolder = MainList;

        private const int MainList = -1;

        private bool _archiveLoaded;

        /// <summary>
        /// Reads the folder list and draws the tabs.
        ///
        /// Failure is not worth reporting: a phone with no folders and a phone that
        /// could not read them look the same, and either way the main list is there.
        /// </summary>
        private async void LoadFolders(MtprotoClient client)
        {
            try
            {
                _folders = await Folders.GetAsync(client, TelegramService.Info);
            }
            catch (Exception)
            {
                _folders = new List<ChatFolder>();
            }

            BuildTabs();
        }

        private void BuildTabs()
        {
            FolderTabs.Children.Clear();

            AddTab("chats", MainList);
            foreach (ChatFolder folder in _folders) AddTab(folder.Title, folder.Id);

            // Last, because it is where things go to be out of the way.
            AddTab("archive", Folders.ArchiveFolderId);
        }

        private void AddTab(string title, int id)
        {
            int folderId = id;

            var button = new Button
            {
                Content = title,
                Margin = new Thickness(0),
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 20,
                Opacity = folderId == _selectedFolder ? 1.0 : 0.5,
            };

            button.Click += delegate { SelectFolder(folderId); };
            FolderTabs.Children.Add(button);
        }

        private async void SelectFolder(int folderId)
        {
            _selectedFolder = folderId;
            BuildTabs();

            // The archive really is a second list, so it has to be fetched the first
            // time it is opened rather than filtered out of what is already here.
            if (folderId == Folders.ArchiveFolderId && !_archiveLoaded)
            {
                await LoadArchive();
            }

            ApplyFolder();
        }

        private async Task LoadArchive()
        {
            SetBusy(true, "Loading archive...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Messages.DialogPage page = await Messages.GetDialogPageAsync(
                    client, PageSize, 0, 0, null, TelegramService.Info,
                    Folders.ArchiveFolderId);

                int now = Now();
                foreach (DialogEntry d in page.Entries)
                {
                    if (Known(d.PeerId)) continue;
                    _all.Add(ItemFor(d, now));
                }

                _archiveLoaded = true;
                SetBusy(false, null);

                FetchAvatars(client);
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not load the archive: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        /// <summary>Rebuilds the visible list for whichever tab is selected.</summary>
        private void ApplyFolder()
        {
            int now = Now();

            _dialogs.Clear();
            foreach (DialogItem item in _all)
            {
                if (!Shows(item, now)) continue;
                _dialogs.Add(item);
            }

            // Paging applies to the main list; a folder shows what is already loaded.
            LoadMoreButton.Visibility = _hasMore && _selectedFolder == MainList
                ? Visibility.Visible : Visibility.Collapsed;

            UpdateTile();
        }

        private bool Shows(DialogItem item, int now)
        {
            if (_selectedFolder == MainList) return !item.Archived;
            if (_selectedFolder == Folders.ArchiveFolderId) return item.Archived;

            foreach (ChatFolder folder in _folders)
            {
                if (folder.Id != _selectedFolder) continue;
                return Folders.Contains(folder, item.Entry, now);
            }

            return false;
        }

        private static int Now()
        {
            return (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0,
                                                        DateTimeKind.Utc)).TotalSeconds;
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
                TopMessageId = d.TopMessageId,
                TopMessageDate = d.TopMessageDate,
                Muted = d.IsMuted(now),
                PhotoId = d.PhotoId,
                PhotoDcId = d.PhotoDcId,
                Archived = d.Archived,
                Entry = d,
            };
        }

        private const int PageSize = 40;

        private bool _hasMore;
        private bool _loadingMore;

        /// <summary>
        /// Appends the next page of chats.
        ///
        /// Continues from the last entry rather than asking for a bigger list, so
        /// what has already been read is not fetched and rebuilt a second time.
        /// </summary>
        private async void LoadMore_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingMore || _dialogs.Count == 0) return;

            _loadingMore = true;
            LoadMoreButton.IsEnabled = false;
            SetBusy(true, "Loading more chats...");

            try
            {
                DialogItem last = _dialogs[_dialogs.Count - 1];

                MtprotoClient client = await TelegramService.ConnectAsync();

                Messages.DialogPage page = await Messages.GetDialogPageAsync(
                    client, PageSize, last.TopMessageDate, last.TopMessageId,
                    Messages.InputPeerFor(last.Kind, last.PeerId, last.AccessHash),
                    TelegramService.Info);

                int added = 0;
                int now = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0,
                                                               DateTimeKind.Utc)).TotalSeconds;

                foreach (DialogEntry d in page.Entries)
                {
                    // The entry the offset was taken from comes back as the first of
                    // the next page.
                    if (Known(d.PeerId)) continue;

                    _all.Add(ItemFor(d, now));
                    added++;
                }

                // A page that adds nothing new means the end, whatever it says.
                _hasMore = page.HasMore && added > 0;

                ApplyFolder();
                SetBusy(false, added == 0 ? "No more chats." : null);
                FetchAvatars(client);
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not load more: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
            finally
            {
                _loadingMore = false;
                LoadMoreButton.IsEnabled = true;
                ShowLoadMore();
            }
        }

        private bool Known(long peerId)
        {
            foreach (DialogItem d in _all) if (d.PeerId == peerId) return true;
            return false;
        }

        private void ShowLoadMore()
        {
            // Paging is a property of the main list, so the button follows the tab
            // as well as whether there is more to fetch.
            LoadMoreButton.Visibility = _hasMore && _selectedFolder == MainList
                ? Visibility.Visible : Visibility.Collapsed;
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

                List<DialogEntry> entries = page.Entries;
                _hasMore = page.HasMore;

                // The server owns the mute settings; this only mirrors them so the
                // notifier and the background agent can both reach the same answer.
                int now = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0,
                                                               DateTimeKind.Utc)).TotalSeconds;
                var muted = new List<long>();
                foreach (DialogEntry d in entries) if (d.IsMuted(now)) muted.Add(d.PeerId);
                MuteStore.Replace(muted);

                _all.Clear();
                int now2 = Now();
                foreach (DialogEntry d in entries) _all.Add(ItemFor(d, now2));

                _archiveLoaded = false;
                ApplyFolder();

                _loaded = true;
                LoadFolders(client);

                // After the list is on screen, never before it: an avatar is worth
                // waiting no time at all for, and fetching them first would hold the
                // whole chat list behind a row of pictures.
                FetchAvatars(client);

                // Load fills the collection directly rather than going through
                // Refresh, so the tile has to be told here too - and this is the
                // moment that matters most, being the one time the counts come
                // straight from the server.
                UpdateTile();
                SetBusy(false, entries.Count == 0 ? "No chats yet." : null);

                TelegramService.SaveSalt();
            }
            catch (RpcException ex)
            {
                if ((ex.ErrorType ?? "").Contains("AUTH_KEY_UNREGISTERED") ||
                    (ex.ErrorType ?? "").Contains("SESSION_REVOKED") ||
                    (ex.ErrorType ?? "").Contains("USER_DEACTIVATED"))
                {
                    // The stored key is no longer valid - most likely the session was
                    // revoked from another device. Keeping it would fail every call.
                    PhoneSession.Delete();
                    TelegramService.Disconnect();
                    NavigationService.Navigate(new Uri("/LoginPage.xaml", UriKind.Relative));
                    return;
                }
                SetBusy(false, ex.ErrorType);
            }
            catch (Exception ex)
            {
                SetBusy(false, ex.Message);
            }
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace((char)10, ' ').Replace((char)13, ' ');
            return text.Length > 70 ? text.Substring(0, 70) + "..." : text;
        }

        private void OnMessages(List<TextMessage> messages)
        {
            // Updates arrive on the receive loop, not the UI thread.
            Dispatcher.BeginInvoke(delegate
            {
                bool unknownPeer = false;

                foreach (TextMessage m in messages)
                {
                    bool matched = false;

                    // Everything loaded, not just the tab on screen: a message for
                    // a chat in another folder is still known, and treating it as
                    // unknown would reload the whole list for nothing.
                    foreach (DialogItem d in _all)
                    {
                        if (d.PeerId != m.PeerId && d.PeerId != m.FromId) continue;

                        // Only count what we have not already counted. Anything at or
                        // below the newest id we know about has been seen before -
                        // getDifference happily returns the same message twice.
                        matched = true;

                        if (m.Id <= d.TopMessageId) continue;
                        d.TopMessageId = m.Id;

                        if (!string.IsNullOrEmpty(m.Text)) d.Preview = Shorten(m.Text);
                        else if (m.Media != null) d.Preview = m.Media.Describe();

                        // Our own messages are not unread, and neither is a chat the
                        // user is currently looking at.
                        if (!m.Out && d.PeerId != AppState.OpenPeerId) d.UnreadCount++;
                    }

                    if (!matched) unknownPeer = true;
                }

                // A message from a chat we do not have means the list itself is out
                // of date - a new conversation, or one that was deleted and has just
                // come back. Only the server knows the whole list, so re-read it.
                if (unknownPeer) ReloadSoon();

                Refresh();
            });
        }

        /// <summary>
        /// Reloads the chat list shortly, coalescing bursts.
        ///
        /// A batch of updates can mention several unknown chats at once; reloading
        /// per message would fire a handful of getDialogs calls back to back.
        /// </summary>
        private void ReloadSoon()
        {
            if (_reloadPending) return;
            _reloadPending = true;

            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += delegate
            {
                timer.Stop();
                _reloadPending = false;
                Load();
            };
            timer.Start();
        }

        // (char)10 rather than an escape: backslashes do not survive the tooling
        // used to edit this file.
        private static readonly string NewLine = ((char)10).ToString();

        private void OnSignedOut()
        {
            Dispatcher.BeginInvoke(delegate
            {
                MessageBox.Show(
                    "This device is no longer signed in to Telegram." +
                    NewLine + NewLine +
                    "The session may have been ended from another device.",
                    "Signed out", MessageBoxButton.OK);

                NavigationService.Navigate(new Uri("/LoginPage.xaml", UriKind.Relative));
                while (NavigationService.CanGoBack) NavigationService.RemoveBackEntry();
            });
        }

        /// <summary>Zeroes the badge once the server has been told the chat was read.</summary>
        private void OnChatRead(long peerId)
        {
            Dispatcher.BeginInvoke(delegate
            {
                foreach (DialogItem d in _all)
                {
                    if (d.PeerId != peerId) continue;
                    if (d.UnreadCount == 0) return;
                    d.UnreadCount = 0;
                }
                Refresh();
            });
        }

        /// <summary>
        /// Rebuilds the list so the template picks up changed values.
        ///
        /// DialogItem is a plain object with no change notification, so mutating one
        /// in place does not update the row. Rebuilding is crude and flickers; it is
        /// the honest cost of not having implemented INotifyPropertyChanged here.
        /// </summary>
        private void Refresh()
        {
            var items = new List<DialogItem>(_dialogs);
            _dialogs.Clear();
            foreach (DialogItem d in items) _dialogs.Add(d);

            UpdateTile();
        }

        /// <summary>
        /// Puts the current unread picture on the start screen tile.
        ///
        /// Driven from here because this list holds the server's own unread counts,
        /// which is the only authoritative source. Anything the service added while
        /// no list was loaded is replaced, not added to.
        /// </summary>
        private void UpdateTile()
        {
            var chats = new List<TileChat>();

            foreach (DialogItem d in _all)
            {
                if (d.UnreadCount <= 0) continue;
                chats.Add(new TileChat { PeerId = d.PeerId, Title = d.Title });
            }

            try { LiveTile.Set(chats); }
            catch (Exception) { }
        }

        /// <summary>
        /// Fills in the pictures once the list is already usable.
        ///
        /// One at a time rather than all at once: this is a phone on a phone
        /// network, and forty simultaneous downloads would compete with the messages
        /// the user actually came for. Anything already cached costs nothing.
        /// </summary>
        private async void FetchAvatars(MtprotoClient client)
        {
            var items = new List<DialogItem>(_all);

            foreach (DialogItem d in items)
            {
                if (d.PhotoId == 0) continue;

                ImageSource cached = AvatarCache.Get(d.PhotoId);
                if (cached != null) { d.Avatar = cached; continue; }

                try
                {
                    var peer = new PeerInfo
                    {
                        Id = d.PeerId,
                        AccessHash = d.AccessHash,
                        PhotoId = d.PhotoId,
                        PhotoDcId = d.PhotoDcId,
                        Name = d.Title,
                    };

                    ImageSource image = await AvatarCache.FetchAsync(client, peer, d.Kind);
                    if (image != null) d.Avatar = image;
                }
                catch (Exception)
                {
                    // A picture that will not come is not worth reporting.
                }
            }
        }

        /// <summary>Chat title for a peer id, for notification text.</summary>
        private string NameFor(long peerId)
        {
            foreach (DialogItem d in _all)
                if (d.PeerId == peerId) return d.Title;
            return null;
        }

        private void OnReconnected()
        {
            Dispatcher.BeginInvoke(delegate { Load(); });
        }

        private void OnConnectionLost(string reason)
        {
            Dispatcher.BeginInvoke(delegate { SetBusy(false, "Disconnected: " + reason); });
        }

        private void DialogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = DialogList.SelectedItem as DialogItem;
            if (item == null) return;

            AppState.OpenPeerId = item.PeerId;

            NavigationService.Navigate(new Uri(
                "/ConversationPage.xaml?peer=" + item.PeerId +
                "&hash=" + item.AccessHash +
                "&kind=" + item.Kind +
                "&title=" + Uri.EscapeDataString(item.Title ?? ""), UriKind.Relative));
        }

        private void Dialog_Hold(object sender, System.Windows.Input.GestureEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            _menuTarget = element.DataContext as DialogItem;
            if (_menuTarget == null) return;

            ActionTitle.Text = _menuTarget.Title;

            // Every kind of chat, now that groups and channels notify like the rest
            // rather than being silenced unconditionally.
            MuteButton.Content = _menuTarget.Muted ? "unmute" : "mute";
            ArchiveButton.Content = _menuTarget.Archived ? "unarchive" : "archive";

            BuildFolderToggles(_menuTarget);

            ActionOverlay.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Mutes or unmutes the chat the menu was opened on.
        ///
        /// The local copy is updated before the round trip so the list reflects the
        /// tap at once; a failure puts it back, since a chat that looks muted and is
        /// not is worse than one that never appeared to change.
        /// </summary>
        private async void Mute_Click(object sender, RoutedEventArgs e)
        {
            DialogItem target = _menuTarget;
            ActionOverlay.Visibility = Visibility.Collapsed;
            _menuTarget = null;

            if (target == null) return;

            bool muted = !target.Muted;

            target.Muted = muted;
            MuteStore.Set(target.PeerId, muted);
            Refresh();

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();
                await Messages.SetMutedAsync(client, target.Kind, target.PeerId,
                                             target.AccessHash, muted, TelegramService.Info);
            }
            catch (Exception ex)
            {
                target.Muted = !muted;
                MuteStore.Set(target.PeerId, !muted);
                Refresh();

                var rpc = ex as RpcException;
                SetBusy(false, "Could not change mute: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        /// <summary>
        /// A button per editable folder, showing whether this chat is in it.
        ///
        /// Shared folders are left out: they belong to whoever shared them, and
        /// editing one here would edit it for everybody.
        /// </summary>
        private void BuildFolderToggles(DialogItem target)
        {
            FolderToggles.Children.Clear();

            foreach (ChatFolder folder in _folders)
            {
                if (!folder.Editable) continue;

                ChatFolder which = folder;
                bool inside = folder.Include.Contains(target.PeerId) ||
                              folder.Pinned.Contains(target.PeerId);

                var button = new Button
                {
                    Content = (inside ? "in " : "add to ") + folder.Title,
                    Opacity = inside ? 1.0 : 0.65,
                };

                button.Click += delegate { ToggleFolder(target, which, !inside); };
                FolderToggles.Children.Add(button);
            }
        }

        private async void ToggleFolder(DialogItem target, ChatFolder folder, bool member)
        {
            ActionOverlay.Visibility = Visibility.Collapsed;
            _menuTarget = null;

            SetBusy(true, (member ? "Adding to " : "Removing from ") + folder.Title + "...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                await Folders.SetMembershipAsync(
                    client, folder,
                    Messages.InputPeerFor(target.Kind, target.PeerId, target.AccessHash),
                    target.PeerId, member, TelegramService.Info);

                SetBusy(false, null);
                ApplyFolder();
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not change the folder: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));

                // The copy in memory has already been changed and the server has
                // not, so the two now disagree. Re-reading is the only way back to
                // the truth - and it needs its own connection, since the failure may
                // well have been the previous one going away.
                try
                {
                    MtprotoClient fresh = await TelegramService.ConnectAsync();
                    LoadFolders(fresh);
                }
                catch (Exception)
                {
                    // Nothing more to try. The next refresh will put it right.
                }
            }
        }

        /// <summary>Moves a chat into the archive, or back out of it.</summary>
        private async void Archive_Click(object sender, RoutedEventArgs e)
        {
            DialogItem target = _menuTarget;
            ActionOverlay.Visibility = Visibility.Collapsed;
            _menuTarget = null;

            if (target == null) return;

            bool archive = !target.Archived;
            SetBusy(true, archive ? "Archiving..." : "Unarchiving...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                await Folders.SetArchivedAsync(
                    client,
                    Messages.InputPeerFor(target.Kind, target.PeerId, target.AccessHash),
                    archive, TelegramService.Info);

                target.Archived = archive;
                if (target.Entry != null) target.Entry.Archived = archive;

                SetBusy(false, null);
                ApplyFolder();
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not " + (archive ? "archive" : "unarchive") + ": " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        private void CancelMenu_Click(object sender, RoutedEventArgs e)
        {
            ActionOverlay.Visibility = Visibility.Collapsed;
            _menuTarget = null;
        }

        private void ActionOverlay_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            // Tapping the dimmed area behind the panel dismisses it.
            ActionOverlay.Visibility = Visibility.Collapsed;
            _menuTarget = null;
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            DialogItem target = _menuTarget;
            ActionOverlay.Visibility = Visibility.Collapsed;
            _menuTarget = null;
            if (target == null) return;

            if (MessageBox.Show(
                    "Delete all messages in this chat?" + NewLine + NewLine +
                    "This only affects your copy. The other side keeps theirs.",
                    "Clear " + target.Title, MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                return;

            RunHistoryAction(target, true);
        }

        private void DeleteChat_Click(object sender, RoutedEventArgs e)
        {
            DialogItem target = _menuTarget;
            ActionOverlay.Visibility = Visibility.Collapsed;
            _menuTarget = null;
            if (target == null) return;

            if (MessageBox.Show(
                    "Delete this chat and all its messages?" + NewLine + NewLine +
                    "The chat is removed from your list. This cannot be undone.",
                    "Delete " + target.Title, MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                return;

            RunHistoryAction(target, false);
        }

        /// <summary>
        /// Clears or deletes, then reloads.
        ///
        /// revoke is deliberately false: deleting the other participant copy is a
        /// far bigger action than the menu implies, and is not something to do on a
        /// long tap.
        /// </summary>
        private async void RunHistoryAction(DialogItem target, bool justClear)
        {
            SetBusy(true, justClear ? "Clearing..." : "Deleting...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();
                byte[] peer = Messages.InputPeerFor(target.Kind, target.PeerId, target.AccessHash);

                await Messages.DeleteHistoryAsync(client, peer, justClear, false,
                                                  TelegramService.Info);

                if (justClear)
                {
                    // Re-read rather than assume. Whether an emptied chat stays in
                    // the list is the server's decision, not ours, and guessing
                    // leaves the UI disagreeing with reality.
                    target.Preview = "";
                    target.UnreadCount = 0;
                    Refresh();
                    Load();
                }
                else
                {
                    _dialogs.Remove(target);
                    SetBusy(false, null);
                }
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, rpc != null ? rpc.ErrorType : ex.Message);
            }
        }

        private void Refresh_Click(object sender, EventArgs e)
        {
            Load();
        }


        private long _bannerPeerId;
        private System.Windows.Threading.DispatcherTimer _bannerTimer;

        /// <summary>
        /// Shows an in-app banner for an arriving message.
        ///
        /// Needed because Windows Phone suppresses a toast raised by the app that
        /// is currently on screen - without this the foreground case is silent.
        /// Runs on the receive loop, so it marshals to the UI thread itself.
        /// </summary>
        private void OnBanner(string title, string body, long peerId)
        {
            Dispatcher.BeginInvoke(delegate
            {
                _bannerPeerId = peerId;
                BannerTitle.Text = title;
                BannerBody.Text = body;
                BannerPanel.Visibility = Visibility.Visible;

                if (_bannerTimer == null)
                {
                    _bannerTimer = new System.Windows.Threading.DispatcherTimer();
                    _bannerTimer.Interval = TimeSpan.FromSeconds(4);
                    _bannerTimer.Tick += delegate
                    {
                        _bannerTimer.Stop();
                        BannerPanel.Visibility = Visibility.Collapsed;
                    };
                }

                // Restart the timer so a second message extends the banner rather
                // than inheriting the remainder of the first one's time.
                _bannerTimer.Stop();
                _bannerTimer.Start();
            });
        }

        /// <summary>Tapping the banner opens the chat it came from.</summary>
        private static bool _clockNoticeShown;

        private void OnClockSkew(int offset)
        {
            ShowClockWarning();
        }

        private void ShowClockWarning()
        {
            if (_clockNoticeShown) return;

            string message = TelegramService.ClockWarning();
            if (message == null) return;

            _clockNoticeShown = true;
            OnBanner("Clock", message, 0);
        }

        private void Banner_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            BannerPanel.Visibility = Visibility.Collapsed;
            if (_bannerTimer != null) _bannerTimer.Stop();

            long peer = _bannerPeerId;
            if (peer == 0) return;

            OpenChat(peer);
        }

        /// <summary>Navigates to a chat by peer id, when we know it.</summary>
        private void OpenChat(long peerId)
        {
            foreach (DialogItem d in _all)
            {
                if (d.PeerId != peerId) continue;

                AppState.OpenPeerId = d.PeerId;
                NavigationService.Navigate(new Uri(
                    "/ConversationPage.xaml?peer=" + d.PeerId +
                    "&hash=" + d.AccessHash +
                    "&kind=" + d.Kind +
                    "&title=" + Uri.EscapeDataString(d.Title ?? ""), UriKind.Relative));
                return;
            }
        }

        private void NewChat_Click(object sender, EventArgs e)
        {
            NavigationService.Navigate(new Uri("/NewChatPage.xaml", UriKind.Relative));
        }

        private void Settings_Click(object sender, EventArgs e)
        {
            NavigationService.Navigate(new Uri("/SettingsPage.xaml", UriKind.Relative));
        }

        private void Exit_Click(object sender, EventArgs e)
        {
            AppExit.Quit();
        }
    }
}
