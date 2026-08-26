using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Lumigram.Audio;
using Lumigram.Mtproto;
using Lumigram.Phone.Services;

namespace Lumigram.Phone
{
    /// <summary>
    /// A message shaped for the bubble template.
    ///
    /// Raises PropertyChanged because an attachment arrives after the bubble is
    /// already on screen - the image is downloaded in the background, and without
    /// notification the row would never show it.
    /// </summary>
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
        public string Time { get; set; }
        public bool Out { get; set; }
        public MediaInfo Media { get; set; }

        /// <summary>Where the downloaded file landed, once it has been fetched.</summary>
        public string CachePath { get; set; }

        /// <summary>Guards against a second save on the same item.</summary>
        public bool Saving { get; set; }

        /// <summary>True while this voice message is the one being played.</summary>
        public bool Playing { get; set; }

        /// <summary>
        /// The addresses in this message, if any.
        ///
        /// Worked out once when the message is built rather than every time it is
        /// drawn: a list can be scrolled repeatedly and the text never changes.
        /// </summary>
        public List<string> Links { get; set; }

        /// <summary>Who wrote it, in a group. Empty in a one-to-one chat.</summary>
        private string _senderName;
        public string SenderName
        {
            get { return _senderName; }
            set { _senderName = value; Raise("SenderName"); Raise("SenderVisibility"); }
        }

        public Visibility SenderVisibility
        {
            get
            {
                return string.IsNullOrEmpty(_senderName) ? Visibility.Collapsed
                                                         : Visibility.Visible;
            }
        }

        /// <summary>Set once the attachment has been written to storage.</summary>
        public SavedFile SavedFile { get; set; }

        private string _text;
        public string Text
        {
            get { return _text; }
            set { _text = value; Raise("Text"); Raise("TextVisibility"); }
        }

        public Visibility TextVisibility
        {
            get { return string.IsNullOrEmpty(Text) ? Visibility.Collapsed : Visibility.Visible; }
        }

        private ImageSource _image;
        public ImageSource Image
        {
            get { return _image; }
            set { _image = value; Raise("Image"); Raise("ImageVisibility"); Raise("MediaNoteVisibility"); }
        }

        public Visibility ImageVisibility
        {
            get { return _image == null ? Visibility.Collapsed : Visibility.Visible; }
        }

        private string _mediaNote;
        public string MediaNote
        {
            get { return _mediaNote; }
            set { _mediaNote = value; Raise("MediaNote"); Raise("MediaNoteVisibility"); }
        }

