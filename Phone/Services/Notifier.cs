using System;
using System.Collections.Generic;
using System.Windows;
using Microsoft.Phone.Shell;
using Lumigram.Mtproto;

namespace Lumigram.Phone.Services
{
    /// <summary>
    /// Where the user currently is, which decides whether a message is worth
    /// interrupting them for.
    /// </summary>
    internal static class AppState
    {
        /// <summary>The chat on screen, or 0 when none is open.</summary>
        public static long OpenPeerId;

        /// <summary>False once the app has been deactivated or backgrounded.</summary>
        public static bool IsForeground = true;
    }

    /// <summary>
    /// Decides what to announce, and announces it.
    ///
    /// The policy, in the order it is applied:
    ///
    ///   own messages          never notify
    ///   notifications off     never notify
    ///   chat muted            never notify
    ///   chat open on screen   never notify for *that* chat
    ///   anything else         toast, plus an in-app banner when in the foreground
    ///
    /// Groups and channels used to be silent unconditionally. They are not any
    /// more: they follow the same mute as everything else, which is both what the
    /// user expects from every other client and strictly more capable - a noisy
    /// group can be muted, where before a quiet one could not be heard.
    ///
    /// The last rule is the one that matters most in practice: being told about a
    /// message you are already looking at is pure noise, while a message from a
    /// different chat is exactly what a notification is for.
    /// </summary>
    internal static class Notifier
    {
        /// <summary>Raised for the in-app banner. Handlers run on the caller's thread.</summary>
        public static event Action<string, string, long> Banner;

        /// <summary>
        /// Turns a peer id into a name for the toast. Supplied by the chat list,
        /// which is the only place that knows them.
        ///
        /// A registered resolver rather than an argument, because deciding what to
        /// announce is the service's job and not a page's: while this lived in the
        /// chat list, opening a conversation unsubscribed it and nothing was
        /// announced at all until the user navigated back.
        /// </summary>
        public static Func<long, string> NameSource;

        /// <summary>Filters a batch and announces whatever survives.</summary>
        public static void Handle(List<TextMessage> messages)
        {
            AppSettings settings = AppSettings.Current;
            if (!settings.NotificationsEnabled) return;

            foreach (TextMessage m in messages)
            {
                if (m.Out) continue;                       // our own

                long peer = m.PeerId != 0 ? m.PeerId : m.FromId;

                // Muted on the account, so muted here. Checked against the stored
                // set rather than the chat list, because the background agent has to
                // reach the same answer and has never seen a chat list.
                if (MuteStore.IsMuted(peer)) continue;

                // The chat the user is reading right now.
                if (AppState.IsForeground && peer != 0 && peer == AppState.OpenPeerId) continue;

                Func<long, string> nameFor = NameSource;
                string title = null;
                try { if (nameFor != null) title = nameFor(peer); }
                catch (Exception) { }
                if (string.IsNullOrEmpty(title)) title = "Lumigram";

                string body = Summarise(m);

                Show(title, body, settings.NotificationSound);

                Action<string, string, long> handler = Banner;
                if (handler != null && AppState.IsForeground) handler(title, body, peer);
            }
        }

        /// <summary>
        /// Raises a toast with none of the policy above applied.
        ///
        /// Exists to answer one question that guesswork could not: whether this
        /// phone shows a toast from this app at all. Every real notification is
        /// filtered by settings, by peer, and by where the user is, so a silent
        /// phone has several possible explanations - and separating "the toast was
        /// never raised" from "the toast was raised and not shown" needs a path
        /// with no filtering in it.
        /// </summary>
        public static void ShowTest(string title, string body)
        {
            Show(title, body, AppSettings.Current.NotificationSound);
        }

        private static string Summarise(TextMessage m)
        {
            if (!string.IsNullOrEmpty(m.Text))
                return m.Text.Length > 120 ? m.Text.Substring(0, 120) + "..." : m.Text;

            if (m.Media != null) return m.Media.Describe();
            return "new message";
        }

        /// <summary>
        /// Shows a system toast.
        ///
        /// ShellToast is silent by default; the sound setting is honoured by
        /// pointing it at a sound file only when the user asked for one. Windows
        /// Phone shows a toast from the foreground only for a *different* app, so in
        /// practice this is what the user sees when Lumigram is backgrounded, and
        /// the in-app banner covers the foreground case.
        /// </summary>
        private static void Show(string title, string body, bool withSound)
        {
            // ShellToast is a shell API and wants the UI thread, and this is called
            // from the receive loop. Failing off-thread would be invisible, since a
            // toast that cannot be shown is deliberately swallowed below.
            try
            {
                System.Windows.Threading.Dispatcher dispatcher = Deployment.Current.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(delegate { ShowCore(title, body, withSound); });
                    return;
                }
            }
            catch (Exception)
            {
                // No dispatcher at all - the background agent's process.
            }

            ShowCore(title, body, withSound);
        }

        private static void ShowCore(string title, string body, bool withSound)
        {
            try
            {
                var toast = new ShellToast
                {
                    Title = title,
                    Content = body,
                    NavigationUri = new Uri("/ChatsPage.xaml", UriKind.Relative),
                };

                if (withSound)
                {
                    // An empty Sound means the system default; leaving the property
                    // alone entirely is what keeps it silent.
                    toast.Sound = new Uri("", UriKind.RelativeOrAbsolute);
                }

                toast.Show();
            }
            catch (Exception)
            {
                // A toast that cannot be shown must never take a message down with it.
            }
        }
    }
}
