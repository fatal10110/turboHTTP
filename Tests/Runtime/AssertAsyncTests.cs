using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace TurboHTTP.Tests
{
    [TestFixture]
    public class AssertAsyncTests
    {
        [Test]
        [Timeout(2000)]
        public void ThrowsAsync_TaskDelegate_DoesNotDeadlockCapturedSynchronizationContext()
        {
            var priorContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());

                var ex = AssertAsync.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("task boom");
                });

                Assert.That(ex.Message, Is.EqualTo("task boom"));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(priorContext);
            }
        }

        [Test]
        [Timeout(2000)]
        public void ThrowsAsync_ValueTaskDelegate_DoesNotDeadlockCapturedSynchronizationContext()
        {
            var priorContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());

                var ex = AssertAsync.ThrowsAsync<InvalidOperationException>(
                    new Func<ValueTask>(async () =>
                    {
                        await Task.Yield();
                        throw new InvalidOperationException("valuetask boom");
                    }),
                    preferValueTask: true);

                Assert.That(ex.Message, Is.EqualTo("valuetask boom"));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(priorContext);
            }
        }

        private sealed class NonPumpingSynchronizationContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object state)
            {
                _ = d;
                _ = state;
            }

            public override void Send(SendOrPostCallback d, object state)
            {
                _ = d;
                _ = state;
            }
        }
    }
}
