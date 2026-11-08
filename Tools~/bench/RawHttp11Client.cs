using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace TurboHTTP.Tools.Bench
{
    /// <summary>
    /// The raw-socket floor: one keep-alive TCP connection, a fixed GET written as bytes and a Content-Length
    /// framed response read into one reused buffer. No pooling, no parsing beyond Content-Length, no
    /// verification: the cheapest client the same Kestrel route can have.
    /// </summary>
    internal sealed class RawHttp11Client : IDisposable
    {
        private static readonly byte[] HeadTerminator = { 13, 10, 13, 10 };
        private static readonly byte[] CrLf = { 13, 10 };
        private static readonly byte[] ContentLengthName = Encoding.ASCII.GetBytes("content-length:");

        private readonly IPEndPoint _endpoint;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private Socket _socket;
        private NetworkStream _stream;
        private byte[] _request;
        private int _requestSize = -1;

        public RawHttp11Client(IPAddress address, int port)
        {
            _endpoint = new IPEndPoint(address, port);
        }

        public async Task<long> GetAsync(int size)
        {
            if (_stream == null)
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await _socket.ConnectAsync(_endpoint).ConfigureAwait(false);
                _stream = new NetworkStream(_socket, ownsSocket: true);
            }

            if (size != _requestSize)
            {
                _request = Encoding.ASCII.GetBytes("GET /bytes/" + size.ToString(CultureInfo.InvariantCulture) +
                                                   " HTTP/1.1\r\nHost: 127.0.0.1:" + _endpoint.Port.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
                _requestSize = size;
            }

            await _stream.WriteAsync(_request).ConfigureAwait(false);

            int filled = 0;
            int headEnd;
            while ((headEnd = HeadEnd(_buffer.AsSpan(0, filled))) < 0)
            {
                if (filled == _buffer.Length)
                    throw new InvalidDataException("response head over 64 KiB");
                int n = await _stream.ReadAsync(_buffer.AsMemory(filled)).ConfigureAwait(false);
                if (n == 0)
                    throw new InvalidDataException("connection closed in the response head");
                filled += n;
            }

            long length = ContentLength(_buffer.AsSpan(0, headEnd));
            long body = filled - (headEnd + 4);
            while (body < length)
            {
                int n = await _stream.ReadAsync(_buffer.AsMemory(0, (int)Math.Min(_buffer.Length, length - body))).ConfigureAwait(false);
                if (n == 0)
                    throw new InvalidDataException("connection closed in the body");
                body += n;
            }

            if (body != size)
                throw new InvalidDataException("raw body " + body + " of " + size + " bytes");
            return body;
        }

        private static int HeadEnd(ReadOnlySpan<byte> data) => data.IndexOf(HeadTerminator);

        private static long ContentLength(ReadOnlySpan<byte> head)
        {
            ReadOnlySpan<byte> name = ContentLengthName;
            while (!head.IsEmpty)
            {
                int eol = head.IndexOf(CrLf);
                var line = eol < 0 ? head : head.Slice(0, eol);
                if (line.Length > name.Length && Ascii.EqualsIgnoreCase(line.Slice(0, name.Length), name))
                {
                    var value = Encoding.ASCII.GetString(line.Slice(name.Length)).Trim();
                    return long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
                }

                head = eol < 0 ? ReadOnlySpan<byte>.Empty : head.Slice(eol + 2);
            }

            throw new InvalidDataException("response without Content-Length");
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }
}
