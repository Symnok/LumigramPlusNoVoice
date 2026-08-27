using System;
using System.Collections.Generic;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Windows.UI.Xaml;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>
    /// Deciding what is worth interrupting the user for, and doing it.
    ///
    /// The policy, in the order it is applied:
    ///
    ///   notifications off     never
    ///   chat muted            never
    ///   chat open on screen   never for *that* chat
    ///   anything else         a toast, and a banner on the page
    ///
    /// The third rule is the one that matters most in practice: being told about a
    /// message already on screen is pure noise, while one from a different chat is
    /// exactly what a notification is for.
    ///
    /// Detection is by top message id per chat rather than by unread count. A count
    /// moves for reasons that are not new messages - reading elsewhere, a chat being
    /// cleared - and each of those would announce something that did not arrive.
    /// </summary>
    public static class Notifications
    {
        /// <summary>The chat on screen, or 0 when none is open.</summary>
        public static long OpenPeerId;

        /// <summary>
        /// A chat a tapped toast asked for, waiting to be opened.
        ///
        /// The toast only carries a peer id, and a peer id alone is not enough to
        /// open a conversation - the access hash lives in the chat list. So the id
        /// is parked here and the chat list opens it once it has loaded.
        /// </summary>
        public static long PendingPeerId;

        /// <summary>The prefix on a toast's launch argument.</summary>
        private const string PeerArgument = "peer=";

        /// <summary>
        /// Reads the peer id out of a launch argument, or 0 if there is not one.
        /// </summary>
        public static long PeerFromArgument(string argument)
        {
            if (string.IsNullOrEmpty(argument)) return 0;
            if (!argument.StartsWith(PeerArgument, StringComparison.Ordinal)) return 0;

            long id;
            return long.TryParse(argument.Substring(PeerArgument.Length), out id) ? id : 0;
        }

        /// <summary>
        /// Raised for the in-app banner. Windows Phone does not show an app its own
        /// toast while it is in the foreground, so without this the case the app is
        /// actually in - open, on another chat - would be silent.
        /// </summary>
        public static event Action<string, string, DialogEntry> Banner;

        /// <summary>
        /// The newest message seen in each chat.
        ///
        /// The first look only records; announcing everything already waiting the
        /// moment the app opens would be a wall of toasts for messages the user has
        /// had for hours.
        /// </summary>
        /// <summary>
        /// Why toasts are not appearing, or null when they should be.
        ///
        /// A refused toast raises nothing - Show() returns and the notification is
        /// simply dropped - so the only way to tell a suppressed toast from a broken
        /// one is to ask the notifier what it thinks its state is.
        /// </summary>
        public static string LastError;

        /// <summary>What the platform says about our ability to raise a toast.</summary>
        public static string NotifierState()
        {
            try
            {
                NotificationSetting setting =
                    ToastNotificationManager.CreateToastNotifier().Setting;

                return setting == NotificationSetting.Enabled ? null : setting.ToString();
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static readonly Dictionary<long, int> Seen = new Dictionary<long, int>();
        private static bool _baseline;

        /// <summary>Forgets everything, so a new account does not inherit it.</summary>
        public static void Reset()
        {
            lock (Seen)
            {
                Seen.Clear();
                _baseline = false;
                LiveTile.Clear();
            }
        }

        /// <summary>
        /// Looks at a freshly read chat list and announces what is new.
        ///
        /// Called by whoever happens to have fetched one, rather than polling
        /// separately: the chat list already reads this every few seconds, and a
        /// second poll would double the traffic to learn the same thing.
        /// </summary>
        public static void Observe(List<DialogEntry> dialogs)
        {
            if (dialogs == null) return;

            var announce = new List<DialogEntry>();
            int now = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0,
                                                           DateTimeKind.Utc)).TotalSeconds;

            lock (Seen)
            {
                bool first = !_baseline;

                foreach (DialogEntry d in dialogs)
                {
                    int previous;
                    bool known = Seen.TryGetValue(d.PeerId, out previous);

                    Seen[d.PeerId] = d.TopMessageId;

                    if (first || !known) continue;
                    if (d.TopMessageId <= previous) continue;

                    // Nothing unread means we sent it, or it was read elsewhere.
                    if (d.UnreadCount <= 0) continue;
                    if (d.IsMuted(now)) continue;
                    if (d.PeerId == OpenPeerId) continue;

                    announce.Add(d);
                }

                _baseline = true;
            }

            if (!AppSettings.Notifications)
            {
                // The toggle covers the tile too. A badge is a notification that
                // happens to be quiet, and leaving one up after the user said no
                // would be a surprise.
                LiveTile.Clear();
                return;
            }

            // Written on every pass, not only when something arrived: the tile also
            // has to come down as chats are read.
            LiveTile.Update(dialogs, now);

            foreach (DialogEntry d in announce) Announce(d);
        }

        private static void Announce(DialogEntry dialog)
        {
            string title = string.IsNullOrEmpty(dialog.Title) ? "Lumigram+" : dialog.Title;
            string body = string.IsNullOrEmpty(dialog.LastText) ? "new message" : dialog.LastText;

            // One notification per message. The system toast turned out to appear
            // even while the app is in the foreground, so raising the in-app banner
            // as well showed the same message twice. The banner is now what happens
            // when the toast cannot be - which is the case it was written for.
            if (Toast(title, body, PeerArgument + dialog.PeerId)) return;

            Action<string, string, DialogEntry> handler = Banner;
            if (handler == null) return;

            try { handler(title, body, dialog); }
            catch (Exception) { }
        }

        /// <summary>
        /// Raises a system toast.
        ///
        /// WinRT toasts go to the action centre by themselves, which is the one
        /// thing the Silverlight client had to be coaxed into - there is no manifest
        /// switch and no first-toast registration to get right here.
        /// </summary>
        /// <summary>Returns whether the toast was actually handed to the platform.</summary>
        private static bool Toast(string title, string body, string launch)
        {
            try
            {
                XmlDocument xml = ToastNotificationManager.GetTemplateContent(
                    ToastTemplateType.ToastText02);

                XmlNodeList lines = xml.GetElementsByTagName("text");
                lines[0].AppendChild(xml.CreateTextNode(title));
                lines[1].AppendChild(xml.CreateTextNode(body));

                // Tapping the toast has to lead somewhere, and by the time it is
                // tapped the list it came from is long gone - so the chat travels
                // with the notification.
                ((XmlElement)xml.SelectSingleNode("/toast")).SetAttribute("launch", launch);

                ToastNotifier notifier = ToastNotificationManager.CreateToastNotifier();

                LastError = notifier.Setting == NotificationSetting.Enabled
                    ? null : notifier.Setting.ToString();

                if (LastError != null) return false;

                notifier.Show(new ToastNotification(xml));
                return true;
            }
            catch (Exception ex)
            {
                // A toast that cannot be shown must never take a message down with
                // it - but it should not vanish without a trace either.
                LastError = ex.Message;
                return false;
            }
        }
    }
}
