using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Phone.Scheduler;
using Microsoft.Phone.Shell;
using Lumigram.Mtproto;
using Lumigram.Phone;
using Lumigram.Phone.Services;

namespace Lumigram.Agent
{
    /// <summary>
    /// The periodic background agent: wakes, checks for messages, notifies, stops.
    ///
    /// Windows Phone runs this roughly every half hour and allows it about
    /// 25 seconds and a small memory budget. Overrun or crash twice and the OS
    /// unschedules the agent permanently, so everything here is on a short leash
    /// and NotifyComplete is called no matter what happens.
    ///
    /// It runs in its own process, sharing nothing with the app but isolated
    /// storage - which is why the session and the update position live in files
    /// rather than in memory.
    /// </summary>
    public class ScheduledAgent : ScheduledTaskAgent
    {
        /// <summary>
        /// Leaves headroom inside the OS budget.
        ///
        /// Being killed for overrunning counts against the two strikes that get an
        /// agent disabled for good, so it is better to give up early and catch the
        /// messages on the next wake.
        /// </summary>
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(18);

        static ScheduledAgent()
        {
            // An unhandled exception in an agent is a strike against it. Break in
            // the debugger when attached; swallow it otherwise.
            System.Windows.Application.Current.UnhandledException += delegate (
                object sender, System.Windows.ApplicationUnhandledExceptionEventArgs e)
            {
                e.Handled = true;
                if (System.Diagnostics.Debugger.IsAttached) System.Diagnostics.Debugger.Break();
            };
        }

        protected override void OnInvoke(ScheduledTask task)
        {
            try
            {
                RunAsync().Wait(Budget);
            }
            catch (Exception)
            {
                // Nothing here is worth a strike.
            }
            finally
            {
                // Must always be called, or the OS treats the agent as hung.
                NotifyComplete();
            }
        }

        private async Task RunAsync()
        {
            AppSettings settings = AppSettings.Current;
            if (!settings.NotificationsEnabled) return;

            PhoneSession session = PhoneSession.Load();
            if (session == null || !session.SignedIn) return;

            UpdateState state = UpdateStateStore.Load();

            using (var transport = new PhoneTransport())
            {
                var crypto = new PhoneCrypto();
                var client = new MtprotoClient(crypto, transport, delegate { });
                client.Info = AppInfo.Create();

                await client.ConnectWithKeyAsync(session.Host, TelegramServers.DefaultPort,
                                                 session.ToAuthKey());

                ClientInfo info = AppInfo.Create();

                if (state.Pts == 0)
                {
                    // Nothing to compare against yet: learn the position and report
                    // nothing. Announcing the whole backlog on first run would be
                    // a wall of toasts.
                    state = await UpdateReader.GetStateAsync(client, info);
                    UpdateStateStore.Save(state);
                    return;
                }

                List<TextMessage> missed = await UpdateReader.GetDifferenceAsync(client, state);
                UpdateStateStore.Save(state);

                Announce(missed, settings);
                UpdateTile(missed);
            }
        }

        /// <summary>
        /// Toasts what arrived, following the same policy the app uses.
        ///
        /// Muted chats are skipped, groups and channels included - the mute set is
        /// shared through storage precisely so this process reaches the same answer
        /// as the app. There is no "chat currently open" here, so that rule alone
        /// does not apply.
        /// </summary>
        /// <summary>
        /// Adds what arrived to the tile.
        ///
        /// Counts every chat that received something, including the groups and
        /// channels this agent deliberately does not toast about - the badge counts
        /// what is waiting, not interruptions. Names are not available here, so the
        /// chats are counted but unnamed; the chat list fills the back of the tile in
        /// the next time the app is opened.
        /// </summary>
        private static void UpdateTile(List<TextMessage> messages)
        {
            if (messages == null) return;

            foreach (TextMessage m in messages)
            {
                if (m.Out) continue;

                long peer = m.PeerId != 0 ? m.PeerId : m.FromId;
                if (peer == 0) continue;

                try { LiveTile.Add(peer, null); }
                catch (Exception) { }
            }
        }

        private static void Announce(List<TextMessage> messages, AppSettings settings)
        {
            if (messages == null) return;

            int shown = 0;
            foreach (TextMessage m in messages)
            {
                if (m.Out) continue;

                long peer = m.PeerId != 0 ? m.PeerId : m.FromId;
                if (MuteStore.IsMuted(peer)) continue;

                // A quiet cap: a long absence can produce dozens of messages, and
                // dozens of toasts is not a notification, it is an assault.
                if (shown >= 3)
                {
                    Toast("Lumigram", (messages.Count - shown) + " more messages", settings);
                    break;
                }

                Toast("Lumigram", Summarise(m), settings);
                shown++;
            }
        }

        private static string Summarise(TextMessage m)
        {
            if (!string.IsNullOrEmpty(m.Text))
                return m.Text.Length > 110 ? m.Text.Substring(0, 110) + "..." : m.Text;
            if (m.Media != null) return m.Media.Describe();
            return "new message";
        }

        private static void Toast(string title, string body, AppSettings settings)
        {
            try
            {
                var toast = new ShellToast
                {
                    Title = title,
                    Content = body,
                    NavigationUri = new Uri("/ChatsPage.xaml", UriKind.Relative),
                };

                if (settings.NotificationSound)
                    toast.Sound = new Uri("", UriKind.RelativeOrAbsolute);

                toast.Show();
            }
            catch (Exception) { }
        }
    }
}
