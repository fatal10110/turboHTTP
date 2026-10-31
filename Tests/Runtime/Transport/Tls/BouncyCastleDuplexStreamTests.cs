using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http2;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #36: the BouncyCastle secure stream is full duplex. A pending read must not delay a write
    /// (RFC 8446 section 5: the two record-layer directions are independent), which HTTP/2
    /// (read loop always reading) and the HTTP/1.1 Expect: 100-continue timeout path rely on.
    /// </summary>
    [TestFixture]
    public class BouncyCastleDuplexStreamTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

        private static async Task<TlsResult> ConnectAsync(ITlsProvider provider, int port, string[] alpn)
        {
            var stream = await BouncyCastleLoopbackServer.ConnectAsync(port).ConfigureAwait(false);
            try
            {
                using var cts = new CancellationTokenSource(Bound);
                return await provider.WrapAsync(stream, "localhost", alpn, cts.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer, read, buffer.Length - read, ct).ConfigureAwait(false);
                if (n <= 0)
                    throw new EndOfStreamException();
                read += n;
            }
        }

        private static async Task AssertCompletesAsync(Task task, string because)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Bound)).ConfigureAwait(false);
            Assert.AreSame(task, winner, because);
            await task.ConfigureAwait(false);
        }

        [Test]
        public void WriteCompletes_WhileReadIsPending()
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrIgnore();

            // The server answers only after it received the client's bytes, so the client's read
            // stays pending until its write has gone through.
            using var server = BouncyCastleLoopbackServer.TryStartTls(stream =>
            {
                var ping = new byte[4];
                BouncyCastleLoopbackServer.ReadExact(stream, ping, ping.Length);
                var pong = Encoding.ASCII.GetBytes("pong");
                stream.Write(pong, 0, pong.Length);
                stream.Flush();
                BouncyCastleLoopbackServer.DrainUntilClosed(stream);
            });
            if (server == null)
            {
                Assert.Ignore("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");
                return;
            }

            AssertAsync.Run(async () =>
            {
                var tls = await ConnectAsync(provider, server.Port, Array.Empty<string>());
                using var secure = tls.SecureStream;
                using var cts = new CancellationTokenSource(Bound);

                var reply = new byte[4];
                var readTask = ReadExactAsync(secure, reply, cts.Token);
                Assert.IsFalse(readTask.IsCompleted, "Nothing was sent yet; the read must be pending.");

                var writeTask = secure.WriteAsync(Encoding.ASCII.GetBytes("ping"), 0, 4, cts.Token);
                await AssertCompletesAsync(writeTask, "The write must complete while a read is pending.");
                await AssertCompletesAsync(readTask, "The reply must arrive after the write.");

                Assert.AreEqual("pong", Encoding.ASCII.GetString(reply));
            });

            Assert.IsNull(server.HandlerError, server.HandlerError?.ToString());
        }

        [Test]
        public void Http2_FiftyConcurrentRequests_Complete()
        {
            const int requestCount = 50;
            var provider = BouncyCastleLoopbackServer.GetProviderOrIgnore();
            using var server = BouncyCastleLoopbackServer.TryStartTls(stream =>
                ServeHttp2Async(stream).GetAwaiter().GetResult());
            if (server == null)
            {
                Assert.Ignore("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");
                return;
            }

            AssertAsync.Run(async () =>
            {
                var tls = await ConnectAsync(provider, server.Port, new[] { "h2" });
                using var cts = new CancellationTokenSource(Bound);
                using var connection = new Http2Connection(
                    tls.SecureStream, "localhost", server.Port, new Http2Options(), new StreamingOptions());

                await AssertCompletesAsync(connection.InitializeAsync(cts.Token),
                    "SETTINGS ACK must be written while the read loop has a read pending.");

                var responses = new Task<UHttpResponse>[requestCount];
                for (int i = 0; i < requestCount; i++)
                {
                    var request = new UHttpRequest(
                        HttpMethod.GET, new Uri($"https://localhost:{server.Port}/r{i}"));
                    responses[i] = connection.SendRequestAsync(request, new RequestContext(request), cts.Token)
                        .AsTask();
                }

                await AssertCompletesAsync(Task.WhenAll(responses),
                    "All HEADERS writes must proceed while the read loop is reading.");

                foreach (var task in responses)
                {
                    using var response = await task;
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    Assert.AreEqual("ok", response.GetBodyAsString());
                }
            });

            Assert.IsNull(server.HandlerError, server.HandlerError?.ToString());
        }

        [Test]
        public void ExpectContinue_TimeoutPath_SendsBodyOverBouncyCastle()
        {
            BouncyCastleLoopbackServer.GetProviderOrIgnore();
            const string payload = "bc-expect-continue-body";
            string receivedBody = null;

            // Never sends 100 Continue: the client must send the body after its timeout while its
            // read for the interim/final response head is still pending.
            using var server = BouncyCastleLoopbackServer.TryStartTls(stream =>
            {
                int contentLength = ReadRequestHeadContentLength(stream);
                var body = new byte[contentLength];
                BouncyCastleLoopbackServer.ReadExact(stream, body, contentLength);
                Volatile.Write(ref receivedBody, Encoding.UTF8.GetString(body));

                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                stream.Write(response, 0, response.Length);
                stream.Flush();
                BouncyCastleLoopbackServer.DrainUntilClosed(stream);
            });
            if (server == null)
            {
                Assert.Ignore("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");
                return;
            }

            AssertAsync.Run(async () =>
            {
                using var pool = new TcpConnectionPool(tlsBackend: TlsBackend.BouncyCastle);
                using var transport = new RawSocketTransport(
                    pool,
                    TlsBackend.BouncyCastle,
                    new Http2Options(),
                    new StreamingOptions { ExpectContinueTimeoutMs = 100 });

                var request = new UHttpRequest(HttpMethod.POST, new Uri($"https://localhost:{server.Port}/continue"))
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(payload), writable: false)),
                        payload.Length)
                    .WithExpectContinue()
                    .WithTimeout(TimeSpan.FromSeconds(60));
                var send = TransportDispatchHelper.CollectResponseAsync(
                    transport.DispatchAsync, request, new RequestContext(request));

                await AssertCompletesAsync(send, "The body write must not wait for the pending head read.");
                using var response = await send;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("ok", response.GetBodyAsString());
            });

            Assert.AreEqual(payload, Volatile.Read(ref receivedBody));
            Assert.IsNull(server.HandlerError, server.HandlerError?.ToString());
        }

        /// <summary>Reads a request head (bounded) and returns its Content-Length.</summary>
        private static int ReadRequestHeadContentLength(Stream stream)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (head.Length > 16 * 1024)
                    throw new InvalidDataException("Request head too large.");
                BouncyCastleLoopbackServer.ReadExact(stream, one, 1);
                head.Append((char)one[0]);
            }

            foreach (var line in head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim()
                        .Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    return int.Parse(line.Substring(colon + 1).Trim());
                }
            }

            throw new InvalidDataException("Request has no Content-Length.");
        }

        /// <summary>
        /// Minimal HTTP/2 server: SETTINGS exchange, then ":status 200" + "ok" for every request
        /// stream. Runs on the connection's dedicated server thread.
        /// </summary>
        private static async Task ServeHttp2Async(Stream stream)
        {
            var preface = new byte[24];
            await ReadExactAsync(stream, preface, CancellationToken.None).ConfigureAwait(false);

            var codec = new Http2FrameCodec(stream);
            await codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Settings,
                Flags = Http2FrameFlags.None,
                StreamId = 0,
                Payload = Array.Empty<byte>(),
                Length = 0
            }, CancellationToken.None).ConfigureAwait(false);

            var statusOk = new byte[] { 0x88 }; // HPACK static index 8: ":status: 200"
            var body = Encoding.ASCII.GetBytes("ok");
            while (true)
            {
                Http2Frame frame;
                try
                {
                    frame = await codec.ReadFrameAsync(16384, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
                    return; // client closed
                }

                switch (frame.Type)
                {
                    case Http2FrameType.Settings when (frame.Flags & Http2FrameFlags.Ack) == 0:
                        await codec.WriteFrameAsync(new Http2Frame
                        {
                            Type = Http2FrameType.Settings,
                            Flags = Http2FrameFlags.Ack,
                            StreamId = 0,
                            Payload = Array.Empty<byte>(),
                            Length = 0
                        }, CancellationToken.None).ConfigureAwait(false);
                        break;

                    case Http2FrameType.Ping when (frame.Flags & Http2FrameFlags.Ack) == 0:
                        await codec.WriteFrameAsync(new Http2Frame
                        {
                            Type = Http2FrameType.Ping,
                            Flags = Http2FrameFlags.Ack,
                            StreamId = 0,
                            Payload = frame.Payload,
                            Length = frame.Length
                        }, CancellationToken.None).ConfigureAwait(false);
                        break;

                    case Http2FrameType.Headers:
                        await codec.WriteFrameAsync(new Http2Frame
                        {
                            Type = Http2FrameType.Headers,
                            Flags = Http2FrameFlags.EndHeaders,
                            StreamId = frame.StreamId,
                            Payload = statusOk,
                            Length = statusOk.Length
                        }, CancellationToken.None).ConfigureAwait(false);
                        await codec.WriteFrameAsync(new Http2Frame
                        {
                            Type = Http2FrameType.Data,
                            Flags = Http2FrameFlags.EndStream,
                            StreamId = frame.StreamId,
                            Payload = body,
                            Length = body.Length
                        }, CancellationToken.None).ConfigureAwait(false);
                        break;

                    case Http2FrameType.GoAway:
                        return;
                }
            }
        }
    }
}
