using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Tests.Transport;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #349: a loopback test server's <c>Dispose</c> never blocks without a bound. It closes the
    /// accepted sockets (a handler blocked in a read that ignores its token ends then) and waits
    /// for its tasks at most <see cref="BoundedShutdown.DefaultBound"/>, then abandons them with
    /// their faults observed. Dispose runs on the pool here so a regression fails the test instead
    /// of freezing the run.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketTestServerDisposeTests
    {
        private static readonly TimeSpan DisposeBound = TimeSpan.FromSeconds(10);

        [Test]
        public void WebSocketTestServer_HandlerWaitingForHandshake_DisposeReturns()
        {
            AssertAsync.Run(async () =>
            {
                var server = new WebSocketTestServer();
                using var client = await ConnectPartialHeadAsync(server.Port, "GET / HTTP/1.1\r\n").ConfigureAwait(false);
                await WaitForConnectionAsync(() => server.ConnectionCount).ConfigureAwait(false);

                await AssertDisposeReturnsAsync(server.Dispose).ConfigureAwait(false);
            });
        }

        [Test]
        public void WebSocketTestServer_HandlerThatNeverEnds_DisposeReturnsAfterTheBound()
        {
            var stall = new TaskCompletionSource<bool>();
            try
            {
                AssertAsync.Run(async () =>
                {
                    var server = new WebSocketTestServer(new WebSocketTestServerOptions { HandlerStall = stall.Task });
                    using var client = await ConnectPartialHeadAsync(server.Port, "GET / HTTP/1.1\r\n").ConfigureAwait(false);
                    await WaitForConnectionAsync(() => server.ConnectionCount).ConfigureAwait(false);

                    // Neither the token nor the closed socket ends this handler: only the bound does.
                    await AssertDisposeReturnsAsync(server.Dispose).ConfigureAwait(false);
                });
            }
            finally
            {
                stall.TrySetResult(true);
            }
        }

        [Test]
        public void WebSocketTestProxyServer_HandlerWaitingForConnectHead_DisposeReturns()
        {
            AssertAsync.Run(async () =>
            {
                var proxy = new WebSocketTestProxyServer();
                using var client = await ConnectPartialHeadAsync(proxy.Port, "CONNECT 127.0.0.1:1 HTTP/1.1\r\n").ConfigureAwait(false);
                await WaitForConnectionAsync(() => proxy.ConnectionCount).ConfigureAwait(false);

                await AssertDisposeReturnsAsync(proxy.Dispose).ConfigureAwait(false);
            });
        }

        [Test]
        public void WebSocketTestServer_DisposeTwice_IsANoOp()
        {
            var server = new WebSocketTestServer();
            server.Dispose();
            Assert.DoesNotThrow(server.Dispose);
        }

        [Test]
        public void BoundedShutdown_TaskThatNeverEnds_IsAbandonedAndItsLaterFaultObserved()
        {
            LogAssert.Expect(LogType.Warning, new Regex("abandoned \\(#349\\)"));
            using var probe = new UnobservedTaskProbe();
            var marker = new InvalidOperationException("abandoned-accept-loop");

            bool ended = AbandonThenFault(marker);
            probe.CollectWithControl();

            Assert.IsFalse(ended, "a task that never ends must be reported as abandoned");
            // Only a runtime that reports unobserved faults can show the observation (not iOS IL2CPP).
            if (probe.ControlReported)
                Assert.IsFalse(probe.WasReported(marker), "the abandoned task faulted later and nobody observed it");
        }

        // Not inlined, so the abandoned task is unreachable from the test frame when the probe collects.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool AbandonThenFault(Exception marker)
        {
            var source = new TaskCompletionSource<bool>();
            bool ended = BoundedShutdown.Wait(source.Task, TimeSpan.FromMilliseconds(50), nameof(WebSocketTestServerDisposeTests));
            source.SetException(marker);
            return ended;
        }

        private static async Task<TcpClient> ConnectPartialHeadAsync(int port, string partialHead)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                byte[] bytes = Encoding.ASCII.GetBytes(partialHead);
                await client.GetStream().WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        // Condition poll, bounded: the handler is reading once the accept loop counted the client.
        private static async Task WaitForConnectionAsync(Func<int> connectionCount)
        {
            for (int i = 0; i < 1000 && connectionCount() == 0; i++)
                await Task.Delay(10).ConfigureAwait(false);
            Assert.AreEqual(1, connectionCount(), "the server never accepted the connection");
        }

        private static async Task AssertDisposeReturnsAsync(Action dispose)
        {
            var disposing = Task.Run(dispose);
            var first = await Task.WhenAny(disposing, Task.Delay(DisposeBound)).ConfigureAwait(false);
            Assert.AreSame(disposing, first, "Dispose did not return within " + DisposeBound.TotalSeconds + " s while a client held its connection open");
            await disposing.ConfigureAwait(false);
        }
    }
}
