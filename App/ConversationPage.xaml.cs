using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    public sealed class MessageItem
    {
        public int Id { get; set; }
        public string Text { get; set; }
        public string Time { get; set; }
        public bool Out { get; set; }
        public string SenderName { get; set; }

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
    public sealed partial class ConversationPage : Page
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
            string text = m.Text ?? "";

            // Attachments are described rather than shown. Rendering them needs the
            // download path, and a conversation that silently omits half its
            // messages would be worse than one that names what it cannot draw.
            if (text.Length == 0 && m.Media != null) text = "[" + m.Media.Describe() + "]";
            if (text.Length == 0) text = "[empty]";

            _messages.Add(new MessageItem
            {
                Id = m.Id,
                Text = text,
                Time = m.DateUtc.ToLocalTime().ToString("HH:mm"),
                Out = m.Out,
                SenderName = SenderFor(m),
            });
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
