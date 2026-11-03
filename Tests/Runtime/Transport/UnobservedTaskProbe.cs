using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// Records <see cref="TaskScheduler.UnobservedTaskException"/> reports of marker exceptions
    /// (#172). A control task faulted with <see cref="ControlMarker"/> and never observed proves
    /// that this runtime reports unobserved tasks at all; without that report a "nothing was
    /// reported" assertion would be vacuous.
    /// </summary>
    internal sealed class UnobservedTaskProbe : IDisposable
    {
        private readonly ConcurrentQueue<Exception> _reported = new ConcurrentQueue<Exception>();
        private readonly EventHandler<UnobservedTaskExceptionEventArgs> _handler;

        public UnobservedTaskProbe()
        {
            ControlMarker = new InvalidOperationException("unobserved-task-probe control");
            _handler = (_, e) =>
            {
                foreach (var inner in e.Exception.InnerExceptions)
                    _reported.Enqueue(inner);
            };
            TaskScheduler.UnobservedTaskException += _handler;
        }

        public Exception ControlMarker { get; }

        /// <summary>Faults a task nobody observes, then collects until the finalizers ran.</summary>
        public void CollectWithControl()
        {
            FaultUnobservedControl(ControlMarker);
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        public bool ControlReported => WasReported(ControlMarker);

        public bool WasReported(Exception marker)
        {
            foreach (var reported in _reported)
            {
                if (ReferenceEquals(reported, marker))
                    return true;
            }

            return false;
        }

        public void Dispose()
        {
            TaskScheduler.UnobservedTaskException -= _handler;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FaultUnobservedControl(Exception marker)
        {
            var source = new TaskCompletionSource<bool>();
            source.SetException(marker);
        }
    }
}
