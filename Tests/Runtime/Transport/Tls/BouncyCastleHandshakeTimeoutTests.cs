using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #142: the BouncyCastle handshake is bounded as a whole. BouncyCastle checks its handshake
    /// timeout only in blocking mode, which the provider does not use, so the provider enforces
    /// <c>TurboTlsClient.GetHandshakeTimeoutMillis</c> itself by disposing the inner stream at the
    /// deadline. The bound holds for a silent peer and for one that keeps sending records.
    /// </summary>
    [TestFixture]
    public class BouncyCastleHandshakeTimeoutTests
    {
        [Test]
        public void ServerThatNeverSendsServerHello_FailsAtTheHandshakeBound()
        {
            const int timeoutMillis = 500;
            var provider = ClientHelloCapture.CreateBouncyCastleProvider(timeoutMillis);
            using var server = BouncyCastleLoopbackServer.StartRaw(stream =>
            {
                ClientHelloCapture.ReadHandshakeMessage(stream);
                BouncyCastleLoopbackServer.DrainUntilClosed(stream); // never answers
            });

            var stopwatch = Stopwatch.StartNew();
            var ex = AssertAsync.ThrowsAsync<IOException>(
                async () => await HandshakeAsync(provider, server.Port));
            stopwatch.Stop();

            Assert.IsInstanceOf<TimeoutException>(ex.InnerException, ex.ToString());
            StringAssert.Contains("timed out after 500 ms", ex.Message);
            Assert.GreaterOrEqual(stopwatch.ElapsedMilliseconds, timeoutMillis - 50,
                "The handshake failed before its bound.");
            Assert.Less(stopwatch.ElapsedMilliseconds, 15000,
                "The handshake must fail at its bound, not at the 30 s caller timeout.");
            Assert.IsNull(server.HandlerError, "Scripted server failed: " + server.HandlerError);
        }

        [Test]
        public void ServerTricklingRecords_FailsAtTheHandshakeBound()
        {
            const int timeoutMillis = 1000;
            const int intervalMillis = 50;
            var provider = ClientHelloCapture.CreateBouncyCastleProvider(timeoutMillis);
            int recordsSent = 0;
            using var stopTrickle = new ManualResetEventSlim(false);
            using var server = BouncyCastleLoopbackServer.StartRaw(stream =>
            {
                ClientHelloCapture.ReadHandshakeMessage(stream);

                // A TLS 1.2 ServerHello followed by a Certificate message that claims 16000 bytes,
                // one byte per record: every record is valid and moves the handshake forward, so
                // the client never sits idle on a read for long. 20 s of records in total.
                var bytes = BuildTrickledHandshake();
                try
                {
                    for (int i = 0; i < bytes.Length && i < 400; i++)
                    {
                        var record = new byte[] { ClientHelloCapture.HandshakeRecord, 0x03, 0x03, 0x00, 0x01, bytes[i] };
                        stream.Write(record, 0, record.Length);
                        stream.Flush();
                        Interlocked.Increment(ref recordsSent);
                        if (stopTrickle.Wait(intervalMillis)) // the scripted peer's pace
                            return;
                    }
                }
                catch (IOException)
                {
                    // The client closed the connection: expected.
                }
            });

            var stopwatch = Stopwatch.StartNew();
            var ex = AssertAsync.ThrowsAsync<IOException>(
                async () => await HandshakeAsync(provider, server.Port));
            stopwatch.Stop();
            int sent = Volatile.Read(ref recordsSent);
            stopTrickle.Set();

            Assert.IsInstanceOf<TimeoutException>(ex.InnerException, ex.ToString());
            Assert.GreaterOrEqual(sent, 5, "The peer must have been sending records during the handshake.");
            Assert.GreaterOrEqual(stopwatch.ElapsedMilliseconds, timeoutMillis - 50,
                "The handshake failed before its bound.");
            Assert.Less(stopwatch.ElapsedMilliseconds, 10000,
                "A trickling peer must not extend the handshake past its bound.");
        }

        private static async Task<TlsResult> HandshakeAsync(ITlsProvider provider, int port)
        {
            // Only failing handshakes use this helper; the provider disposes the stream on failure.
            var stream = await BouncyCastleLoopbackServer.ConnectAsync(port).ConfigureAwait(false);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await provider.WrapAsync(stream, "localhost", new[] { "http/1.1" }, cts.Token)
                .ConfigureAwait(false);
        }

        private static byte[] BuildTrickledHandshake()
        {
            var bytes = new MemoryStream();

            // ServerHello (TLS 1.2): TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256, renegotiation_info.
            var hello = new MemoryStream();
            hello.WriteByte(0x03);
            hello.WriteByte(0x03);
            for (int i = 0; i < 32; i++)
                hello.WriteByte(0x5A);
            hello.WriteByte(0x00);
            hello.WriteByte(0xC0);
            hello.WriteByte(0x2F);
            hello.WriteByte(0x00);
            hello.Write(new byte[] { 0x00, 0x05, 0xFF, 0x01, 0x00, 0x01, 0x00 }, 0, 7);
            WriteHandshakeHeader(bytes, 2, (int)hello.Length);
            hello.WriteTo(bytes);

            // Certificate header claiming a 16000-byte body, then filler that never completes it.
            WriteHandshakeHeader(bytes, 11, 16000);
            for (int i = 0; i < 400; i++)
                bytes.WriteByte(0x00);

            return bytes.ToArray();
        }

        private static void WriteHandshakeHeader(Stream stream, byte type, int length)
        {
            stream.WriteByte(type);
            stream.WriteByte((byte)(length >> 16));
            stream.WriteByte((byte)(length >> 8));
            stream.WriteByte((byte)length);
        }
    }
}
