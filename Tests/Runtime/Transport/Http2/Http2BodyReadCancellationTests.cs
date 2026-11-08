using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// Phase 22a.6 Step 1, HTTP/2 item 24 (#102): cancelling a pending HTTP/2 body read only cancels that read.
    /// The bounded per-stream queue keeps its state, so data that arrives afterwards is read intact and in
    /// order, and the body still ends normally (unlike HTTP/1.1, where a cancelled read poisons the connection).
    /// </summary>
    [TestFixture]
    public sealed class Http2BodyReadCancellationTests
    {
        [Test]
        public void PendingReadCancelled_LaterDataIsReadIntact_AndTheBodyEndsNormally()
        {
            AssertAsync.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://cancel.local/h2"));
                var connection = new Http2Connection(Stream.Null, "cancel.local", 443, new Http2Options(), new StreamingOptions());
                var stream = new Http2Stream();
                stream.Initialize(1, request, NoOpHttpHandler.Instance, new RequestContext(request), 65535, 65535, connection);
                var source = new Http2ResponseBodySource(connection, stream, 6, completed: false);
                try
                {
                    var buffer = new byte[8];
                    using (var cts = new CancellationTokenSource())
                    {
                        var pending = source.ReadAsync(buffer, cts.Token).AsTask();
                        Assert.IsFalse(pending.IsCompleted, "a read on an empty body queue must wait");
                        cts.Cancel();
                        AssertAsync.ThrowsAsync<OperationCanceledException>(async () => await pending);
                    }

                    Assert.AreEqual(
                        Http2ResponseBodyEnqueueResult.Accepted,
                        source.TryEnqueueData(new byte[] { 1, 2, 3 }, 0, 3, flowControlledLength: 0));
                    Assert.AreEqual(
                        Http2ResponseBodyEnqueueResult.Accepted,
                        source.TryEnqueueData(new byte[] { 4, 5, 6 }, 0, 3, flowControlledLength: 0));
                    source.Complete();

                    var total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(total), CancellationToken.None)) > 0)
                        total += read;

                    Assert.AreEqual(6, total);
                    CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6 }, new ArraySegment<byte>(buffer, 0, 6));
                }
                finally
                {
                    await source.DisposeAsync();
                    request.Dispose();
                    connection.Dispose();
                }
            });
        }
    }
}