        public Visibility MediaNoteVisibility
        {
            get
            {
                return _image == null && !string.IsNullOrEmpty(_mediaNote)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public HorizontalAlignment Align
        {
            get { return Out ? HorizontalAlignment.Right : HorizontalAlignment.Left; }
        }

        public Thickness Margin
        {
            get { return Out ? new Thickness(60, 4, 12, 4) : new Thickness(12, 4, 60, 4); }
        }

        public Brush Background
        {
            get
            {
                return Out
                    ? (Brush)Application.Current.Resources["PhoneAccentBrush"]
                    : new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
            }
        }
    }

    public partial class ConversationPage : PhoneApplicationPage
    {
        private readonly ObservableCollection<MessageItem> _messages = new ObservableCollection<MessageItem>();

        /// <summary>
        /// Everyone seen writing in this chat.
        ///
        /// A message carries only its sender id, so without this a group is a list
        /// of anonymous text. Filled from the users vector that comes back with the
        /// history and kept for the visit.
        /// </summary>
        private readonly Dictionary<long, PeerInfo> _senders = new Dictionary<long, PeerInfo>();

        private long _peerId;
        private long _accessHash;
        private string _kind;
        private byte[] _inputPeer;
        private bool _loaded;

        public ConversationPage()
        {
            InitializeComponent();
            MessageList.ItemsSource = _messages;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (!_loaded)
            {
                string s;
                if (NavigationContext.QueryString.TryGetValue("peer", out s)) long.TryParse(s, out _peerId);
                if (NavigationContext.QueryString.TryGetValue("hash", out s)) long.TryParse(s, out _accessHash);
                if (NavigationContext.QueryString.TryGetValue("kind", out s)) _kind = s;
                if (NavigationContext.QueryString.TryGetValue("title", out s)) PeerTitle.Text = s;

                _inputPeer = Messages.InputPeerFor(_kind ?? "user", _peerId, _accessHash);
                Load();
            }

            TelegramService.MessagesReceived += OnMessages;
            Notifier.Banner += OnBanner;
            CheckForCard();

            // Messages arriving for the chat on screen must not notify.
            AppState.OpenPeerId = _peerId;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            TelegramService.MessagesReceived -= OnMessages;
            Notifier.Banner -= OnBanner;
            AppState.OpenPeerId = 0;
        }

        private async void CheckForCard()
        {
            try { _hasCard = await GallerySave.HasCardAsync(); }
            catch (Exception) { _hasCard = false; }
        }

        private void SetBusy(bool busy, string status)
        {
            Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = status ?? "";
        }

        private async void Load()
        {
            SetBusy(true, "Loading messages...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();
                Messages.History fetched = await Messages.GetHistoryAsync(
                    client, _inputPeer, 30, TelegramService.Info);

                List<TextMessage> history = fetched.Messages;

                // Who wrote what. Kept for the whole visit, so messages arriving
                // later can be attributed without asking again.
                foreach (KeyValuePair<long, PeerInfo> pair in fetched.Senders)
                    _senders[pair.Key] = pair.Value;

                _messages.Clear();

                // getHistory returns newest first; a conversation reads oldest first.
                for (int i = history.Count - 1; i >= 0; i--) Add(history[i]);

                _loaded = true;
                SetBusy(false, history.Count == 0 ? "No messages yet." : null);
                ScrollToEnd();

                MarkRead(client, history);
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, rpc != null ? rpc.ErrorType : ex.Message);
            }
        }

        /// <summary>
        /// Tells the server the chat has been read up to the newest message.
        ///
        /// The unread count belongs to the server. Clearing it only in the UI makes
        /// it reappear on the next getDialogs, and leaves the chat showing as unread
        /// on the user other devices.
        /// </summary>
        private async void MarkRead(MtprotoClient client, List<TextMessage> history)
        {
            int maxId = 0;
            foreach (TextMessage m in history) if (m.Id > maxId) maxId = m.Id;
            if (maxId == 0) return;

            try
            {
                await Messages.MarkReadAsync(client, _kind, _peerId, _accessHash, maxId,
                                             TelegramService.Info);
                TelegramService.NotifyChatRead(_peerId);
            }
            catch (Exception ex)
            {
                // Not worth a dialog - the badge simply stays until next time - but
                // not worth hiding either: a silent failure here is exactly how the
                // channel case went unnoticed.
                var rpc = ex as RpcException;
                TelegramService.NoteWarning("read failed: " + (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        /// <summary>
        /// The name to show above a message, or empty when it needs none.
        ///
        /// Only in groups and channels, and never for our own: in a one-to-one chat
        /// the sender is never in doubt, and labelling every line with it would be
        /// noise down both sides of the screen.
        /// </summary>
        private string SenderFor(TextMessage m)
        {
            if (m.Out || _kind == "user") return "";

            long from = m.FromId;
            if (from == 0) return "";

            PeerInfo sender;
            if (_senders.TryGetValue(from, out sender)) return sender.Name;

            // Someone who has not written since this chat was opened. Better than
            // nothing, and the next full load names them properly.
            return "user " + from;
        }

        private void Add(TextMessage m)
        {
            var item = new MessageItem
            {
                Id = m.Id,
                Text = m.Text ?? "",
                Time = m.DateUtc.ToLocalTime().ToString("HH:mm"),
                Out = m.Out,
                Media = m.Media,
                SenderName = SenderFor(m),
                Links = Lumigram.Tl.Links.Find(m.Text ?? ""),
            };

            if (m.Media != null)
            {
                item.MediaNote = m.Media.Kind == MediaKind.Photo
                    ? "photo - tap to load"
                    : m.Media.Describe() + (_hasCard ? " - tap to save to SD card"
                                                     : " - needs an SD card to save");
            }
            else if (string.IsNullOrEmpty(m.Text) && m.Note != null)
            {
                item.MediaNote = m.Note;
            }

            _messages.Add(item);

            // Photos load on their own; anything larger waits to be asked for, so a
            // conversation full of video does not start downloading itself.
            if (m.Media != null && m.Media.Kind == MediaKind.Photo &&
                m.Media.FileSize > 0 && m.Media.FileSize < 2 * 1024 * 1024)
                LoadImage(item);
        }

        /// <summary>Fetches an attachment and shows it, from cache when possible.</summary>
        private async void LoadImage(MessageItem item)
        {
            if (item.Media == null || item.Image != null) return;

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                item.MediaNote = "loading...";
                string path = await MediaCache.GetAsync(client, item.Media,
                    delegate (long got, long total)
                    {
                        if (total > 0)
                            Dispatcher.BeginInvoke(delegate
                            {
                                item.MediaNote = "loading " + (got * 100 / total) + "%";
                            });
                    });

                item.CachePath = path;

                byte[] data = MediaCache.Read(path);
                if (data == null) { item.MediaNote = "could not read the cached file"; return; }

                Dispatcher.BeginInvoke(delegate
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        using (var ms = new System.IO.MemoryStream(data))
                            bitmap.SetSource(ms);
                        item.Image = bitmap;
                        item.MediaNote = null;
                        ScrollToEnd();
                    }
                    catch (Exception ex)
                    {
                        item.MediaNote = "cannot display: " + ex.GetType().Name;
                    }
                });
            }
            catch (RpcException ex)
            {
                string t = ex.ErrorType ?? "";
                item.MediaNote = t.Contains("FILE_REFERENCE")
                    ? "link expired - reopen the chat"
                    : t;
            }
            catch (Exception ex)
            {
                item.MediaNote = ex.Message;
            }
        }

        /// <summary>
        /// Draws the message text, colouring any links.
        ///
        /// Built as runs rather than bound, because Silverlight's TextBlock cannot
        /// hold a Hyperlink inline - that belongs to RichTextBox, which is far too
        /// heavy to put behind every message on a phone with 512 MB. Colour carries
        /// the meaning instead, and the tap is handled for the block as a whole.
        /// </summary>
        private void MessageText_Loaded(object sender, RoutedEventArgs e)
        {
            var block = sender as TextBlock;
            if (block == null) return;

            var item = block.DataContext as MessageItem;
            if (item == null) return;

            block.Inlines.Clear();

            foreach (Lumigram.Tl.TextPart part in Lumigram.Tl.Links.Split(item.Text ?? ""))
            {
                var run = new System.Windows.Documents.Run { Text = part.Text };

                if (part.IsLink)
                    run.Foreground = (Brush)Application.Current.Resources["PhoneAccentBrush"];

                block.Inlines.Add(run);
            }
        }

        /// <summary>
        /// Opens the link a message contains.
        ///
        /// A tap anywhere in the text rather than on the link itself: runs cannot be
        /// hit-tested here, and a message almost always holds one address. When it
        /// holds several the menu asks which, since guessing would open the wrong
        /// one half the time.
        /// </summary>
        private void MessageText_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            var block = sender as FrameworkElement;
            if (block == null) return;

            var item = block.DataContext as MessageItem;
            if (item == null || item.Links == null || item.Links.Count == 0) return;

            if (item.Links.Count == 1) { OpenLink(item.Links[0]); return; }

            _menuTarget = item;
            ShowMenuFor(item);
        }

        /// <summary>Keeps a long address readable on a button.</summary>
        private static string Shorten(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            return url.Length <= 40 ? url : url.Substring(0, 37) + "...";
        }

        private void OpenLink(string url)
        {
            try
            {
                var task = new Microsoft.Phone.Tasks.WebBrowserTask();
                task.Uri = new Uri(url, UriKind.Absolute);
                task.Show();
            }
            catch (Exception ex)
            {
                SetBusy(false, "Cannot open the link: " + ex.Message);
            }
        }

        private void Media_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            var item = element.DataContext as MessageItem;
            if (item == null || item.Media == null) return;

            if (item.Media.Kind == MediaKind.Voice) { PlayVoice(item); return; }

            if (item.Media.Kind == MediaKind.Location) { ShowOnMap(item.Media); return; }

            // The app cannot play video, so a saved file is handed to the system
            // player rather than opened here.
            if (item.Media.Kind != MediaKind.Photo)
            {
                if (item.SavedFile != null) PlaySaved(item);
                else SaveToGallery(item);
                return;
            }

            if (item.Image == null) { LoadImage(item); return; }

            // Already loaded: open it full screen, where it can be zoomed and saved.
            if (!string.IsNullOrEmpty(item.CachePath))
            {
                string name = item.Media.FileName;
                if (string.IsNullOrEmpty(name)) name = "lumigram-" + item.Id + ".jpg";

                NavigationService.Navigate(new Uri(
                    "/ImageViewerPage.xaml?path=" + Uri.EscapeDataString(item.CachePath) +
                    "&name=" + Uri.EscapeDataString(name), UriKind.Relative));
            }
        }

