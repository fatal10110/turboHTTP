using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// The enumerator returned by the <see cref="CoroutineWrapper"/> methods. It owns the lifecycle
    /// binding, the request task and a result that was not delivered, so cleanup does not depend on an
    /// iterator <c>finally</c> block: Unity never disposes a coroutine enumerator that it stops
    /// (<c>StopCoroutine</c>, <c>StopAllCoroutines</c>, host deactivated or destroyed; #956, #177).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The task result has exactly one owner, chosen by the <c>_terminal</c> compare-exchange.
    /// <see cref="MoveNext"/> (main thread) takes it once the task has completed and hands it to
    /// <c>onSuccess</c>, or releases it when callbacks are suppressed or <c>onSuccess</c> is null.
    /// <see cref="TryRelease"/> (any thread) takes it once the task has completed and the operation was
    /// abandoned: disposed, or its binding canceled (caller token, destroyed owner, inactive owner with
    /// <c>cancelOnOwnerInactive</c>). The task continuation and the binding-token registration both call
    /// <see cref="TryRelease"/>, so whichever of "task completed" and "abandoned" happens last runs it.
    /// The binding is disposed by the same owner, after the task completed.
    /// </para>
    /// <para>
    /// A coroutine stopped without one of those signals (owner-less, owner alive) cannot be detected:
    /// the caller disposes the enumerator, which cancels the request.
    /// </para>
    /// </remarks>
    internal sealed class CoroutineOperation<T> : IEnumerator, IDisposable
    {
        private readonly Func<CancellationToken, Task<T>> _start;
        private readonly Action<T> _releaseResult;
        private readonly Action<T> _onSuccess;
        private readonly Action<Exception> _onError;
        private readonly CancellationToken _cancellationToken;
        private readonly UnityEngine.Object _callbackOwner;
        private readonly bool _cancelOnOwnerInactive;

        // Main thread only (MoveNext).
        private bool _started;
        private bool _finished;

        // Published by MoveNext; read by Dispose and TryRelease on any thread.
        private LifecycleCancellationBinding _binding;
        private Task<T> _task;

        // 0 until one owner takes the result (MoveNext or TryRelease).
        private int _terminal;
        private int _disposed;

        internal CoroutineOperation(
            Func<CancellationToken, Task<T>> start,
            Action<T> releaseResult,
            Action<T> onSuccess,
            Action<Exception> onError,
            CancellationToken cancellationToken,
            UnityEngine.Object callbackOwner,
            bool cancelOnOwnerInactive)
        {
            _start = start ?? throw new ArgumentNullException(nameof(start));
            _releaseResult = releaseResult;
            _onSuccess = onSuccess;
            _onError = onError;
            _cancellationToken = cancellationToken;
            _callbackOwner = callbackOwner;
            _cancelOnOwnerInactive = cancelOnOwnerInactive;
        }

        /// <summary>Always <see langword="null"/>: the coroutine waits one frame per step.</summary>
        public object Current => null;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>
        /// Starts the request on the first call, then returns <see langword="true"/> while it runs and
        /// delivers the result once it completed.
        /// </summary>
        public bool MoveNext()
        {
            if (_finished)
                return false;

            if (!_started)
            {
                _started = true;
                if (!TryStart())
                {
                    _finished = true;
                    return false;
                }
            }

            var binding = _binding;
            var task = _task;
            if (!task.IsCompleted)
            {
                if (IsDisposed)
                    return Finish();

                if (CoroutineWrapper.IsCallbackSuppressed(_callbackOwner, binding))
                {
                    // The owner died before LifecycleCancellation.Poll saw it: stop the request now.
                    binding.Cancel(LifecycleCancellationReason.OwnerDestroyed);
                    return Finish();
                }

                return true;
            }

            _finished = true;
            if (Interlocked.CompareExchange(ref _terminal, 1, 0) != 0)
                return false;

            try
            {
                Complete(task, binding);
            }
            finally
            {
                binding.Dispose();
            }

            return false;
        }

        /// <summary>Not supported.</summary>
        public void Reset()
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Stops the operation: cancels a running request and disposes a result that was not delivered.
        /// Idempotent; may be called from any thread.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            var binding = Volatile.Read(ref _binding);
            if (binding == null)
                return;

            try
            {
                // Runs the binding-token registration, which calls TryRelease.
                binding.Cancel(LifecycleCancellationReason.ExplicitToken);
            }
            catch (AggregateException)
            {
                // A request cancellation callback threw; the binding is canceled regardless.
            }

            // The binding was canceled earlier (Cancel did nothing) and the task may already be done.
            TryRelease();
        }

        private bool TryStart()
        {
            var binding = LifecycleCancellation.Bind(
                _callbackOwner,
                _cancellationToken,
                _cancelOnOwnerInactive);

            // Full fence paired with Dispose: one of them sees the other.
            Interlocked.Exchange(ref _binding, binding);
            if (IsDisposed)
            {
                binding.Dispose();
                return false;
            }

            // The binding is not disposed before _task is published: only the result owner disposes it.
            var token = binding.Token;
            Task<T> task;
            try
            {
                task = _start(token);
            }
            catch (Exception ex)
            {
                try
                {
                    CoroutineWrapper.InvokeErrorIfEligible(_onError, ex, _callbackOwner, binding);
                }
                finally
                {
                    binding.Dispose();
                }

                return false;
            }

            // Runs synchronously if the token is already canceled; TryRelease then sees no task and the
            // continuation below takes over.
            _ = token.Register(static state => ((CoroutineOperation<T>)state).TryRelease(), this);
            Volatile.Write(ref _task, task);
            _ = task.ContinueWith(
                static (_, state) => ((CoroutineOperation<T>)state).TryRelease(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return true;
        }

        private void Complete(Task<T> task, LifecycleCancellationBinding binding)
        {
            if (task.IsCanceled || CoroutineWrapper.IsCallbackSuppressed(_callbackOwner, binding))
            {
                ReleaseResult(task);
                return;
            }

            if (task.IsFaulted)
            {
                CoroutineWrapper.InvokeErrorIfEligible(
                    _onError,
                    CoroutineWrapper.UnwrapTaskException(task.Exception),
                    _callbackOwner,
                    binding);
                return;
            }

            var result = CompletedResult(task);
            if (_onSuccess == null)
            {
                _releaseResult?.Invoke(result);
                return;
            }

            // The callback owns the result from here on.
            try
            {
                _onSuccess(result);
            }
            catch (Exception)
            {
                // Unchanged behavior: a throwing onSuccess is not routed to onError (#955).
            }
        }

        private void TryRelease()
        {
            var task = Volatile.Read(ref _task);
            if (task == null || !task.IsCompleted)
                return;

            var binding = Volatile.Read(ref _binding);
            if (!IsDisposed && !binding.IsCancellationRequested)
                return; // The running coroutine still delivers it.

            if (Interlocked.CompareExchange(ref _terminal, 1, 0) != 0)
                return;

            try
            {
                ReleaseResult(task);
            }
            finally
            {
                binding.Dispose();
            }
        }

        private void ReleaseResult(Task<T> task)
        {
            if (task.Status == TaskStatus.RanToCompletion)
            {
                _releaseResult?.Invoke(CompletedResult(task));
            }
            else if (task.IsFaulted)
            {
                // Observed, so an abandoned failure is not reported as an unobserved task exception.
                _ = task.Exception;
            }
        }

        // Only called once the task ran to completion, so reading Result never blocks.
        private static T CompletedResult(Task<T> task) => task.Result;

        private bool Finish()
        {
            _finished = true;
            Dispose();
            return false;
        }
    }
}
