using System;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Lumigram.Mtproto;

namespace Lumigram.Phone
{
    /// <summary>
    /// The transport on Windows Phone 8.1, over WinRT's StreamSocket.
    ///
    /// WP8.1 Silverlight has no System.Net.Sockets, so this is the only way to open
    /// a raw TCP connection. The app manifest must declare ID_CAP_NETWORKING or the
    /// connect attempt fails at runtime rather than at build time.
    /// </summary>
    public sealed class PhoneTransport : ITransport
    {
        private StreamSocket _socket;
        private DataReader _reader;
        private DataWriter _writer;

        public bool IsConnected { get { return _socket != null; } }

        public async Task ConnectAsync(string host, int port)
        {
            _socket = new StreamSocket();

            // MTProto packets are small and latency-sensitive; Nagle would add a
            // round trip of delay to every one of them.
            _socket.Control.NoDelay = true;

            await _socket.ConnectAsync(new HostName(host), port.ToString());

            _reader = new DataReader(_socket.InputStream);
            _writer = new DataWriter(_socket.OutputStream);

            // Without this, LoadAsync(n) returns as soon as *any* bytes arrive and
            // ReceiveExactAsync would have to loop far more than it does.
            _reader.InputStreamOptions = InputStreamOptions.Partial;
        }

        public async Task SendAsync(byte[] data)
        {
            _writer.WriteBytes(data);
            await _writer.StoreAsync();
            await _writer.FlushAsync();
        }

        public async Task<byte[]> ReceiveExactAsync(int count)
        {
            var buffer = new byte[count];
            int read = 0;

            while (read < count)
            {
                uint loaded = await _reader.LoadAsync((uint)(count - read));
                if (loaded == 0)
                    throw new MtprotoException("connection closed after " + read +
                                               " of " + count + " bytes");

                var chunk = new byte[loaded];
                _reader.ReadBytes(chunk);
                System.Buffer.BlockCopy(chunk, 0, buffer, read, (int)loaded);
                read += (int)loaded;
            }

            return buffer;
        }

        public void Dispose()
        {
            // Detach the streams first: disposing a DataReader/DataWriter closes the
            // underlying stream, and doing that out of order throws on WinRT.
            if (_reader != null)
            {
                _reader.DetachStream();
                _reader.Dispose();
                _reader = null;
            }
            if (_writer != null)
            {
                _writer.DetachStream();
                _writer.Dispose();
                _writer = null;
            }
            if (_socket != null)
            {
                _socket.Dispose();
                _socket = null;
            }
        }
    }
}
