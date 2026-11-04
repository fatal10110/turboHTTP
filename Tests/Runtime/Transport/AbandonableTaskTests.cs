using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// #172: a connect or handshake that ignores the token is abandoned on cancellation, and its
    /// later fault (the caller disposes the socket or stream) is observed instead of reaching
    /// <see cref="TaskScheduler.UnobservedTaskException"/>.
    /// </summary>
    [TestFixture]
    public class AbandonableTaskTests
    {
        [Test]
        public void WaitAsync_CancelledThenTaskFaults_FaultIsObserved()
        {
            using var probe = new UnobservedTaskProbe();
            var marker = new ObjectDisposedException("abandoned-connect");

            Exception failure = CancelThenFault(marker);
            probe.CollectWithControl();

            if (!probe.ControlReported)
            {
                TestRequirements.IgnoreEnvironment("This runtime did not report a known unobserved faulted task.");
                return;
            }

            Assert.IsInstanceOf<OperationCanceledException>(failure);
            Assert.IsFalse(probe.WasReported(marker),
                "The abandoned task faulted after cancellation and nobody observed it.");
        }

        [Test]
        public void WaitAsync_TaskCompletesFirst_Completes()
        {
            using var cts = new CancellationTokenSource();
            var source = new TaskCompletionSource<bool>();
            var wait = AbandonableTask.WaitAsync(source.Task, cts.Token);

            source.SetResult(true);

            Assert.IsNull(Capture(wait));
        }

        [Test]
        public void WaitAsync_TaskFaultsFirst_PropagatesFault()
        {
            using var cts = new CancellationTokenSource();
            var source = new TaskCompletionSource<bool>();
            var wait = AbandonableTask.WaitAsync(source.Task, cts.Token);
            var fault = new System.Net.Sockets.SocketException(10061);

            source.SetException(fault);

            Assert.AreSame(fault, Capture(wait));
        }

        [Test]
        public void WaitAsync_AlreadyCancelledToken_ThrowsWithoutWaitingForTask()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var source = new TaskCompletionSource<bool>();

            Assert.IsInstanceOf<OperationCanceledException>(
                Capture(AbandonableTask.WaitAsync(source.Task, cts.Token)));
            source.SetResult(true);
        }

        [Test]
        public void WaitAsync_NonCancellableToken_AwaitsTask()
        {
            var source = new TaskCompletionSource<bool>();
            var wait = AbandonableTask.WaitAsync(source.Task, CancellationToken.None);

            Assert.IsFalse(wait.IsCompleted);
            source.SetResult(true);
            Assert.IsNull(Capture(wait));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception CancelThenFault(Exception marker)
        {
            using var cts = new CancellationTokenSource();
            var source = new TaskCompletionSource<bool>();
            var wait = AbandonableTask.WaitAsync(source.Task, cts.Token);

            cts.Cancel();
            Exception failure = Capture(wait);

            // The caller's failure path disposes the socket; the pending connect then faults.
            source.SetException(marker);
            return failure;
        }

        private static Exception Capture(Task task)
        {
            try
            {
                task.GetAwaiter().GetResult();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }
}
