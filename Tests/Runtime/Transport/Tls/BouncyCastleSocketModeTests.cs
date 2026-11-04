using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #221: the BouncyCastle provider over each socket I/O mode the pool can select
    /// (<c>SocketIoMode</c>): SAEA, poll/select, and a plain <see cref="NetworkStream"/>. A
    /// multi-record payload goes through the handshake, both directions and record reassembly.
    /// </summary>
    [TestFixture]
    public class BouncyCastleSocketModeTests
    {
        // Several TLS records each way (the record limit is 16 KiB), and not a multiple of it.
        private const int PayloadSize = 70000;
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

        [TestCase("Saea")]
        [TestCase("PollSelect")]
        [TestCase("NetworkStream")]
        public void Handshake_AndMultiRecordEcho_Succeed(string mode)
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrFail();

            using var server = BouncyCastleLoopbackServer.TryStartTls(stream =>
            {
                var received = new byte[PayloadSize];
                BouncyCastleLoopbackServer.ReadExact(stream, received, PayloadSize);
                stream.Write(received, 0, received.Length);
                stream.Flush();
                BouncyCastleLoopbackServer.DrainUntilClosed(stream);
            });
            if (server == null)
            {
                TestRequirements.IgnoreEnvironment("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");
                return;
            }

            var payload = new byte[PayloadSize];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i * 31 + 7);

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                AssertAsync.Run(async () =>
                {
                    await socket.ConnectAsync(IPAddress.Loopback, server.Port);
                    Stream inner = CreateStream(mode, socket);

                    using var cts = new CancellationTokenSource(Bound);
                    var tls = await provider.WrapAsync(inner, "localhost", Array.Empty<string>(), cts.Token);
                    Assert.AreEqual("BouncyCastle", tls.ProviderName);
                    var secure = tls.SecureStream;

                    await secure.WriteAsync(payload, 0, payload.Length, cts.Token);
                    var echo = new byte[PayloadSize];
                    int total = 0;
                    while (total < echo.Length)
                    {
                        int read = await secure.ReadAsync(echo, total, echo.Length - total, cts.Token);
                        Assert.Greater(read, 0, $"End of stream after {total} of {PayloadSize} bytes.");
                        total += read;
                    }

                    CollectionAssert.AreEqual(payload, echo);
                });
            }
            finally
            {
                // The socket owns the connection; closing it ends the server's drain loop.
                socket.Dispose();
            }

            Assert.IsNull(server.HandlerError, server.HandlerError?.ToString());
        }

        private static Stream CreateStream(string mode, Socket socket)
        {
            switch (mode)
            {
                case "Saea":
                    return new SaeaStream(new SaeaSocketChannel(socket));
                case "PollSelect":
                    return new PollSelectStream(new PollSelectSocketChannel(socket));
                default:
                    return new NetworkStream(socket, ownsSocket: false);
            }
        }
    }
}