        private void ScrollToEnd()
        {
            // Let the list lay out before scrolling, or it scrolls to the old extent.
            Dispatcher.BeginInvoke(delegate { Scroller.ScrollToVerticalOffset(double.MaxValue); });
        }

        private void OnMessages(List<TextMessage> messages)
        {
            Dispatcher.BeginInvoke(delegate
            {
                bool added = false;
                foreach (TextMessage m in messages)
                {
                    // Only what belongs to this conversation.
                    if (m.PeerId != _peerId && m.FromId != _peerId) continue;
                    if (Contains(m.Id)) continue;
                    if (Adopt(m)) continue;

                    Add(m);
                    added = true;
                }
                if (added) ScrollToEnd();
            });
        }

        /// <summary>
        /// Gives an arriving echo to the copy already on screen, if one is waiting
        /// for it.
        ///
        /// Covers the gap between showing a sent message immediately and learning
        /// what id the server gave it: if the message comes back through updates
        /// first, there is no id to match on yet and it would be drawn a second
        /// time. Only messages of our own with no id yet are considered, and each
        /// is claimed once, so two identical messages sent deliberately still show
        /// as two.
        /// </summary>
        private bool Adopt(TextMessage m)
        {
            if (!m.Out || m.Id == 0) return false;

            foreach (MessageItem item in _messages)
            {
                if (!item.Out || item.Id != 0) continue;
                if (item.Text != m.Text) continue;

                item.Id = m.Id;
                return true;
            }

            return false;
        }

