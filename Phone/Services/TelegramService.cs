using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace Lumigram.Phone.Services
{
    /// <summary>
    /// Owns the connection for the whole app.
    ///
    /// One connection, shared by every page. Pages come and go as the user
    /// navigates; the authorisation and the receive loop must not, or every
    /// navigation would cost a handshake - which on this hardware is seconds.
    /// </summary>
    internal static class TelegramService
    {
        private const string DefaultHost = "149.154.167.51";   // production DC2, the bootstrap
        private static readonly object _lock = new object();

        private static MtprotoClient _client;
        private static PhoneTransport _transport;
        private static PhoneSession _session;

        public static readonly ICrypto Crypto = new PhoneCrypto();

        /// <summary>Raised for messages arriving while the app is open.</summary>
        public static event Action<List<TextMessage>> MessagesReceived;

        /// <summary>Raised when the connection drops, so pages can show it.</summary>
        public static event Action<string> ConnectionLost;

        /// <summary>Raised after a dropped connection is re-established.</summary>
        public static event Action Reconnected;

        /// <summary>
        /// Raised when the stored authorisation is no longer valid and the user has
        /// to sign in again.
        /// </summary>
        public static event Action SignedOutRemotely;

        /// <summary>Raised when a chat has been marked read, so the list can update.</summary>
        public static event Action<long> ChatRead;

        /// <summary>
        /// Raised when the device clock is far enough out to be worth telling the
        /// user about. The argument is how many seconds the phone is behind the
        /// server - negative when it is ahead.
        /// </summary>
        public static event Action<int> ClockSkewDetected;

        /// <summary>
        /// Records something that went wrong but did not stop anything, so it shows
        /// up on the diagnostics line instead of vanishing.
        /// </summary>
        public static void NoteWarning(string message)
        {
            _lastError = message ?? "";
            UpdateDiagnostics();
        }

        /// <summary>Server time minus device time, in seconds.</summary>
        public static int ClockOffset { get; private set; }

        /// <summary>
        /// Below this the phone is close enough that saying so would be noise. Well
        /// above the tolerance the protocol layer corrects at, because compensating
        /// silently is the right default - the warning is for a clock wrong enough
        /// that the user will see it elsewhere too.
        /// </summary>
        private const int ClockWarnSeconds = 60;

        private static bool _clockWarned;

        /// <summary>
        /// Records a corrected clock offset and, if it is large, says so once.
        ///
        /// Persisted with the session because the offset is what makes the next
        /// connection work first time: the alternative is rediscovering it after a
        /// request has already been refused.
        /// </summary>
        private static void OnClockAdjusted(int offset)
        {
            ClockOffset = offset;
            UpdateDiagnostics();

            try
            {
                if (_session != null)
                {
                    _session.TimeOffset = offset;
                    _session.Save();
                }
            }
            catch (Exception) { }

            if (Math.Abs(offset) < ClockWarnSeconds || _clockWarned) return;
            _clockWarned = true;

            Action<int> handler = ClockSkewDetected;
            if (handler != null) handler(offset);
        }

        /// <summary>
        /// How wrong the phone's clock is, in words, or null when it is fine.
        /// Available to a page that starts after the correction already happened.
        /// </summary>
        public static string ClockWarning()
        {
            int off = ClockOffset;
            if (Math.Abs(off) < ClockWarnSeconds) return null;

            int minutes = Math.Abs(off) / 60;
            string amount = minutes >= 120 ? (minutes / 60) + " hours"
                          : minutes >= 60 ? "an hour"
                          : minutes > 0 ? minutes + " min"
                          : Math.Abs(off) + " s";

            return "This phone's clock is " + amount + (off > 0 ? " behind" : " ahead") +
                   ". Lumigram is compensating, but turning on automatic date and time " +
                   "in Settings will fix it properly.";
        }

        public static void NotifyChatRead(long peerId)
        {
            Action<long> handler = ChatRead;
            if (handler != null) handler(peerId);
        }

        private static bool _reconnecting;
        private static bool _polling;

        /// <summary>
        /// How often to ask the server what was missed.
        ///
        /// Pushed updates are meant to make this unnecessary, and on this client they
        /// have not proved reliable - the QR login flow only ever worked by polling.
        /// Rather than leave incoming messages depending on a mechanism that may not
        /// fire, updates.getDifference runs on a timer. It is cheap when nothing has
        /// happened: the server answers differenceEmpty.
        /// </summary>
        public static TimeSpan PollInterval = TimeSpan.FromSeconds(8);

        /// <summary>
        /// A running account of what the update machinery is actually doing.
        ///
        /// There is no console on a phone, and "nothing happens" has too many
        /// possible causes to guess between: the poll may not be running, it may be
        /// throwing, the server may be returning nothing, or the messages may be
        /// arriving and being filtered out by the page. This distinguishes them.
        /// </summary>
        public static string Diagnostics = "not started";
        private static int _polls, _pushes, _delivered, _errors;
        private static string _lastError = "";

        private static void UpdateDiagnostics()
        {
            Diagnostics = (ClockOffset != 0 ? "skew " + ClockOffset + "s  " : "") +
                          "polls " + _polls + "  pushed " + _pushes +
                          "  msgs " + _delivered + "  errs " + _errors +
                          "  pts " + State.Pts +
                          "  bg " + BackgroundControl.TrackingStatus +
                          (_lastError.Length > 0 ? "  [" + _lastError + "]" : "");
        }

        /// <summary>
        /// Shared with the background agent through isolated storage - it runs in
        /// its own process and has no other way to know where the app got to.
        /// </summary>
        public static UpdateState State = UpdateStateStore.Load();

        public static PhoneSession Session { get { return _session; } }
        public static bool IsSignedIn { get { return _session != null && _session.SignedIn; } }

        /// <summary>What we tell Telegram about ourselves. Shared with the agent.</summary>
        public static ClientInfo Info
        {
            get { return AppInfo.Create(); }
        }

        public static bool IsConnected
        {
            get { lock (_lock) return _client != null; }
        }

        /// <summary>
        /// Connects, reusing a stored authorisation key when there is one.
        ///
        /// The distinction matters on this hardware: reusing a key is a socket
        /// connect, while creating one costs a full DH handshake - about 4.6 s on a
        /// Lumia 521.
        /// </summary>
        public static async Task<MtprotoClient> ConnectAsync(Action<string> progress = null)
        {
            progress = progress ?? delegate { };

            lock (_lock)
            {
                if (_client != null) return _client;
            }

            _session = PhoneSession.Load();

            var transport = new PhoneTransport();
            var client = new MtprotoClient(Crypto, transport, delegate { });
            client.Info = Info;
            client.ClockAdjusted += OnClockAdjusted;

            client.UpdateReceived += OnUpdate;
            client.Faulted += OnFaulted;

            if (_session != null)
            {
                progress("Connecting...");
                await client.ConnectWithKeyAsync(_session.Host, TelegramServers.DefaultPort,
                                                 _session.ToAuthKey());
            }
            else
            {
                progress("Establishing a secure connection...");
                await client.ConnectAsync(DefaultHost, TelegramServers.DefaultPort);

                _session = PhoneSession.FromAuthKey(client.AuthKey, DefaultHost);
                _session.Save();
            }

            lock (_lock)
            {
                _transport = transport;
                _client = client;
            }

            await CatchUpAsync(client);
            StartPolling();
            return client;
        }

        /// <summary>
        /// True for the errors that mean the stored authorisation is finished.
        /// </summary>
        public static bool IsAuthGone(RpcException ex)
        {
            string t = ex.ErrorType ?? "";
            return t.Contains("AUTH_KEY_UNREGISTERED")
                || t.Contains("SESSION_REVOKED")
                || t.Contains("SESSION_EXPIRED")
                || t.Contains("USER_DEACTIVATED")
                || t.Contains("AUTH_KEY_DUPLICATED");
        }

        private static void HandleAuthGone()
        {
            lock (_lock)
            {
                if (_session == null) return;      // already handled

                // Expected while signing in: the key is real but has no account yet.
                // Only a *signed-in* session going away means anything.
                if (!_session.SignedIn) return;
            }

            Disconnect();
            PhoneSession.Delete();
            _session = null;
            State = new UpdateState();

            Action handler = SignedOutRemotely;
            if (handler != null) handler();
        }

        /// <summary>
        /// Asks for missed messages on a timer, for as long as we are connected.
        ///
        /// Only once signed in. Before that the authorisation key exists but carries
        /// no account, so updates.getState correctly answers AUTH_KEY_UNREGISTERED -
        /// and treating that as a lost session tears down the very connection a
        /// login is waiting on.
        /// </summary>
        private static void StartPolling()
        {
            lock (_lock)
            {
                if (_polling) return;
                if (_session == null || !_session.SignedIn) return;
                _polling = true;
            }

            Task.Factory.StartNew(async delegate
            {
                while (true)
                {
                    await Task.Delay(PollInterval);

                    MtprotoClient client;
                    lock (_lock) client = _client;
                    if (client == null) continue;          // reconnect logic owns this

                    try { await CatchUpAsync(client); }
                    catch (Exception) { }
                }
            }, TaskCreationOptions.LongRunning);
        }

        /// <summary>
        /// Fetches anything that happened while disconnected.
        ///
        /// Updates are only pushed to a live connection, so without this every
        /// message that arrives while the app is closed - or during a dropped
        /// connection - is invisible until something else forces a reload.
        /// </summary>
        private static async Task CatchUpAsync(MtprotoClient client)
        {
            if (_session == null || !_session.SignedIn) return;

            try
            {
                _polls++;

                if (State.Pts == 0)
                {
                    // First run: learn where we are, so later differences are relative
                    // to something. There is nothing to report yet.
                    State = await UpdateReader.GetStateAsync(client, Info);
                    UpdateStateStore.Save(State);
                    UpdateDiagnostics();
                    return;
                }

                List<TextMessage> missed = await UpdateReader.GetDifferenceAsync(client, State);
                UpdateStateStore.Save(State);
                _delivered += missed.Count;
                UpdateDiagnostics();

                if (missed.Count > 0) Deliver(missed);
            }
            catch (RpcException ex) when (IsAuthGone(ex))
            {
                // The key is dead: signed out elsewhere, revoked, or belonging to a
                // different datacenter. Retrying cannot fix it, and quietly retrying
                // forever is how an app ends up looking broken instead of logged out.
                _lastError = ex.ErrorType;
                UpdateDiagnostics();
                HandleAuthGone();
            }
            catch (Exception ex)
            {
                _errors++;
                _lastError = ex.GetType().Name + ": " +
                             (ex.Message.Length > 40 ? ex.Message.Substring(0, 40) : ex.Message);
                UpdateDiagnostics();
            }
        }

        /// <summary>
        /// Reconnects to a different datacenter, which a PHONE_MIGRATE_n error
        /// demands: an account lives on one datacenter and only that one will
        /// authenticate it.
        /// </summary>
        public static async Task<MtprotoClient> MigrateAsync(int dcId, Action<string> progress = null)
        {
            string host = DatacenterHost(dcId);
            Disconnect();

            progress = progress ?? delegate { };
            progress("Connecting to datacenter " + dcId + "...");

            var transport = new PhoneTransport();
            var client = new MtprotoClient(Crypto, transport, delegate { });
            client.Info = Info;
            client.ClockAdjusted += OnClockAdjusted;
            client.UpdateReceived += OnUpdate;
            client.Faulted += OnFaulted;

            await client.ConnectAsync(host, TelegramServers.DefaultPort);

            _session = PhoneSession.FromAuthKey(client.AuthKey, host);
            _session.Save();

            lock (_lock)
            {
                _transport = transport;
                _client = client;
            }
            return client;
        }

        /// <summary>
        /// Opens a second connection without disturbing the current one.
        ///
        /// Needed for the QR migrate step: the login token expires in seconds, and
        /// the handshake on the new datacenter takes about 4.6 s on this hardware.
        /// Tearing down the old connection first means the token is often dead
        /// before it can be imported. With both alive, a fresh token can be fetched
        /// from the old datacenter and imported into the new one immediately.
        /// </summary>
        public static async Task<MtprotoClient> ConnectSeparateAsync(int dcId,
                                                                     Action<string> progress = null)
        {
            progress = progress ?? delegate { };
            string host = DatacenterHost(dcId);

            progress("Preparing datacenter " + dcId + "...");

            var transport = new PhoneTransport();
            var client = new MtprotoClient(Crypto, transport, delegate { });
            client.Info = Info;
            client.ClockAdjusted += OnClockAdjusted;
            await client.ConnectAsync(host, TelegramServers.DefaultPort);
            return client;
        }

        /// <summary>Makes a separately established connection the app's connection.</summary>
        public static void Adopt(MtprotoClient client, int dcId)
        {
            Disconnect();

            client.UpdateReceived += OnUpdate;
            client.Faulted += OnFaulted;

            _session = PhoneSession.FromAuthKey(client.AuthKey, DatacenterHost(dcId));
            _session.Save();

            lock (_lock) { _client = client; _transport = null; }
            StartPolling();
        }

        public static string HostFor(int dcId) { return DatacenterHost(dcId); }

        private static string DatacenterHost(int dcId)
        {
            switch (dcId)
            {
                case 1: return "149.154.175.50";
                case 2: return "149.154.167.51";
                case 3: return "149.154.175.100";
                case 4: return "149.154.167.91";
                case 5: return "149.154.171.5";
                default: return DefaultHost;
            }
        }

        public static void MarkSignedIn()
        {
            if (_session == null) return;
            _session.SignedIn = true;
            _session.PhoneCodeHash = null;          // no longer meaningful once signed in
            _session.Save();

            // Polling is gated on being signed in, so it has to be kicked off here
            // rather than at connect time.
            StartPolling();
        }

        public static void SaveSalt()
        {
            lock (_lock)
            {
                if (_session == null || _client == null) return;
                _session.ServerSalt = _client.ServerSalt;
                _session.Save();
            }
        }

        /// <summary>
        /// Signs out: revokes the session on the server, then deletes the local key.
        ///
        /// Order matters. Deleting the key first would leave a session alive on the
        /// server that nothing can revoke from this device - it would keep working
        /// and keep appearing under Settings -> Devices.
        ///
        /// The local key is deleted even if the server call fails, because the user
        /// asked to sign out and a network problem should not silently leave a
        /// credential on the phone. The caller is told, so they can revoke it from
        /// another device.
        /// </summary>
        public static async Task<bool> SignOutAsync()
        {
            bool revoked = false;

            try
            {
                MtprotoClient client;
                lock (_lock) client = _client;

                if (client != null)
                    revoked = await Messages.LogOutAsync(client, Info);
            }
            catch (Exception)
            {
                revoked = false;
            }

            Disconnect();
            PhoneSession.Delete();
            _session = null;
            State = new UpdateState();

            try { LiveTile.Clear(); } catch (Exception) { }
            try { MuteStore.Clear(); } catch (Exception) { }

            // Nothing left to listen for. The stored preference is deliberately not
            // changed - StopAll only takes the subscription down, so signing back in
            // restores whatever mode the user chose - but holding location access
            // for a signed-out app is not defensible.
            try { BackgroundControl.StopAll(); } catch (Exception) { }

            return revoked;
        }

        public static void Disconnect()
        {
            MtprotoClient client;
            PhoneTransport transport;

            lock (_lock)
            {
                client = _client;
                transport = _transport;
                _client = null;
                _transport = null;
            }

            if (client != null)
            {
                client.UpdateReceived -= OnUpdate;
                client.Faulted -= OnFaulted;
                try { client.Dispose(); } catch (Exception) { }
            }
            if (transport != null)
            {
                try { transport.Dispose(); } catch (Exception) { }
            }
        }

        private static void OnUpdate(TlObject pushed)
        {
            _pushes++;
            UpdateDiagnostics();

            List<TextMessage> messages;
            try { messages = UpdateReader.Extract(pushed, State); }
            catch (Exception) { return; }

            if (messages.Count == 0) return;

            _delivered += messages.Count;
            UpdateDiagnostics();

            Deliver(messages);
        }

        /// <summary>
        /// Hands a batch to the UI and announces whatever deserves it.
        ///
        /// Announcing here rather than from a page is what makes notifications work
        /// when the app is not on screen: pages detach their handlers when they are
        /// navigated away from, so a policy that lived in one was silent exactly
        /// when it was needed.
        /// </summary>
        private static readonly object _seenLock = new object();
        private static readonly HashSet<string> _seen = new HashSet<string>();
        private static readonly Queue<string> _seenOrder = new Queue<string>();

        /// <summary>
        /// How many recently delivered messages are remembered. Enough to cover a
        /// push and the getDifference that follows it, without growing without end.
        /// </summary>
        private const int SeenMemory = 256;

        /// <summary>
        /// Drops messages that have already been delivered once.
        ///
        /// The same message legitimately arrives twice: once as a pushed update, and
        /// again from getDifference, which is the call that actually advances pts.
        /// Everything downstream was therefore processing it twice - two unread
        /// increments, two tile entries, two toasts - and it only ever looked right
        /// because the chat list reloads its counts from the server.
        ///
        /// Keyed by peer and message id together: ids are unique within a chat, not
        /// across chats.
        /// </summary>
        private static List<TextMessage> Undelivered(List<TextMessage> messages)
        {
            var fresh = new List<TextMessage>(messages.Count);

            lock (_seenLock)
            {
                foreach (TextMessage m in messages)
                {
                    // Nothing to key on - deliver it rather than guess.
                    if (m.Id == 0) { fresh.Add(m); continue; }

                    long peer = m.PeerId != 0 ? m.PeerId : m.FromId;
                    string key = peer + ":" + m.Id;

                    if (!_seen.Add(key)) continue;

                    _seenOrder.Enqueue(key);
                    fresh.Add(m);
                }

                while (_seenOrder.Count > SeenMemory) _seen.Remove(_seenOrder.Dequeue());
            }

            return fresh;
        }

        private static void Deliver(List<TextMessage> messages)
        {
            messages = Undelivered(messages);
            if (messages.Count == 0) return;

            Action<List<TextMessage>> handler = MessagesReceived;
            if (handler != null)
            {
                try { handler(messages); }
                catch (Exception) { }
            }

            try { Notifier.Handle(messages); }
            catch (Exception) { }

            // The tile counts every chat that received something, whatever the
            // notification policy says: the badge is a count of what is waiting, not
            // of interruptions, and a channel the user chose not to be toasted about
            // still has unread messages in it. The chat list corrects this with the
            // server's own numbers as soon as it is on screen.
            try
            {
                Func<long, string> nameFor = Notifier.NameSource;

                foreach (TextMessage m in messages)
                {
                    if (m.Out) continue;

                    long peer = m.PeerId != 0 ? m.PeerId : m.FromId;
                    if (peer == 0) continue;

                    string title = null;
                    try { if (nameFor != null) title = nameFor(peer); }
                    catch (Exception) { }

                    LiveTile.Add(peer, title);
                }
            }
            catch (Exception) { }
        }

        private static void OnFaulted(Exception ex)
        {
            lock (_lock) { _client = null; _transport = null; }

            Action<string> handler = ConnectionLost;
            if (handler != null) handler(ex.Message);

            Reconnect();
        }

        /// <summary>
        /// Re-establishes a dropped connection, with a backoff.
        ///
        /// Without this a single network blip ends updates for the rest of the
        /// session: the socket is gone, nothing re-opens it, and the app looks alive
        /// while receiving nothing. The backoff keeps a persistent failure - no
        /// signal, airplane mode - from spinning the radio flat.
        /// </summary>
        private static void Reconnect()
        {
            lock (_lock)
            {
                if (_reconnecting || _session == null) return;
                _reconnecting = true;
            }

            Task.Factory.StartNew(async delegate
            {
                int[] backoffSeconds = { 2, 5, 10, 20, 30, 60 };
                int attempt = 0;

                while (true)
                {
                    int wait = backoffSeconds[Math.Min(attempt, backoffSeconds.Length - 1)];
                    await Task.Delay(TimeSpan.FromSeconds(wait));
                    attempt++;

                    bool stop;
                    lock (_lock) stop = _client != null || _session == null;
                    if (stop) break;

                    try
                    {
                        await ConnectAsync();
                        Action handler = Reconnected;
                        if (handler != null) handler();
                        break;
                    }
                    catch (Exception)
                    {
                        // Keep trying; the backoff grows.
                    }
                }

                lock (_lock) _reconnecting = false;
            });
        }
    }
}
