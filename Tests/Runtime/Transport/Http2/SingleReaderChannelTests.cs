using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    [TestFixture]
    public class SingleReaderChannelTests
    {
        [Test]
        public async Task CancelPendingRead_PreservesTheOriginalCancellationToken()
        {
            var channel = new SingleReaderChannel<int>(capacity: 1);
            using var cts = new CancellationTokenSource();

            var pendingRead = channel.ReadAsync(cts.Token).AsTask();
            cts.Cancel();

            var ex = Assert.ThrowsAsync<OperationCanceledException>(async () => await pendingRead);
            Assert.AreEqual(cts.Token, ex.CancellationToken);
        }

        [Test]
        public async Task Complete_IgnoresSecondFaultAfterTerminalCompletion()
        {
            var channel = new SingleReaderChannel<int>(capacity: 1);
            var pendingRead = channel.ReadAsync().AsTask();
            var first = new InvalidOperationException("first");
            var second = new InvalidOperationException("second");

            channel.Complete(first);
            channel.Complete(second);

            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await pendingRead);
            Assert.AreSame(first, ex);
        }
    }
}