        private bool Contains(int id)
        {
            foreach (MessageItem item in _messages)
                if (item.Id == id) return true;
            return false;
        }

        /// <summary>
        /// Picks an image from the gallery and sends it.
        ///
        /// PhotoChooserTask rather than the WinRT FileOpenPicker: the picker uses
        /// the continuation model, which deactivates the app and resumes it through
        /// App-level plumbing. That is the right mechanism for arbitrary files and
        /// video, and it is worth adding once photos are proven - but it is a lot of
        /// moving parts to introduce before anything works at all.
        /// </summary>
        /// <summary>
        /// The + button: offers what can be attached, or ends a recording.
        ///
        /// While recording, this is the stop button. Folding it in here rather than
        /// keeping a second button on the compose bar is what the bar is short of -
        /// every button on it is width the message box does not get - and there is
        /// no ambiguity, because nothing else can be attached mid-recording anyway.
        /// </summary>
        private async void Attach_Click(object sender, RoutedEventArgs e)
        {
            if (VoiceRecorder.IsRecording)
            {
                await StopRecordingAndSend();
                return;
            }

            AttachActions.Visibility = Visibility.Visible;
        }

        private void CancelAttachMenu_Click(object sender, RoutedEventArgs e)
        {
            AttachActions.Visibility = Visibility.Collapsed;
        }

