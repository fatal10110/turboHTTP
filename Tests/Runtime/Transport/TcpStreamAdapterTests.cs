using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport
{
    [TestFixture]
    public class TcpStreamAdapterTests
    {
        [Test]
        public void SaeaStream_ReadAsync_NonArrayBackedMemory_ReceivesPayload()
        {
            AssertAsync.Run(async () =>
            {
                const string payload = "saea-fallback";
                await VerifyReadIntoNonArrayMemoryAsync(
                    socket => new SaeaStream(new SaeaSocketChannel(socket)),
                    payload);
            });
        }

        [Test]
        public void PollSelectStream_ReadAsync_NonArrayBackedMemory_ReceivesPayload()
        {
            AssertAsync.Run(async () =>
            {
                const string payload = "poll-fallback";
                await VerifyReadIntoNonArrayMemoryAsync(
                    socket => new PollSelectStream(new PollSelectSocketChannel(socket)),
                    payload);
            });
        }

        private static async Task VerifyReadIntoNonArrayMemoryAsync(
            Func<Socket, System.IO.Stream> streamFactory,
            string payload)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var acceptTask = listener.AcceptSocketAsync();
                var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                using var serverSocket = await acceptTask;

                var payloadBytes = System.Text.Encoding.ASCII.GetBytes(payload);
                await serverSocket.SendAsync(new ArraySegment<byte>(payloadBytes), SocketFlags.None);

                using var stream = streamFactory(clientSocket);
                using var owner = new NonArrayMemoryOwner(payloadBytes.Length);

                var read = await stream.ReadAsync(owner.Memory, CancellationToken.None);

                Assert.AreEqual(payloadBytes.Length, read);
                Assert.AreEqual(payload, System.Text.Encoding.ASCII.GetString(owner.GetSpan().Slice(0, read).ToArray()));
            }
            finally
            {
                listener.Stop();
            }
        }

        private sealed class NonArrayMemoryOwner : MemoryManager<byte>
        {
            private byte[] _buffer;

            public NonArrayMemoryOwner(int length)
            {
                _buffer = new byte[length];
            }

            public override Span<byte> GetSpan()
            {
                if (_buffer == null)
                    throw new ObjectDisposedException(nameof(NonArrayMemoryOwner));

                return _buffer;
            }

            public override MemoryHandle Pin(int elementIndex = 0)
            {
                throw new NotSupportedException();
            }

            public override void Unpin()
            {
            }

            protected override void Dispose(bool disposing)
            {
                _buffer = null;
            }
        }
    }
}
