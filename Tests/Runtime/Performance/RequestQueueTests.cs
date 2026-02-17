using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Performance;

namespace TurboHTTP.Tests.Performance
{
    public class RequestQueueTests
    {
        [Test]
        public void TryDequeue_WhenNoItemFound_RestoresSemaphoreSignal()
        {
            using var queue = new RequestQueue<string>();
            queue.Enqueue("item");

            var queuesField = typeof(RequestQueue<string>).GetField(
                "_queues", BindingFlags.NonPublic | BindingFlags.Instance);
            var queues = (ConcurrentQueue<string>[])queuesField.GetValue(queue);
            Assert.IsTrue(queues[(int)RequestPriority.Normal].TryDequeue(out _));

            var semaphoreField = typeof(RequestQueue<string>).GetField(
                "_itemAvailable", BindingFlags.NonPublic | BindingFlags.Instance);
            var semaphore = (SemaphoreSlim)semaphoreField.GetValue(queue);
            Assert.AreEqual(1, semaphore.CurrentCount);

            var dequeued = queue.TryDequeue(out var item);

            Assert.IsFalse(dequeued);
            Assert.IsNull(item);
            Assert.AreEqual(1, semaphore.CurrentCount);
        }
    }
}