        private void AttachActions_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            AttachActions.Visibility = Visibility.Collapsed;
        }

        private void AttachVoice_Click(object sender, RoutedEventArgs e)
        {
            AttachActions.Visibility = Visibility.Collapsed;
            StartRecording();
        }

        /// <summary>
        /// Sends where the phone currently is.
        ///
        /// The location capability is already declared for the background mode, so
        /// nothing new is asked of the user beyond the prompt Windows Phone shows
        /// the first time. A short timeout on purpose: a fix that has not arrived in
        /// a few seconds is usually indoors and never arriving, and waiting silently
        /// is worse than saying so.
        /// </summary>
        private async void AttachLocation_Click(object sender, RoutedEventArgs e)
        {
            AttachActions.Visibility = Visibility.Collapsed;
            AttachButton.IsEnabled = false;

            try
            {
                SetBusy(true, "Finding your location...");

                var locator = new Windows.Devices.Geolocation.Geolocator();
                locator.DesiredAccuracyInMeters = 50;

                Windows.Devices.Geolocation.Geoposition position =
                    await locator.GetGeopositionAsync(TimeSpan.FromMinutes(1),
                                                      TimeSpan.FromSeconds(20));

                Windows.Devices.Geolocation.Geocoordinate at = position.Coordinate;

                SetBusy(true, "Sending location...");

                MtprotoClient client = await TelegramService.ConnectAsync();

                await Upload.SendLocationAsync(
                    client, TelegramService.Crypto, _inputPeer,
                    at.Point.Position.Latitude, at.Point.Position.Longitude,
                    at.Accuracy > 0 ? (int)at.Accuracy : 0,
                    TelegramService.Info);

                SetBusy(false, "");
                Load();
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "No location: " + (rpc != null ? rpc.ErrorType : ex.Message));
            }
            finally
            {
                AttachButton.IsEnabled = true;
            }
        }

        private void AttachPicture_Click(object sender, RoutedEventArgs e)
        {
            AttachActions.Visibility = Visibility.Collapsed;

            var chooser = new Microsoft.Phone.Tasks.PhotoChooserTask();
            chooser.ShowCamera = true;
            chooser.Completed += Photo_Chosen;

            try { chooser.Show(); }
            catch (Exception ex) { SetBusy(false, "Cannot open the gallery: " + ex.Message); }
        }

        private async void Photo_Chosen(object sender, Microsoft.Phone.Tasks.PhotoResult result)
        {
            if (result.TaskResult != Microsoft.Phone.Tasks.TaskResult.OK || result.ChosenPhoto == null)
                return;

            string name = result.OriginalFileName;
            if (string.IsNullOrEmpty(name)) name = "photo.jpg";
            else
            {
                int slash = name.LastIndexOfAny(new[] { '/', (char)92 });
                if (slash >= 0) name = name.Substring(slash + 1);
            }

            AttachButton.IsEnabled = false;
            SendButton.IsEnabled = false;

            var pending = new MessageItem
            {
                Id = 0,
                Text = "",
                MediaNote = "sending " + name + "...",
                Time = DateTime.Now.ToString("HH:mm"),
                Out = true,
            };
            _messages.Add(pending);
            ScrollToEnd();

            try
            {
                using (System.IO.Stream stream = result.ChosenPhoto)
                {
                    long size = stream.Length;
                    MtprotoClient client = await TelegramService.ConnectAsync();

                    UploadedFile uploaded = await Upload.SendFileAsync(
                        client, TelegramService.Crypto, name, size,
                        delegate (byte[] buffer) { return stream.Read(buffer, 0, buffer.Length); },
                        delegate (long done, long total)
                        {
                            if (total > 0)
                                Dispatcher.BeginInvoke(delegate
                                {
                                    pending.MediaNote = "sending " + (done * 100 / total) + "%";
                                });
                        },
                        TelegramService.Info);

                    string caption = (ComposeBox.Text ?? "").Trim();
                    ComposeBox.Text = "";

                    pending.Id = await Upload.SendPhotoAsync(client, TelegramService.Crypto,
                                                _inputPeer, uploaded, caption, TelegramService.Info);

                    Dispatcher.BeginInvoke(delegate
                    {
                        pending.MediaNote = null;
                        pending.Text = string.IsNullOrEmpty(caption) ? "photo sent" : caption;
                    });
                }
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                Dispatcher.BeginInvoke(delegate
                {
                    pending.MediaNote = "not sent: " + (rpc != null ? rpc.ErrorType : ex.Message);
                });
            }

            AttachButton.IsEnabled = true;
            SendButton.IsEnabled = true;
        }

        private MessageItem _menuTarget;
        private SavedFile _lastSaved;

        /// <summary>
        /// Whether an SD card is present.
        ///
        /// Checked once when the page opens so message bubbles can say whether a
        /// video can be saved at all, rather than only finding out on tap.
        /// </summary>
        private static bool _hasCard;

        /// <summary>Opens a saved file with whatever the phone uses for it.</summary>
        private async void PlaySaved(MessageItem item)
        {
            if (item == null || item.SavedFile == null) return;

            bool opened = await GallerySave.OpenAsync(item.SavedFile.File);
            if (!opened)
                item.MediaNote = "saved to " + item.SavedFile.Location +
                                 " - no app on this phone opens it";
        }

        /// <summary>
        /// Downloads an attachment straight into the camera roll.
        ///
        /// Streams to the file rather than buffering: a video is easily larger than
        /// the memory this app is allowed on a 512 MB phone.
        /// </summary>
        private async void SaveToGallery(MessageItem item)
        {
            if (item == null || item.Media == null || item.Saving) return;
            item.Saving = true;

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                item.MediaNote = "saving...";
                SavedFile saved = await GallerySave.DownloadAsync(
                    client, item.Media, FileNameFor(item),
                    delegate (long got, long total)
                    {
                        if (total > 0)
                            Dispatcher.BeginInvoke(delegate
                            {
                                item.MediaNote = "saving " + (got * 100 / total) + "%";
                            });
                    });

                _lastSaved = saved;

                Dispatcher.BeginInvoke(delegate
                {
                    item.MediaNote = "saved to " + saved.Location + " - tap to play";
                    item.SavedFile = saved;
                });
            }
            catch (NoStorageException ex)
            {
                // Not a failure to report as an error - the phone simply has
                // nowhere this app is allowed to put a video.
                item.MediaNote = ex.Message;
            }
            catch (RpcException ex)
            {
                string t = ex.ErrorType ?? "";
                item.MediaNote = t.Contains("FILE_REFERENCE")
                    ? "link expired - reopen the chat"
                    : "not saved: " + t;
            }
            catch (Exception ex)
            {
                item.MediaNote = "not saved: " + ex.Message;
            }

            item.Saving = false;
        }

        private System.Windows.Threading.DispatcherTimer _recordTimer;

        /// <summary>
        /// Starts recording, or stops and sends what was recorded.
        ///
        /// Tap to start, tap again to send, rather than hold-to-talk. Holding is what
        /// other clients do, but it depends on being able to tell a hold from a tap
        /// reliably, and getting that wrong loses a message the user has already
        /// spoken. Two taps cannot be misread.
        /// </summary>
        private void StartRecording()
        {
            string error = VoiceRecorder.Start();
            if (error != null)
            {
                MessageBox.Show(error, "Cannot record", MessageBoxButton.OK);
                return;
            }

            ShowRecording(true);
            SendButton.IsEnabled = false;

            if (_recordTimer == null)
            {
                _recordTimer = new System.Windows.Threading.DispatcherTimer();
                _recordTimer.Interval = TimeSpan.FromMilliseconds(200);
                _recordTimer.Tick += RecordTick;
            }
            _recordTimer.Start();
        }

        private async void RecordTick(object sender, EventArgs e)
        {
            if (!VoiceRecorder.IsRecording) { _recordTimer.Stop(); return; }

            TimeSpan elapsed = VoiceRecorder.Elapsed;
            RecordingTime.Text = (int)elapsed.TotalMinutes + ":" + elapsed.Seconds.ToString("00");

            // Blinking is what says "recording" rather than "ready to record", and
            // it costs nothing to drive from the clock we are already reading.
            RecordingDot.Opacity = ((int)(elapsed.TotalMilliseconds / 500)) % 2 == 0 ? 1.0 : 0.25;

            RecordingLevel.Width = Math.Max(2.0, Math.Min(1.0, VoiceRecorder.Level * 1.6) * 110.0);

            // Stopping at the limit rather than silently continuing: a recording left
            // running by accident should not become a message nobody wants to hear.
            if (VoiceRecorder.ReachedLimit) await StopRecordingAndSend();
        }

        /// <summary>
        /// Swaps the message box for the recording display, and the + button for a
        /// red dot that stops it.
        /// </summary>
        private void ShowRecording(bool recording)
        {
            RecordingPanel.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
            ComposeBox.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;

            if (recording)
            {
                RecordingTime.Text = "0:00";
                RecordingLevel.Width = 2;
                RecordingDot.Opacity = 1.0;

                // A filled red circle rather than the word "stop": it matches the
                // dot next to it, and it reads as "recording" at a glance.
                AttachButton.Content = new System.Windows.Shapes.Ellipse
                {
                    Width = 20,
                    Height = 20,
                    Fill = new SolidColorBrush(Colors.Red),
                };
            }
            else
            {
                AttachButton.Content = "+";
            }
        }

        /// <summary>
        /// Ends the recording, encodes it and sends it.
        ///
        /// Encoding runs off the UI thread. On this hardware it is slower than the
        /// recording itself was, and freezing the conversation while it works would
        /// make the app look like it had crashed at the worst moment.
        /// </summary>
        private async Task StopRecordingAndSend()
        {
            if (_recordTimer != null) _recordTimer.Stop();

            int sampleCount, sampleRate;
            short[] pcm = VoiceRecorder.Stop(out sampleCount, out sampleRate);

            ShowRecording(false);
            AttachButton.IsEnabled = false;
            SendButton.IsEnabled = true;

            try
            {
                if (pcm == null || sampleCount < sampleRate / 4)
                {
                    SetBusy(false, "Too short to send.");
                    return;
                }

                SetBusy(true, "Encoding...");

                EncodedVoice voice = null;
                Exception failure = null;

                await Task.Run(delegate
                {
                    try { voice = VoiceEncoder.Encode(pcm, sampleCount, sampleRate); }
                    catch (Exception ex) { failure = ex; }
                });

                if (failure != null) throw failure;

                SetBusy(true, "Sending...");

                MtprotoClient client = await TelegramService.ConnectAsync();

                using (var stream = new System.IO.MemoryStream(voice.File))
                {
                    UploadedFile uploaded = await Upload.SendFileAsync(
                        client, TelegramService.Crypto, "voice.ogg", stream.Length,
                        delegate (byte[] buffer) { return stream.Read(buffer, 0, buffer.Length); },
                        delegate (long done, long total)
                        {
                            if (total > 0)
                                Dispatcher.BeginInvoke(delegate
                                {
                                    SetBusy(true, "Sending... " + (done * 100 / total) + "%");
                                });
                        },
                        TelegramService.Info);

                    await Upload.SendVoiceAsync(client, TelegramService.Crypto, _inputPeer,
                                                uploaded, voice.DurationSeconds, voice.Waveform,
                                                TelegramService.Info);
                }

                SetBusy(false, "");
                Load();
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Not sent: " + (rpc != null ? rpc.ErrorType : ex.Message));
            }
            finally
            {
                AttachButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Opens a received location in the phone's own maps app.
        ///
        /// No map is drawn here. Rendering one would mean tiles, a network budget
        /// and a control this app does not have, where the phone already ships
        /// something better - and handing the coordinates over is one line.
        /// </summary>
        private void ShowOnMap(MediaInfo location)
        {
            try
            {
                var task = new Microsoft.Phone.Tasks.BingMapsTask();
                task.Center = new System.Device.Location.GeoCoordinate(
                    location.Latitude, location.Longitude);
                task.ZoomLevel = 16;
                task.Show();
            }
            catch (Exception ex)
            {
                SetBusy(false, "Cannot open maps: " + ex.Message);
            }
        }

        /// <summary>
        /// Downloads a voice message if it is not already here, decodes it, and
        /// plays it.
        ///
        /// Decoding runs off the UI thread. It is the slow part - there is no Opus
        /// support on this platform, so every sample is decoded in managed code -
        /// and on a weak phone a long message would otherwise freeze the
        /// conversation while it worked. Playback has to come back to the UI thread,
        /// because XNA's audio does.
        ///
        /// Tapping a message that is already playing stops it.
        /// </summary>
        private async void PlayVoice(MessageItem item)
        {
            if (item.Playing)
            {
                VoicePlayer.Stop();
                Note(item, false);
                return;
            }

            foreach (MessageItem other in _messages)
                if (other.Playing) Note(other, false);

            VoicePlayer.Stop();

            try
            {
                if (string.IsNullOrEmpty(item.CachePath))
                {
                    item.MediaNote = "loading...";
                    MtprotoClient client = await TelegramService.ConnectAsync();
                    item.CachePath = await MediaCache.GetAsync(client, item.Media);
                }

                byte[] file = MediaCache.Read(item.CachePath);
                if (file == null) throw new Exception("the file could not be read");

                item.MediaNote = "decoding...";

                int channels = 1;
                byte[] pcm = null;
                Exception failure = null;

                await Task.Run(delegate
                {
                    try { pcm = VoicePlayer.Decode(file, out channels); }
                    catch (Exception ex) { failure = ex; }
                });

                if (failure != null) throw failure;

                VoicePlayer.Play(pcm, channels);
                Note(item, true);
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                item.MediaNote = "could not play: " + (rpc != null ? rpc.ErrorType : ex.Message);
            }
        }

        /// <summary>Puts the item's caption back to what it should say.</summary>
        private void Note(MessageItem item, bool playing)
        {
            item.Playing = playing;
            if (item.Media == null) return;

            item.MediaNote = (playing ? "playing " : "") + item.Media.Describe();
        }

        /// <summary>
        /// Long tap on any message offers what can be done with it.
        ///
        /// On the whole bubble rather than only on a picture, since deleting applies
        /// to every message; the media actions simply hide themselves when there is
        /// no media to act on.
        /// </summary>
        private void Message_Hold(object sender, System.Windows.Input.GestureEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            var item = element.DataContext as MessageItem;
            if (item == null) return;

            _menuTarget = item;
            ShowMenuFor(item);
        }

        /// <summary>Fills the action menu in for one message and shows it.</summary>
        private void ShowMenuFor(MessageItem item)
        {
            bool hasMedia = item.Media != null;
            string text = item.Text ?? "";
            MessageActionTitle.Text = hasMedia ? item.Media.Describe()
                : text.Length > 60 ? text.Substring(0, 60) + "..."
                : text.Length > 0 ? text
                : "message";

            SaveMediaButton.Visibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;

            // A picture already downloaded can be opened full screen; anything else
            // can only be saved.
            bool viewable = hasMedia && item.Media.Kind == MediaKind.Photo &&
                            !string.IsNullOrEmpty(item.CachePath);
            OpenFullScreenButton.Visibility = viewable ? Visibility.Visible : Visibility.Collapsed;

            // A message that has not been sent yet has no id the server would know,
            // so there is nothing to ask it to delete.
            bool deletable = item.Id != 0;

            // In a channel a deletion is always for everyone and needs the rights to
            // do it, so there is only one thing to offer and no choice to make.
            bool isChannel = _kind == "channel";

            DeleteForMeButton.Visibility = deletable && !isChannel
                ? Visibility.Visible : Visibility.Collapsed;
            DeleteForAllButton.Content = isChannel ? "delete" : "delete for everyone";
            DeleteForAllButton.Visibility = deletable ? Visibility.Visible : Visibility.Collapsed;

            // One button per link. Rebuilt each time rather than kept, because the
            // menu serves whichever message was long-tapped.
            LinkButtons.Children.Clear();
            if (item.Links != null)
            {
                foreach (string url in item.Links)
                {
                    string target = url;
                    var button = new Button { Content = Shorten(url) };
                    button.Click += delegate
                    {
                        MessageActions.Visibility = Visibility.Collapsed;
                        _menuTarget = null;
                        OpenLink(target);
                    };
                    LinkButtons.Children.Add(button);
                }
            }

            MessageActions.Visibility = Visibility.Visible;
        }

        private void DeleteForMe_Click(object sender, RoutedEventArgs e)
        {
            Delete(false);
        }

        private void DeleteForAll_Click(object sender, RoutedEventArgs e)
        {
            Delete(true);
        }

        /// <summary>
        /// Deletes the message the menu was opened on.
        ///
        /// Confirmed first when it is for everyone: that reaches into someone else's
        /// conversation and cannot be undone. Deleting only for this account is
        /// recoverable in the sense that nobody else notices, so it is not worth a
        /// second dialog.
        /// </summary>
        private async void Delete(bool revoke)
        {
            MessageItem item = _menuTarget;
            MessageActions.Visibility = Visibility.Collapsed;
            _menuTarget = null;

            if (item == null || item.Id == 0) return;

            if (revoke)
            {
                MessageBoxResult confirm = MessageBox.Show(
                    _kind == "channel"
                        ? "Delete this message from the channel?"
                        : "Delete this message for everyone in this chat?",
                    "Delete", MessageBoxButton.OKCancel);

                if (confirm != MessageBoxResult.OK) return;
            }

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                await Messages.DeleteMessagesAsync(client, _kind, _peerId, _accessHash,
                                                   new List<int> { item.Id }, revoke,
                                                   TelegramService.Info);

                _messages.Remove(item);
            }
            catch (Exception ex)
            {
                // Worth showing. "Delete for everyone" is refused for messages that
                // are too old or belong to someone else, and a message that quietly
                // stays on screen looks like the app ignored the request.
                var rpc = ex as RpcException;
                MessageBox.Show(
                    rpc != null ? rpc.ErrorType : ex.Message,
                    "Could not delete", MessageBoxButton.OK);
            }
        }

        private void CancelMenu_Click(object sender, RoutedEventArgs e)
        {
            MessageActions.Visibility = Visibility.Collapsed;
            _menuTarget = null;
        }

        private void MessageActions_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            MessageActions.Visibility = Visibility.Collapsed;
            _menuTarget = null;
        }

        private void OpenImage_Click(object sender, RoutedEventArgs e)
        {
            MessageItem item = _menuTarget;
            MessageActions.Visibility = Visibility.Collapsed;
            _menuTarget = null;
            if (item == null || string.IsNullOrEmpty(item.CachePath)) return;

            NavigationService.Navigate(new Uri(
                "/ImageViewerPage.xaml?path=" + Uri.EscapeDataString(item.CachePath) +
                "&name=" + Uri.EscapeDataString(FileNameFor(item)), UriKind.Relative));
        }

        /// <summary>
        /// Writes the picture into the phone's camera roll.
        ///
        /// SavePictureToCameraRoll rather than SavePicture: the camera roll is where
        /// people look for something they have just kept, and the Photos hub shows
        /// it first.
        /// </summary>
        private void SaveImage_Click(object sender, RoutedEventArgs e)
        {
            MessageItem item = _menuTarget;
            MessageActions.Visibility = Visibility.Collapsed;
            _menuTarget = null;
            if (item == null || string.IsNullOrEmpty(item.CachePath)) return;

            if (item.Media.Kind != MediaKind.Photo || string.IsNullOrEmpty(item.CachePath))
            {
                // Not a picture, or not downloaded yet - stream it to the gallery.
                SaveToGallery(item);
                return;
            }

            try
            {
                byte[] data = MediaCache.Read(item.CachePath);
                if (data == null) { SetBusy(false, "Could not read the image."); return; }

                using (var library = new Microsoft.Xna.Framework.Media.MediaLibrary())
                using (var ms = new System.IO.MemoryStream(data))
                {
                    library.SavePictureToCameraRoll(FileNameFor(item), ms);
                }

                SetBusy(false, "Saved to your pictures.");
            }
            catch (Exception ex)
            {
                SetBusy(false, "Could not save: " + ex.Message);
            }
        }

        private static string FileNameFor(MessageItem item)
        {
            string name = item.Media != null ? item.Media.FileName : null;
            if (!string.IsNullOrEmpty(name)) return GallerySave.Sanitise(name);

            string extension = ".jpg";
            if (item.Media != null && item.Media.Kind == MediaKind.Video) extension = ".mp4";
            else if (item.Media != null && item.Media.Kind == MediaKind.Document) extension = ".bin";

            return "lumigram-" + item.Id + extension;
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
        private void Banner_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            BannerPanel.Visibility = Visibility.Collapsed;
            if (_bannerTimer != null) _bannerTimer.Stop();

            long peer = _bannerPeerId;
            if (peer == 0) return;

            OpenChat(peer);
        }

        /// <summary>
        /// Opens another chat from a banner.
        ///
        /// Navigates rather than reusing this page: the peer, its access hash and
        /// the loaded history all belong to the conversation currently shown.
        /// </summary>
        private void OpenChat(long peerId)
        {
            NavigationService.Navigate(new Uri("/ChatsPage.xaml", UriKind.Relative));
        }

        private void ComposeBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Send();
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            Send();
        }

        private async void Send()
        {
            string text = (ComposeBox.Text ?? "").Trim();
            if (text.Length == 0) return;

            ComposeBox.Text = "";
            SendButton.IsEnabled = false;

            // Show it immediately. The alternative - waiting for the server - makes
            // the app feel broken on a slow connection, and a failure is reported
            // below rather than silently swallowed.
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

                // Taking the id the server assigned is what stops the message being
                // shown twice: the copy put on screen a moment ago carries no id, so
                // when the same message arrives back through updates there is nothing
                // to recognise it by and it is added again.
                pending.Id = await Messages.SendTextAsync(client, TelegramService.Crypto,
                                                          _inputPeer, text);
                SetBusy(false, null);
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Not sent: " + (rpc != null ? rpc.ErrorType : ex.Message));

                pending.Text = text + "  (not sent)";
                var items = new List<MessageItem>(_messages);
                _messages.Clear();
                foreach (MessageItem i in items) _messages.Add(i);
            }

            SendButton.IsEnabled = true;
        }
    }
}
