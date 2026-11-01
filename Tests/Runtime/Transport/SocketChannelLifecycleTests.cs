using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// SAEA dispose bound (#168) and cancellation surfacing as OperationCanceledException in both
    /// channel modes (#170). Loopback sockets only.
    /// </summary>
    [TestFixture]
    public class SocketChannelLifecycleTests
    {
        [Test]
        public void Saea_Dispose_CompletionNeverFires_ReturnsWithinBoundAndKeepsBuffers()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                var channel = new SaeaSocketChannel(pair.Client);
                channel.SimulateStuckReceiveForTest(drainTimeoutMs: 100);
                long leakedBefore = SaeaSocketChannel.BuffersLeakedOnDispose;

                TurboDiagnostics.SetSink(null);
                try
                {
                    var dispose = Task.Run(() => channel.Dispose());
                    var finished = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10)));
                    Assert.AreSame(dispose, finished, "Dispose did not return within its bound");
                    await dispose;
                }
                finally
                {
                    TurboDiagnostics.ResetSink();
                }

                Assert.IsFalse(channel.BuffersReleased, "buffers must not return to the pool while the OS may write");
                Assert.Greater(SaeaSocketChannel.BuffersLeakedOnDispose, leakedBefore);
            });
        }

        [Test]
        public void Saea_Dispose_Idle_ReturnsBuffers()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                var channel = new SaeaSocketChannel(pair.Client);
                channel.Dispose();
                Assert.IsTrue(channel.BuffersReleased);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PreCancelledToken_ThrowsOperationCanceled_BothDirections(bool pollSelect)
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                using var channel = ChannelUnderTest.Create(pair.Client, pollSelect);
                using var cts = new CancellationTokenSource();
                cts.Cancel();

                await AssertCanceledAsync(() => channel.ReceiveAsync(new byte[16], cts.Token), cts.Token);
                await AssertCanceledAsync(() => channel.SendAsync(new byte[16], cts.Token), cts.Token);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TokenCancelledByEarlierOperation_ThenReused_ThrowsOperationCanceled(bool pollSelect)
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                using var channel = ChannelUnderTest.Create(pair.Client, pollSelect);
                using var cts = new CancellationTokenSource();

                // A pending receive cancelled mid-flight disposes the socket.
                var pending = channel.ReceiveAsync(new byte[16], cts.Token);
                cts.Cancel();
                await AssertCanceledAsync(() => pending, null);

                // The socket is gone now; the cancelled token must still win over ObjectDisposedException.
                await AssertCanceledAsync(() => channel.ReceiveAsync(new byte[16], cts.Token), cts.Token);
                await AssertCanceledAsync(() => channel.SendAsync(new byte[16], cts.Token), cts.Token);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CancellationRacingOperationStart_Receive_ThrowsOperationCanceled(bool pollSelect)
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                using var channel = ChannelUnderTest.Create(pair.Client, pollSelect);
                using var cts = new CancellationTokenSource();

                // Cancel after the entry check, right before registration: the registration runs
                // inline and disposes the socket before the socket call is made.
                channel.SetHook(() => cts.Cancel());

                await AssertCanceledAsync(() => channel.ReceiveAsync(new byte[16], cts.Token), cts.Token);
            });
        }

        [Test]
        public void Saea_CancellationRacingOperationStart_Send_ThrowsOperationCanceled()
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await LoopbackPair.CreateAsync();
                using var channel = ChannelUnderTest.Create(pair.Client, pollSelect: false);
                using var cts = new CancellationTokenSource();
                channel.SetHook(() => cts.Cancel());

                await AssertCanceledAsync(() => channel.SendAsync(new byte[16], cts.Token), cts.Token);
            });
        }

        private static async Task AssertCanceledAsync(Func<Task> operation, CancellationToken? expected)
        {
            try
            {
                var task = operation();
                var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.AreSame(task, finished, "the operation neither completed nor was cancelled");
                await task;
            }
            catch (OperationCanceledException ex)
            {
                if (expected.HasValue)
                    Assert.AreEqual(expected.Value, ex.CancellationToken, "OCE must carry the caller's token");
                return;
            }
            catch (Exception ex)
            {
                Assert.Fail("Expected OperationCanceledException, got " + ex.GetType().Name);
            }

            Assert.Fail("Expected OperationCanceledException, but the operation completed.");
        }

        internal sealed class ChannelUnderTest : IDisposable
        {
            private readonly SaeaSocketChannel _saea;
            private readonly PollSelectSocketChannel _poll;

            private ChannelUnderTest(SaeaSocketChannel saea, PollSelectSocketChannel poll)
            {
                _saea = saea;
                _poll = poll;
            }

            public static ChannelUnderTest Create(Socket socket, bool pollSelect)
            {
                return pollSelect
                    ? new ChannelUnderTest(null, new PollSelectSocketChannel(socket))
                    : new ChannelUnderTest(new SaeaSocketChannel(socket), null);
            }

            public void SetHook(Action hook)
            {
                if (_saea != null) _saea.TestHookBeforeRegister = hook;
                else _poll.TestHookBeforeRegister = hook;
            }

            public Task ReceiveAsync(byte[] buffer, CancellationToken ct)
            {
                return _saea != null
                    ? _saea.ReceiveAsync(buffer, 0, buffer.Length, ct).AsTask()
                    : _poll.ReceiveAsync(buffer, 0, buffer.Length, ct).AsTask();
            }

            public Task SendAsync(byte[] data, CancellationToken ct)
            {
                return _saea != null
                    ? _saea.SendAsync(data, ct).AsTask()
                    : _poll.SendAsync(data, ct).AsTask();
            }

            public void Dispose()
            {
                _saea?.Dispose();
                _poll?.Dispose();
            }
        }

        internal sealed class LoopbackPair : IDisposable
        {
            private readonly TcpListener _listener;

            private LoopbackPair(TcpListener listener, Socket client, Socket server)
            {
                _listener = listener;
                Client = client;
                Server = server;
            }

            public Socket Client { get; }
            public Socket Server { get; }

            public static async Task<LoopbackPair> CreateAsync(int bufferSize = 0)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var acceptTask = listener.AcceptSocketAsync();
                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                if (bufferSize > 0)
                {
                    client.SendBufferSize = bufferSize;
                    client.ReceiveBufferSize = bufferSize;
                }

                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                var server = await acceptTask;
                if (bufferSize > 0)
                {
                    server.SendBufferSize = bufferSize;
                    server.ReceiveBufferSize = bufferSize;
                }

                return new LoopbackPair(listener, client, server);
            }

            public void Dispose()
            {
                try { Client.Dispose(); } catch { }
                try { Server.Dispose(); } catch { }
                try { _listener.Stop(); } catch { }
            }
        }
    }
}
