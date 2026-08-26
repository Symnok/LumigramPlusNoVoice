using System;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Mtproto;
using Lumigram.Phone;

namespace LumigramPlus.App
{
    /// <summary>
    /// The app's connection to Telegram.
    ///
    /// One connection, held for as long as the app runs, because an authorisation
    /// key costs several seconds to negotiate and a login is several calls that must
    /// share one session. Reconnecting between them invalidates whatever the
    /// previous call set up.
    ///
    /// Deliberately smaller than the Silverlight client's equivalent: there is no
    /// polling loop, no reconnect timer and no update plumbing here yet. Those
    /// belong with the chat list, and adding them before there is anything to show
    /// would be building machinery with nothing to drive.
    /// </summary>
    public static class TelegramService
    {
        public static readonly ICrypto Crypto = new PhoneCrypto();

        private static readonly object Gate = new object();
        private static MtprotoClient _client;
        private static PhoneTransport _transport;

        public static SessionStore Session;

        /// <summary>
        /// What this client tells Telegram about itself.
        ///
        /// A fresh instance every time. ClientInfo.Default used to be a shared
        /// mutable object, and call sites assigning over it meant one caller's
        /// credentials silently became everyone's - and anything running before them
        /// sent api_id 0, which Telegram rejects as CONNECTION_API_ID_INVALID.
        /// </summary>
        public static ClientInfo Info
        {
            get
            {
                ClientInfo info = ClientInfo.Default;
                info.ApiId = Secrets.ApiId;
                info.ApiHash = Secrets.ApiHash;
                // Distinct from the Silverlight client on purpose. Both share an
                // api_id, so Telegram lists both under the same registered app name
                // - this is the only field that says which of the two a session
                // actually came from.
                info.DeviceModel = "Windows Phone (Lumigram+)";
                info.SystemVersion = "8.1";
                info.AppVersion = "Lumigram+";
                return info;
            }
        }

        public static MtprotoClient Current
        {
            get { lock (Gate) return _client; }
        }

        /// <summary>
        /// The live connection, opening one if there is not already one.
        ///
        /// Reuses the stored authorisation key when there is one, which is the normal
        /// path after first launch: keys are permanent, and paying for the DH
        /// exchange once is the entire point of storing it.
        /// </summary>
        public static async Task<MtprotoClient> ConnectAsync(Action<string> progress = null)
        {
            lock (Gate) if (_client != null) return _client;

            progress = progress ?? delegate { };

            if (Session == null) Session = await SessionStore.LoadAsync();

            var transport = new PhoneTransport();
            var client = new MtprotoClient(Crypto, transport, delegate { });
            client.Info = Info;

            if (Session != null)
            {
                progress("Connecting...");
                await client.ConnectWithKeyAsync(Session.Host, TelegramServers.DefaultPort,
                                                 Session.ToAuthKey());
            }
            else
            {
                progress("Negotiating an authorisation key...");
                string host = TelegramServers.ProductionDc2Host;

                await client.ConnectAsync(host, TelegramServers.DefaultPort);

                Session = SessionStore.FromAuthKey(client.AuthKey, host);
                await Session.SaveAsync();
            }

            lock (Gate)
            {
                _transport = transport;
                _client = client;
            }

            return client;
        }

        /// <summary>
        /// Opens a second connection without disturbing the current one.
        ///
        /// Needed for the QR migrate step: a login token expires in seconds and the
        /// handshake on the new datacenter takes several. Closing the old connection
        /// first means the token is usually dead before it can be imported, so both
        /// are held open until the import has been made.
        /// </summary>
        public static async Task<MtprotoClient> ConnectSeparateAsync(int dcId,
                                                                     Action<string> progress = null)
        {
            progress = progress ?? delegate { };
            progress("Preparing datacenter " + dcId + "...");

            string host = TelegramServers.HostFor(dcId);

            var transport = new PhoneTransport();
            var client = new MtprotoClient(Crypto, transport, delegate { });
            client.Info = Info;

            await client.ConnectAsync(host, TelegramServers.DefaultPort);
            return client;
        }

        /// <summary>Makes a separately established connection the app's connection.</summary>
        public static async Task AdoptAsync(MtprotoClient client, int dcId)
        {
            MtprotoClient old;
            lock (Gate)
            {
                old = _client;
                _client = client;
                _transport = null;
            }

            if (old != null)
            {
                // Give up the authorisation on the datacenter being left behind.
                //
                // Exporting a login token requires initConnection, which registers a
                // connection attempt - so migrating leaves an authorisation on the
                // old datacenter that will never be completed, and Telegram lists it
                // as an unsuccessful login. Nothing else clears it: the key is not
                // signed in, so there is no session to end from anywhere.
                try { await Messages.LogOutAsync(old, Info); }
                catch (Exception) { }

                try { old.Dispose(); }
                catch (Exception) { }
            }

            Session = SessionStore.FromAuthKey(client.AuthKey, TelegramServers.HostFor(dcId));
            await Session.SaveAsync();
        }

        public static async Task SignedInAsync()
        {
            if (Session == null) return;

            Session.SignedIn = true;
            await Session.SaveAsync();
        }

        public static void Disconnect()
        {
            MtprotoClient client;
            lock (Gate)
            {
                client = _client;
                _client = null;
                _transport = null;
            }

            if (client == null) return;

            try { client.Dispose(); }
            catch (Exception) { }
        }
    }
}
