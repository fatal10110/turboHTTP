using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// The bounded CONNECT reply reader shared by RawSocketTransport and the WebSocket proxy
    /// tunnel (#44, #166): per-line and header-block caps, 2xx replies never drained
    /// (RFC 9110 section 9.3.6), exact framing for non-2xx drains.
    /// </summary>
    [TestFixture]
    public class ConnectReplyReaderTests
    {
        [Test]
        public void StatusLineWithoutLf_FailsAtLineCap_WithBoundedRead()
        {
            var flood = new byte[200 * 1024];
            for (var i = 0; i < flood.Length; i++)
                flood[i] = (byte)'A';
            using var stream = new MemoryStream(Concat(Ascii("HTTP/1.1 200 "), flood), writable: false);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(
                () => ConnectReplyReader.ReadAsync(stream, CancellationToken.None));

            Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
            StringAssert.Contains("line exceeded", ex.HttpError.Message);
            Assert.LessOrEqual(stream.Position, ConnectReplyReader.MaxLineBytes + 1);
        }

        [Test]
        public void HeaderLineWithoutLf_FailsAtLineCap()
        {
            var flood = new string('x', 64 * 1024);
            using var stream = new MemoryStream(
                Ascii("HTTP/1.1 200 Connection Established\r\nX-Flood: " + flood),
                writable: false);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(
                () => ConnectReplyReader.ReadAsync(stream, CancellationToken.None));

            Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
            StringAssert.Contains("line exceeded", ex.HttpError.Message);
            Assert.Less(stream.Position, 2 * ConnectReplyReader.MaxLineBytes);
        }

        [Test]
        public void BareCarriageReturnFlood_FailsAtLineCap()
        {
            var flood = new byte[64 * 1024];
            for (var i = 0; i < flood.Length; i++)
                flood[i] = (byte)'\r';
            using var stream = new MemoryStream(
                Concat(Ascii("HTTP/1.1 200 Connection Established\r\n"), flood),
                writable: false);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(
                () => ConnectReplyReader.ReadAsync(stream, CancellationToken.None));

            Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
            Assert.Less(stream.Position, 2 * ConnectReplyReader.MaxLineBytes);
        }

        [Test]
        public void HeaderBlockOverTotalCap_FailsWithBoundedRead()
        {
            var sb = new StringBuilder("HTTP/1.1 200 Connection Established\r\n");
            var value = new string('v', 1000);
            for (var i = 0; i < 40; i++)
                sb.Append("X-H").Append(i).Append(": ").Append(value).Append("\r\n");
            sb.Append("\r\n");
            using var stream = new MemoryStream(Ascii(sb.ToString()), writable: false);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(
                () => ConnectReplyReader.ReadAsync(stream, CancellationToken.None));

            Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
            StringAssert.Contains("header block exceeded", ex.HttpError.Message);
            Assert.LessOrEqual(stream.Position, ConnectReplyReader.MaxHeaderBytes + 1);
        }

        [Test]
        public void StatusLineCountsTowardHeaderBlockCap()
        {
            // A status line and one header line, each exactly at the line cap: the header alone
            // fits the block cap, the two together (plus terminators) do not.
            var reason = new string('r', ConnectReplyReader.MaxLineBytes - "HTTP/1.1 200 ".Length);
            var header = "X-Fill: " + new string('f', ConnectReplyReader.MaxLineBytes - "X-Fill: ".Length);
            using var stream = new MemoryStream(
                Ascii("HTTP/1.1 200 " + reason + "\r\n" + header + "\r\n\r\n"),
                writable: false);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(
                () => ConnectReplyReader.ReadAsync(stream, CancellationToken.None));

            StringAssert.Contains("header block exceeded", ex.HttpError.Message);
        }

        [Test]
        public void EofInsideHeaderBlock_Fails()
        {
            using var stream = new MemoryStream(
                Ascii("HTTP/1.1 200 Connection Established\r\nVia: proxy\r\n"),
                writable: false);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(
                () => ConnectReplyReader.ReadAsync(stream, CancellationToken.None));

            Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
        }

        [TestCase("HTTP/1.1 2x0 OK")]
        [TestCase("HTTP/1.1 2000 OK")]
        [TestCase("ICY 200 OK")]
        [TestCase("HTTP/1.1")]
        public void MalformedStatusLine_Fails(string statusLine)
        {
            using var stream = new MemoryStream(Ascii(statusLine + "\r\n\r\n"), writable: false);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(
                () => ConnectReplyReader.ReadAsync(stream, CancellationToken.None));

            Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
        }

        [TestCase(200, true)]
        [TestCase(204, true)]
        [TestCase(299, true)]
        [TestCase(300, false)]
        [TestCase(407, false)]
        public void AnyTwoHundredReply_EstablishesTunnel(int status, bool tunnel)
        {
            AssertAsync.Run(async () =>
            {
                using var stream = new MemoryStream(Ascii("HTTP/1.1 " + status + "\r\n\r\n"), writable: false);
                var reply = await ConnectReplyReader.ReadAsync(stream, CancellationToken.None);
                Assert.AreEqual(status, reply.StatusCode);
                Assert.AreEqual(tunnel, reply.IsTunnelEstablished);
            });
        }

        [Test]
        public void TwoHundredWithFramingHeaders_LeavesTunnelBytesUnread()
        {
            AssertAsync.Run(async () =>
            {
                var head = "HTTP/1.1 200 Connection Established\r\n" +
                           "Content-Length: 5\r\n" +
                           "Transfer-Encoding: chunked\r\n\r\n";
                using var stream = new MemoryStream(Concat(Ascii(head), Ascii("TUNNL")), writable: false);

                var reply = await ConnectReplyReader.ReadAsync(stream, CancellationToken.None);
                Assert.IsTrue(reply.IsTunnelEstablished);
                Assert.AreEqual(head.Length, stream.Position);

                await ConnectReplyReader.DrainBodyAsync(stream, reply, CancellationToken.None);
                Assert.AreEqual(head.Length, stream.Position, "a 2xx CONNECT reply has no body to drain");
            });
        }

        [Test]
        public void ProxyAuthContentLengthDrain_StopsAtNextReply()
        {
            AssertAsync.Run(async () =>
            {
                using var stream = new MemoryStream(
                    Ascii("HTTP/1.1 407 Proxy Authentication Required\r\n" +
                          "Content-Length: 3\r\n\r\nabc" +
                          "HTTP/1.1 200 Connection Established\r\n\r\nTUN"),
                    writable: false);

                var first = await ConnectReplyReader.ReadAsync(stream, CancellationToken.None);
                Assert.AreEqual(407, first.StatusCode);
                await ConnectReplyReader.DrainBodyAsync(stream, first, CancellationToken.None);

                var second = await ConnectReplyReader.ReadAsync(stream, CancellationToken.None);
                Assert.AreEqual(200, second.StatusCode);
                Assert.AreEqual(stream.Length - 3, stream.Position);
            });
        }

        [Test]
        public void ProxyAuthWithNonChunkedTransferEncoding_CannotBeDrained()
        {
            AssertAsync.Run(async () =>
            {
                using var stream = new MemoryStream(
                    Ascii("HTTP/1.1 407 Proxy Authentication Required\r\n" +
                          "Transfer-Encoding: gzip\r\nContent-Length: 3\r\n\r\nabc"),
                    writable: false);

                var reply = await ConnectReplyReader.ReadAsync(stream, CancellationToken.None);
                var ex = AssertAsync.ThrowsAsync<UHttpException>(
                    () => ConnectReplyReader.DrainBodyAsync(stream, reply, CancellationToken.None));
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
            });
        }

        [Test]
        public void ProxyAuthBodyOverDrainCap_FailsWithoutReading()
        {
            AssertAsync.Run(async () =>
            {
                var head = "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: " +
                           (ConnectReplyReader.MaxDrainBytes + 1) + "\r\n\r\n";
                using var stream = new MemoryStream(Concat(Ascii(head), new byte[64]), writable: false);

                var reply = await ConnectReplyReader.ReadAsync(stream, CancellationToken.None);
                var ex = AssertAsync.ThrowsAsync<UHttpException>(
                    () => ConnectReplyReader.DrainBodyAsync(stream, reply, CancellationToken.None));
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.AreEqual(head.Length, stream.Position);
            });
        }

        [Test]
        public void ProxyAuthChunkedBodyOverDrainCap_Fails()
        {
            AssertAsync.Run(async () =>
            {
                var head = "HTTP/1.1 407 Proxy Authentication Required\r\nTransfer-Encoding: chunked\r\n\r\n" +
                           (ConnectReplyReader.MaxDrainBytes + 1).ToString("x") + "\r\n";
                using var stream = new MemoryStream(Concat(Ascii(head), new byte[64]), writable: false);

                var reply = await ConnectReplyReader.ReadAsync(stream, CancellationToken.None);
                var ex = AssertAsync.ThrowsAsync<UHttpException>(
                    () => ConnectReplyReader.DrainBodyAsync(stream, reply, CancellationToken.None));
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
            });
        }

        /// <summary>
        /// #44 regression: a 200 CONNECT reply carrying Content-Length must hand the very next byte
        /// to the TLS handshake. The old code drained Content-Length bytes before the status test.
        /// </summary>
        [Test]
        public void EstablishConnectTunnel_TwoHundredWithContentLength_DoesNotDrainTunnelBytes()
        {
            AssertAsync.Run(async () =>
            {
                var head = "HTTP/1.1 200 Connection Established\r\nContent-Length: 5\r\n\r\n";
                var stream = new TunnelScriptStream(Concat(Ascii(head), Ascii("\x16\x03\x03\x00\x02")));
                using var transport = new RawSocketTransport(tlsBackend: TlsBackend.SslStream);

                var method = typeof(RawSocketTransport).GetMethod(
                    "EstablishConnectTunnelAsync",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(method);

                try
                {
                    await (Task<TlsResult>)method.Invoke(
                        transport,
                        new object[] { stream, "api.example.com", 443, null, CancellationToken.None });
                    Assert.Fail("The scripted stream fails the TLS handshake.");
                }
                catch (Exception ex) when (!(ex is AssertionException))
                {
                    // The TLS provider failed on the scripted stream; only the read position matters.
                }

                Assert.AreEqual(
                    head.Length,
                    stream.ReadOffset,
                    "bytes after a 2xx CONNECT header block belong to the tunnel and must not be drained");
            });
        }

        private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }

        /// <summary>
        /// Serves a scripted proxy reply; the first write (CONNECT) succeeds and any later write
        /// (the TLS ClientHello) fails, so nothing reads past what the CONNECT path consumed.
        /// </summary>
        private sealed class TunnelScriptStream : Stream
        {
            private readonly byte[] _script;
            private int _writes;

            public TunnelScriptStream(byte[] script)
            {
                _script = script;
            }

            public int ReadOffset { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var toCopy = Math.Min(count, _script.Length - ReadOffset);
                if (toCopy <= 0)
                    return 0;
                Buffer.BlockCopy(_script, ReadOffset, buffer, offset, toCopy);
                ReadOffset += toCopy;
                return toCopy;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (++_writes > 1)
                    throw new IOException("Injected TLS write failure.");
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }

            public override void Flush()
            {
            }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
