using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>One message in a conversation.</summary>
    public sealed class MessageItem : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        public int Id { get; set; }
        public string Text { get; set; }
        public string Time { get; set; }
        public bool Out { get; set; }
        public string SenderName { get; set; }

        /// <summary>The attachment, when there is one.</summary>
        public MediaInfo Media { get; set; }

        private string _mediaNote;
        public string MediaNote
        {
            get { return _mediaNote; }
            set { _mediaNote = value; Raise("MediaNote"); Raise("MediaNoteVisibility"); }
        }

        private ImageSource _picture;
        public ImageSource Picture
        {
            get { return _picture; }
            set { _picture = value; Raise("Picture"); Raise("PictureVisibility"); Raise("MediaNoteVisibility"); }
        }

        public Visibility PictureVisibility
        {
            get { return _picture == null ? Visibility.Collapsed : Visibility.Visible; }
        }

        /// <summary>
        /// The caption describing an attachment, shown only while there is no
        /// picture to show instead.
        /// </summary>
        public Visibility MediaNoteVisibility
        {
            get
            {
                return _picture == null && !string.IsNullOrEmpty(_mediaNote)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>Hidden when a message is only an attachment.</summary>
        public Visibility TextVisibility
        {
            get { return string.IsNullOrEmpty(Text) ? Visibility.Collapsed : Visibility.Visible; }
        }

        /// <summary>
        /// Whether the gallery is a sensible destination for this attachment.
        ///
        /// Pictures and videos have a place there; a document does not, and offering
        /// to put one in the gallery is offering something that cannot work.
        /// </summary>
        public Visibility GalleryVisibility
        {
            get
            {
                bool media = Media != null &&
                             (Media.Kind == MediaKind.Photo || Media.Kind == MediaKind.Video);

                return media ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>Guards against a second fetch while one is already running.</summary>
        public bool Loading { get; set; }

        /// <summary>True for the oldest message that had not been read.</summary>
        public bool FirstUnread { get; set; }

        public Visibility UnreadMarkVisibility
        {
            get { return FirstUnread ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility SenderVisibility
        {
            get
            {
                return string.IsNullOrEmpty(SenderName) ? Visibility.Collapsed
                                                        : Visibility.Visible;
            }
        }

        /// <summary>Ours on the right, theirs on the left - the usual convention.</summary>
        public HorizontalAlignment Align
        {
            get { return Out ? HorizontalAlignment.Right : HorizontalAlignment.Left; }
        }

        /// <summary>
        /// Leaves room on the opposite side so a bubble cannot run the full width,
        /// which is what makes the two directions readable at a glance.
        /// </summary>
        public Thickness Margin
        {
            get { return Out ? new Thickness(48, 3, 0, 3) : new Thickness(0, 3, 48, 3); }
        }

        public Brush Background
        {
            get
            {
                return Out
                    ? new SolidColorBrush(Color.FromArgb(255, 0, 120, 215))
                    : new SolidColorBrush(Color.FromArgb(255, 60, 60, 60));
            }
        }
    }

    /// <summary>
    /// One conversation: what has been said, and saying something.
    ///
    /// History only, with no live updates yet - a message sent from elsewhere will
    /// not appear until the page is reopened. That needs the update machinery, which
    /// is a piece of work in its own right and not a reason to withhold a
    /// conversation that can be read and replied to.
    /// </summary>
    public sealed partial class ConversationPage : Page, IFileContinuation
    {
        /// <summary>How much history to fetch. A screenful several times over.</summary>
        private const int HistoryCount = 30;

        private readonly ObservableCollection<MessageItem> _messages =
            new ObservableCollection<MessageItem>();

        /// <summary>
        /// Who has written here, by id.
        ///
        /// A message names its sender by id and nothing else, so without the users
        /// vector that arrives alongside the history, every group message is
        /// anonymous.
        /// </summary>
        private readonly Dictionary<long, PeerInfo> _senders = new Dictionary<long, PeerInfo>();

        private DialogItem _peer;
        private byte[] _inputPeer;

        public ConversationPage()
        {
            InitializeComponent();
            MessageList.ItemsSource = _messages;

            // Take charge of what happens when the keyboard appears.
            //
            // By default the system resizes the window to keep the focused box in
            // view, and that resize is why the first tap on Send did nothing: the
            // tap dismisses the keyboard, the page re-lays out, and the button moves
            // out from under the finger before the press is delivered. Saying the
            // element is already in view stops the automatic resize; the compose bar
            // is lifted above the keyboard here instead, so nothing moves when it
            // goes away.
            InputPane pane = InputPane.GetForCurrentView();
            pane.Showing += OnKeyboardShowing;
            pane.Hiding += OnKeyboardHiding;
        }

        private void OnKeyboardShowing(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            args.EnsuredFocusedElementInView = true;
            Root.Margin = new Thickness(0, 0, 0, args.OccludedRect.Height);
        }

        private void OnKeyboardHiding(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            args.EnsuredFocusedElementInView = true;
            Root.Margin = new Thickness(0);
        }

        /// <summary>
        /// How often to look for new messages while the chat is on screen.
        ///
        /// Polling, not the update stream. The proper machinery keeps a connection
        /// listening and applies pushed updates; this is a fraction of the work and
        /// makes the difference between a conversation that is live and one that has
        /// to be reopened to see a reply. It runs only while the page is showing.
        /// </summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        private DispatcherTimer _poll;

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            if (_poll != null) _poll.Stop();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _peer = e.Parameter as DialogItem;
            if (_peer == null)
            {
                SetBusy(false, "No chat to open.");
                return;
            }

            PeerTitle.Text = _peer.Title ?? "chat";
            _inputPeer = Messages.InputPeerFor(_peer.Kind, _peer.PeerId, _peer.AccessHash);

            Load();
        }

        private async void Load()
        {
            SetBusy(true, "Loading messages...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Messages.History history = await Messages.GetHistoryAsync(
                    client, _inputPeer, HistoryCount, TelegramService.Info);

                foreach (KeyValuePair<long, PeerInfo> pair in history.Senders)
                    _senders[pair.Key] = pair.Value;

                _messages.Clear();

                // getHistory returns newest first; a conversation reads oldest first.
                for (int i = history.Messages.Count - 1; i >= 0; i--)
                    Add(history.Messages[i]);

                SetBusy(false, _messages.Count == 0 ? "No messages yet." : "");

                OpenWhereReadingStopped();

                MarkRead(client, history.Messages);
                StartPolling();
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not load: " + (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        /// <summary>
        /// Tells the server the chat has been read.
        ///
        /// The unread count belongs to the server, not to this app. Zeroing the badge
        /// locally makes it come back on the next chat list load, and leaves the chat
        /// showing unread on every other device the account is signed in on.
        ///
        /// Channels take a different method to everything else, which Core handles -
        /// sending the wrong one fails silently and the badge simply never clears.
        /// </summary>
        private async void MarkRead(MtprotoClient client, List<TextMessage> history)
        {
            int maxId = 0;
            foreach (TextMessage m in history) if (m.Id > maxId) maxId = m.Id;

            if (maxId == 0) return;

            try
            {
                await Messages.MarkReadAsync(client, _peer.Kind, _peer.PeerId,
                                             _peer.AccessHash, maxId, TelegramService.Info);

                // So the list shows the change on the way back without a round trip.
                _peer.UnreadCount = 0;
            }
            catch (Exception)
            {
                // Not worth interrupting reading for; the badge stays until next time.
            }
        }

        /// <summary>
        /// Positions the conversation on the first message not yet read.
        ///
        /// The read marker comes from the chat list and is read before the chat is
        /// marked read, which is the only moment it still means anything - opening
        /// the chat is what moves it.
        ///
        /// Falls back to the newest message: with nothing unread, or with the unread
        /// run older than the history fetched, the bottom is where a reader wants to
        /// be anyway.
        /// </summary>
        private void OpenWhereReadingStopped()
        {
            int readTo = _peer != null ? _peer.ReadInboxMaxId : 0;

            MessageItem first = null;
            foreach (MessageItem item in _messages)
            {
                if (item.Out || item.Id == 0 || item.Id <= readTo) continue;

                first = item;
                break;
            }

            if (first == null) { ScrollToEnd(); return; }

            first.FirstUnread = true;

            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low,
                delegate
                {
                    MessageList.UpdateLayout();
                    MessageList.ScrollIntoView(first);
                });
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
        /// Adds anything that has arrived since the last look.
        ///
        /// Matched by message id rather than by count or by text: the same history
        /// is fetched every time, so anything else either re-adds what is already
        /// shown or merges two identical messages that were genuinely sent twice.
        /// </summary>
        private async void Refresh()
        {
            if (_refreshing) return;
            _refreshing = true;

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Messages.History history = await Messages.GetHistoryAsync(
                    client, _inputPeer, HistoryCount, TelegramService.Info);

                foreach (KeyValuePair<long, PeerInfo> pair in history.Senders)
                    _senders[pair.Key] = pair.Value;

                bool added = false;

                for (int i = history.Messages.Count - 1; i >= 0; i--)
                {
                    TextMessage m = history.Messages[i];
                    if (m.Id == 0 || Contains(m.Id)) continue;

                    Add(m);
                    added = true;
                }

                if (added)
                {
                    ScrollToEnd();
                    MarkRead(client, history.Messages);
                }
            }
            catch (Exception)
            {
                // A failed poll is not worth reporting: the next one is five seconds
                // away, and a connection blip would otherwise paint an error over a
                // conversation that is fine.
            }
            finally
            {
                _refreshing = false;
            }
        }

        private bool _refreshing;

        private bool Contains(int id)
        {
            foreach (MessageItem item in _messages)
                if (item.Id == id) return true;

            return false;
        }

        private void Add(TextMessage m)
        {
            var item = new MessageItem
            {
                Id = m.Id,
                Text = m.Text ?? "",
                Time = m.DateUtc.ToLocalTime().ToString("HH:mm"),
                Out = m.Out,
                SenderName = SenderFor(m),
                Media = m.Media,
            };

            if (m.Media != null)
            {
                // Photos are cheap enough to fetch on sight when the setting allows
                // it. Everything else waits to be asked for: a document can be any
                // size at all, and this is a phone on a phone network.
                item.MediaNote = m.Media.Describe() + " - tap to load";
            }

            _messages.Add(item);

            // Anything already fetched is shown without asking; anything else is
            // fetched now or on tap, depending on the setting.
            if (m.Media != null && m.Media.Kind == MediaKind.Photo)
            {
                if (AppSettings.AutoLoadPhotos) LoadMedia(item);
                else ShowIfCached(item);
            }
        }

        /// <summary>Shows a picture that is already on disk, without fetching.</summary>
        private async void ShowIfCached(MessageItem item)
        {
            try
            {
                Windows.Storage.StorageFile file = await MediaCache.FindAsync(item.Media);
                if (file == null) return;

                item.Picture = new Windows.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri("ms-appdata:///local/media/" + file.Name));
            }
            catch (Exception)
            {
                // Nothing to show; the caption stays.
            }
        }

        /// <summary>
        /// Fetches the picture behind a message and shows it.
        ///
        /// Progress is reported into the caption rather than a bar: on a slow
        /// connection a photo is tens of seconds, and a message that says nothing
        /// for that long reads as broken.
        /// </summary>
        private async void LoadMedia(MessageItem item)
        {
            if (item.Media == null || item.Picture != null) return;
            if (item.Loading) return;

            item.Loading = true;
            string caption = item.MediaNote;

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Uri uri = await MediaCache.GetAsync(client, item.Media,
                    delegate (long got, long total)
                    {
                        if (total <= 0) return;

                        var ignored = Dispatcher.RunAsync(
                            Windows.UI.Core.CoreDispatcherPriority.Low,
                            delegate { item.MediaNote = "loading " + (got * 100 / total) + "%"; });
                    });

                if (uri == null)
                {
                    item.MediaNote = caption;
                    return;
                }

                if (item.Media.Kind == MediaKind.Photo)
                {
                    item.Picture = new Windows.UI.Xaml.Media.Imaging.BitmapImage(uri);
                    return;
                }

                // Nothing to draw for a document, so say it is here and what can be
                // done with it - otherwise a finished download looks like nothing
                // happened.
                item.MediaNote = item.Media.Kind == MediaKind.Video
                    ? item.Media.Describe() + " - tap to play"
                    : item.Media.Describe() + " - hold to save";
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                item.MediaNote = "could not load: " + (rpc != null ? rpc.ErrorType : ex.Message);
            }
            finally
            {
                item.Loading = false;
            }
        }

        /// <summary>
        /// Copies a picture into the phone's own gallery.
        ///
        /// SavedPictures rather than the app's storage: a picture kept where only
        /// this app can see it has not really been saved. Requires the pictures
        /// library capability, without which the folder simply is not there.
        /// </summary>
        private async void SavePicture(MessageItem item)
        {
            if (item == null || item.Media == null) return;

            bool photo = item.Media.Kind == MediaKind.Photo;
            bool video = item.Media.Kind == MediaKind.Video;

            if (!photo && !video)
            {
                // Should not be reachable - the menu entry is hidden for documents -
                // but a guess about where a file belongs is worth refusing rather
                // than acting on.
                item.MediaNote = "use save as... for this file";
                return;
            }

            try
            {
                Windows.Storage.StorageFile file = await MediaCache.FindAsync(item.Media);

                if (file == null)
                {
                    item.MediaNote = "load it first, then save";
                    return;
                }

                item.MediaNote = "saving...";

                // Each library takes its own kind. A video written into the pictures
                // library is accepted and then shown by nothing.
                Windows.Storage.StorageFolder target = photo
                    ? Windows.Storage.KnownFolders.SavedPictures
                    : Windows.Storage.KnownFolders.VideosLibrary;

                await file.CopyAsync(target, "lumigram-" + file.Name,
                                     Windows.Storage.NameCollisionOption.ReplaceExisting);

                item.MediaNote = "saved to the gallery";
            }
            catch (Exception ex)
            {
                item.MediaNote = "not saved: " + ex.Message;
            }
        }

        /// <summary>
        /// Opens the save menu on a press and hold.
        ///
        /// An attached flyout does not show itself - something has to ask - and the
        /// Started phase is the moment to do it: waiting for Completed means the
        /// menu appears only after the finger lifts.
        /// </summary>
        private void Media_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;

            var element = sender as FrameworkElement;
            if (element == null) return;

            Windows.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(element);
        }

        /// <summary>Opens a downloaded picture full screen, where it can be zoomed.</summary>
        private async void Media_DoubleTap(object sender,
                                           Windows.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            var item = element.DataContext as MessageItem;
            if (item == null || item.Media == null) return;

            Windows.Storage.StorageFile file = await MediaCache.FindAsync(item.Media);
            if (file == null) return;

            Frame.Navigate(typeof(ImageViewerPage), file.Name);
        }

        private void SaveMenu_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            SavePicture(element.DataContext as MessageItem);
        }

        private void Media_Tap(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            var item = element.DataContext as MessageItem;
            if (item == null || item.Media == null) return;

            PlayOrLoad(item);
        }

        /// <summary>
        /// Plays a video that is already here, or fetches whatever is not.
        ///
        /// One tap does the obvious thing at each stage: the first fetches, and the
        /// next plays. Two gestures for the two halves of one action would be
        /// something to learn rather than something to use.
        /// </summary>
        private async void PlayOrLoad(MessageItem item)
        {
            if (item.Media != null && item.Media.Kind == MediaKind.Video)
            {
                Windows.Storage.StorageFile file = await MediaCache.FindAsync(item.Media);

                if (file != null)
                {
                    Frame.Navigate(typeof(VideoPage), file.Name);
                    return;
                }
            }

            LoadMedia(item);
        }

        private string SenderFor(TextMessage m)
        {
            if (m.Out || _peer.Kind == "user") return "";
            if (m.FromId == 0) return "";

            PeerInfo sender;
            if (_senders.TryGetValue(m.FromId, out sender)) return sender.Name;

            return "user " + m.FromId;
        }

        private void Compose_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;

            e.Handled = true;
            Send();
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            Send();
        }

        /// <summary>
        /// Offers any file to send.
        ///
        /// Every type, not only pictures: sending arbitrary files is one of the two
        /// reasons this client exists. The picker suspends the app, so the answer
        /// arrives in FilePicked rather than here.
        /// </summary>
        private void Attach_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
            picker.SuggestedStartLocation =
                Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;

            // The picker refuses to open with no filter at all; * is how "anything"
            // is spelled.
            picker.FileTypeFilter.Add("*");

            picker.PickSingleFileAndContinue();
        }

        /// <summary>The chosen file comes back here, after the app has been reactivated.</summary>
        public async void FilePicked(Windows.ApplicationModel.Activation.FileOpenPickerContinuationEventArgs args)
        {
            if (args == null || args.Files == null || args.Files.Count == 0) return;

            Windows.Storage.StorageFile file = args.Files[0];
            await SendFileAsync(file);
        }

        /// <summary>
        /// Uploads a file and sends it.
        ///
        /// Sent as a photo when it is one and as a document otherwise - the
        /// difference is what other clients show inline versus offer as a download,
        /// and getting it wrong makes a picture arrive as an attachment nobody can
        /// see without saving it first.
        /// </summary>
        private async Task SendFileAsync(Windows.Storage.StorageFile file)
        {
            var pending = new MessageItem
            {
                Id = 0,
                Text = "",
                Time = DateTime.Now.ToString("HH:mm"),
                Out = true,
                MediaNote = "sending " + file.Name + "...",
            };

            _messages.Add(pending);
            ScrollToEnd();

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Windows.Storage.Streams.IBuffer read =
                    await Windows.Storage.FileIO.ReadBufferAsync(file);

                byte[] bytes;
                Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(read, out bytes);

                using (var stream = new System.IO.MemoryStream(bytes))
                {
                    UploadedFile uploaded = await Upload.SendFileAsync(
                        client, TelegramService.Crypto, file.Name, stream.Length,
                        delegate (byte[] buffer) { return stream.Read(buffer, 0, buffer.Length); },
                        delegate (long done, long total)
                        {
                            if (total <= 0) return;

                            var ignored = Dispatcher.RunAsync(
                                Windows.UI.Core.CoreDispatcherPriority.Low,
                                delegate
                                {
                                    pending.MediaNote = "sending " + (done * 100 / total) + "%";
                                });
                        },
                        TelegramService.Info);

                    string type = file.ContentType ?? "";

                    if (type.StartsWith("image/"))
                    {
                        await Upload.SendPhotoAsync(client, TelegramService.Crypto,
                                                    _inputPeer, uploaded, "", TelegramService.Info);
                    }
                    else if (type.StartsWith("video/"))
                    {
                        // Size and duration are what make a video arrive as one.
                        // Sent as a plain document it is offered as a file to
                        // download rather than something to play, on every client.
                        Windows.Storage.FileProperties.VideoProperties video =
                            await file.Properties.GetVideoPropertiesAsync();

                        await Upload.SendVideoAsync(
                            client, TelegramService.Crypto, _inputPeer, uploaded, "",
                            type, (int)video.Duration.TotalSeconds,
                            (int)video.Width, (int)video.Height, TelegramService.Info);
                    }
                    else
                    {
                        await Upload.SendDocumentAsync(client, TelegramService.Crypto,
                                                       _inputPeer, uploaded, "",
                                                       type.Length > 0 ? type : "application/octet-stream",
                                                       TelegramService.Info);
                    }
                }

                pending.MediaNote = "sent " + file.Name;
                Refresh();
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                pending.MediaNote = "not sent: " + (rpc != null ? rpc.ErrorType : ex.Message);
            }
        }

        /// <summary>
        /// Offers to save any attachment wherever the user likes.
        ///
        /// The gallery is right for a picture and useless for anything else, so this
        /// is the answer for documents - and it is the other half of what WinRT was
        /// ported for.
        /// </summary>
        private async void SaveAsMenu_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            var item = element.DataContext as MessageItem;
            if (item == null || item.Media == null) return;

            Windows.Storage.StorageFile cached = await MediaCache.FindAsync(item.Media);
            if (cached == null)
            {
                item.MediaNote = "load it first, then save";
                return;
            }

            _saving = item;

            var picker = new Windows.Storage.Pickers.FileSavePicker();

            string name = item.Media.FileName;
            if (string.IsNullOrEmpty(name)) name = cached.Name;

            picker.SuggestedFileName = name;
            picker.DefaultFileExtension = System.IO.Path.GetExtension(cached.Name);

            picker.FileTypeChoices.Add("file",
                new List<string> { System.IO.Path.GetExtension(cached.Name) });

            picker.PickSaveFileAndContinue();
        }

        private MessageItem _saving;

        /// <summary>The chosen destination comes back here.</summary>
        public async void SaveLocationPicked(Windows.ApplicationModel.Activation.FileSavePickerContinuationEventArgs args)
        {
            MessageItem item = _saving;
            _saving = null;

            if (args == null || args.File == null || item == null) return;

            try
            {
                Windows.Storage.StorageFile cached = await MediaCache.FindAsync(item.Media);
                if (cached == null) return;

                Windows.Storage.Streams.IBuffer read =
                    await Windows.Storage.FileIO.ReadBufferAsync(cached);

                await Windows.Storage.FileIO.WriteBufferAsync(args.File, read);

                item.MediaNote = "saved as " + args.File.Name;
            }
            catch (Exception ex)
            {
                item.MediaNote = "not saved: " + ex.Message;
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        /// <summary>
        /// Guards against the same message being sent twice - a fast double tap, or
        /// Enter and the button together.
        /// </summary>
        private bool _sending;

        private async void Send()
        {
            if (_sending) return;

            string text = (ComposeBox.Text ?? "").Trim();
            if (text.Length == 0) return;

            _sending = true;

            ComposeBox.Text = "";
            SendButton.IsEnabled = false;

            // Shown at once. Waiting for the server makes the app feel broken on a
            // slow connection, and a failure is reported below rather than hidden.
            var pending = new MessageItem
            {
                Id = 0,
                Text = text,
                Time = DateTime.Now.ToString("HH:mm"),
                Out = true,
            };

            _messages.Add(pending);
            ScrollToEnd();

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                await Messages.SendTextAsync(client, TelegramService.Crypto, _inputPeer, text);

                SetBusy(false, "");
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Not sent: " + (rpc != null ? rpc.ErrorType : ex.Message));

                pending.Text = text + "  (not sent)";

                // The item has no change notification, so the list is rebuilt to
                // pick the new text up.
                var all = new List<MessageItem>(_messages);
                _messages.Clear();
                foreach (MessageItem item in all) _messages.Add(item);
            }
            finally
            {
                _sending = false;
                SendButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Moves to the newest message.
        ///
        /// Two things had to be right here, and neither is obvious. The layout has to
        /// be brought up to date first, or ScrollableHeight still describes the list
        /// as it was before the messages were added - which is why opening a chat
        /// left it at the top. And the target is deliberately larger than any real
        /// extent rather than ScrollableHeight itself: the value is clamped, so
        /// asking to go far too far always lands exactly at the bottom, whereas
        /// asking for a number read a moment too early lands short.
        /// </summary>
        private void ScrollToEnd()
        {
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low,
                delegate
                {
                    MessageList.UpdateLayout();

                    if (_messages.Count > 0)
                        MessageList.ScrollIntoView(_messages[_messages.Count - 1]);
                });
        }

        private void SetBusy(bool busy, string status)
        {
            Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = status ?? "";
        }
    }
}
