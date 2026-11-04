using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace TurboHTTP.Cache
{
    /// <summary>
    /// Awaitable that always resumes the awaiting method on a thread-pool thread. Unlike
    /// <c>await Task.Yield()</c> it never captures the caller's <see cref="SynchronizationContext"/>
    /// or <see cref="System.Threading.Tasks.TaskScheduler"/>, so background cache work cannot be
    /// posted to (and then wait on) a context that is not pumping, such as Unity's main thread
    /// blocked in a synchronous wait or a backgrounded app (#245).
    /// </summary>
    internal readonly struct ThreadPoolHop : ICriticalNotifyCompletion
    {
        private static readonly WaitCallback s_runContinuation = static state => ((Action)state)();

        public ThreadPoolHop GetAwaiter() => this;

        /// <summary>Always <c>false</c>: the hop happens even when already on a pool thread.</summary>
        public bool IsCompleted => false;

        public void GetResult()
        {
        }

        public void OnCompleted(Action continuation)
        {
            if (continuation == null)
                throw new ArgumentNullException(nameof(continuation));
            ThreadPool.QueueUserWorkItem(s_runContinuation, continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            if (continuation == null)
                throw new ArgumentNullException(nameof(continuation));
            // The async method builder flows the ExecutionContext itself on this path.
            ThreadPool.UnsafeQueueUserWorkItem(s_runContinuation, continuation);
        }
    }
}
