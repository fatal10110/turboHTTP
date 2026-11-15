using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #1135: Abort or Dispose while the transport is still connecting cancels the transport's token,
    /// disposes the stream the transport hands over afterwards, and fails ConnectAsync with a closed or
    /// cancellation error instead of the Connecting-to-Open transition failure.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketAbortDuringConnectTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/abort-during-connect");

        // Guard only: every await finishes long before this, it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        public enum Closer
        {
            ConnectionAbort,
            ConnectionDispose,
            ClientAbort
        }

        [TestCase(Closer.ConnectionAbort)]
        [TestCase(Closer.ConnectionDispose)]
        [TestCase(Closer.ClientAbort)]
        public void CloseWhileTransportConnects_CancelsConnectAndDisposesLateStream(Closer closer)
        {
            AssertAsync.Run(async () =>
            {
                ControlledClientStream clientStream = null;
                using var peer = new InMemoryWebSocketPeer(inner => clientStream = new ControlledClientStream(inner));
                using var transport = new DeferredTransport();
                var options = WebSocketReceiveBackpressureTests.CreateOptions(capacity: 8);

                var connection = new WebSocketConnection();
                var client = new WebSocketClient(transport);
                try
                {
                    Task connect = closer == Closer.ClientAbort
                        ? client.ConnectAsync(TestUri, options, CancellationToken.None)
                        : connection.ConnectAsync(TestUri, transport, options, CancellationToken.None);

                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(transport.Started, Guard,
                        "The transport connect did not start.").ConfigureAwait(false);
                    CancellationToken transportToken = transport.Started.Result;

                    switch (closer)
                    {
                        case Closer.ConnectionAbort:
                            connection.Abort();
                            break;
                        case Closer.ConnectionDispose:
                            connection.Dispose();
                            break;
                        default:
                            client.Abort();
                            break;
                    }

                    Assert.IsTrue(transportToken.IsCancellationRequested,
                        "The close must cancel the token the transport connects with.");

                    // The transport ignored the token and completes anyway, with a peer that would answer
                    // the upgrade: the connection must not open on it, nor leak it.
                    var lateStream = await peer.ConnectAsync(TestUri, options, CancellationToken.None)
                        .ConfigureAwait(false);
                    transport.Complete(lateStream);

                    Exception failure = null;
                    try
                    {
                        await WebSocketReceiveBackpressureTests.WaitWithTimeout(connect, Guard,
                            "ConnectAsync did not finish after the close.").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }

                    Assert.IsNotNull(failure, "ConnectAsync must fail once the connection was closed.");
                    StringAssert.DoesNotContain("transition", failure.Message);
                    if (failure is WebSocketException wsFailure)
                        Assert.AreEqual(WebSocketError.ConnectionClosed, wsFailure.Error);
                    else
                        Assert.IsInstanceOf<OperationCanceledException>(failure);

                    Assert.IsNotNull(clientStream);
                    Assert.IsTrue(clientStream.IsDisposed, "The stream handed over after the close must be disposed.");
                    Assert.AreNotEqual(WebSocketState.Open, closer == Closer.ClientAbort ? client.State : connection.State);
                }
                finally
                {
                    connection.Dispose();
                    client.Dispose();
                }
            });
        }

        /// <summary>Hands out the stream the test supplies, ignoring the token like a stalled socket connect.</summary>
        private sealed class DeferredTransport : IWebSocketTransport
        {
            private readonly TaskCompletionSource<CancellationToken> _started =
                new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<Stream> _result =
                new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<CancellationToken> Started => _started.Task;

            public Task<Stream> ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct)
            {
                _started.TrySetResult(ct);
                return _result.Task;
            }

            public void Complete(Stream stream)
            {
                _result.TrySetResult(stream);
            }

            public void Dispose()
            {
                _result.TrySetCanceled();
            }
        }
    }
}
