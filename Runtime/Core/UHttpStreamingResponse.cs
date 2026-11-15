using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Core
{
    public sealed class UHttpStreamingResponse : IAsyncDisposable, IDisposable
    {
        private readonly IResponseBodySource _bodySource;
        private readonly ResponseBodyStream _body;
        private readonly object _disposeCallbackGate = new object();
        private Action _onDispose;
        private int _disposed;
#if DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
        private static int s_undisposedFinalizedCount;
#endif

        public UHttpStreamingResponse(
            HttpStatusCode statusCode,
            HttpHeaders headers,
            IResponseBodySource bodySource)
        {
            StatusCode = statusCode;
            Headers = headers ?? new HttpHeaders();
            _bodySource = bodySource ?? throw new ArgumentNullException(nameof(bodySource));
            _body = new ResponseBodyStream(this);
        }

        public HttpStatusCode StatusCode { get; }

        public HttpHeaders Headers { get; }

        public ResponseBodyStream Body => _body;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        internal long? BodyLength => _bodySource.Length;

        internal IResponseBodySource BodySourceForTesting => _bodySource;

        /// <summary>
        /// Number of responses whose finalizer ran without a prior dispose since start-up (debug,
        /// editor and development builds only; always 0 otherwise). Lets tests attribute a dropped
        /// response to the test that dropped it (#366).
        /// </summary>
        internal static int UndisposedFinalizedCountForTesting
        {
            get
            {
#if DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
                return Volatile.Read(ref s_undisposedFinalizedCount);
#else
                return 0;
#endif
            }
        }

        internal ValueTask<int> ReadBodyAsync(Memory<byte> destination, CancellationToken ct)
        {
            return _bodySource.ReadAsync(destination, ct);
        }

        /// <summary>
        /// Returns HTTP trailers after the response body has reached completion.
        /// Calling this before EOF may wait for the remaining body to be consumed or for transport
        /// cleanup to complete; on HTTP/1.1 responses without trailer support this returns
        /// <see cref="HttpHeaders.Empty"/>.
        /// </summary>
        public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return _bodySource.GetTrailersAsync(ct);
        }

        /// <summary>
        /// Backstop for a response dropped without <see cref="Dispose"/> or <see cref="DisposeAsync"/>
        /// (both suppress finalization): its body source is aborted so the transport releases what the
        /// body holds (#583), for example the HTTP/1.1 connection lease and its per-host permit.
        /// </summary>
        /// <remarks>
        /// <see cref="IResponseBodySource.Abort"/> is not called here: a finalizer never runs transport or
        /// pool callbacks (deadlock risk on the finalizer thread). It queues a static thread-pool work item
        /// that captures only the body source. The dispose callbacks (request hold, request context) are
        /// not run; what they release is left to the garbage collector, as for a dropped
        /// <see cref="UHttpResponse"/>.
        /// </remarks>
        ~UHttpStreamingResponse()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

#if DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
            Interlocked.Increment(ref s_undisposedFinalizedCount);
            TurboDiagnostics.Log(
                "[TurboHTTP] UHttpStreamingResponse was not disposed. Response bodies must be disposed to release transport resources.");
#endif

            QueueDroppedBodyAbort(_bodySource);
        }

        private static readonly WaitCallback s_abortDroppedBody = AbortDroppedBody;

        private static void QueueDroppedBodyAbort(IResponseBodySource bodySource)
        {
            // During domain unload or process exit the transport is going away with everything else.
            if (bodySource == null || Environment.HasShutdownStarted)
                return;

            try
            {
                ThreadPool.UnsafeQueueUserWorkItem(s_abortDroppedBody, bodySource);
            }
            catch (Exception)
            {
                // A finalizer must not throw; with no thread pool left the process is shutting down.
            }
        }

        private static void AbortDroppedBody(object state)
        {
            try
            {
                ((IResponseBodySource)state).Abort();
            }
            catch (Exception ex)
            {
                TurboDiagnostics.Log("[TurboHTTP] Aborting the body of an undisposed UHttpStreamingResponse threw.", ex);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            GC.SuppressFinalize(this);

            Task pendingBodyDispose = null;
            try
            {
                pendingBodyDispose = StartBodyDispose();
            }
            finally
            {
                if (pendingBodyDispose == null)
                    InvokeDisposeCallbacks();
            }

            if (pendingBodyDispose != null)
                CompleteDisposeWhenBodyDisposed(pendingBodyDispose);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return default;

            GC.SuppressFinalize(this);
            return DisposeAsyncCore();
        }

        internal void AttachRequestRelease(Action releaseAction)
        {
            if (releaseAction == null)
                return;

            var releaseImmediately = false;
            lock (_disposeCallbackGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    releaseImmediately = true;
                }
                else
                {
                    _onDispose = (Action)Delegate.Combine(_onDispose, releaseAction);
                }
            }

            if (releaseImmediately)
                releaseAction();
        }

        internal void AbortBody()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _bodySource.Abort();
        }

        /// <summary>
        /// Starts the synchronous dispose of the body source without waiting for it. Returns
        /// <c>null</c> when the source is finished (aborted, disposed, or failed and aborted), or the
        /// still-running dispose of a fully read body.
        /// </summary>
        private Task StartBodyDispose()
        {
            if (!_body.HasReachedEndOfStream)
            {
                _bodySource.Abort();
                return null;
            }

            Task disposeTask;
            try
            {
                // AsTask consumes the ValueTask; on an already completed one it does not allocate.
                disposeTask = _bodySource.DisposeAsync().AsTask();
            }
            catch
            {
                _bodySource.Abort();
                return null;
            }

            if (!disposeTask.IsCompleted)
                return disposeTask;

            if (disposeTask.Status != TaskStatus.RanToCompletion)
                AbortAfterFailedBodyDispose(disposeTask);
            return null;
        }

        /// <summary>
        /// Sync <see cref="Dispose"/> never blocks its caller (often Unity's main thread) on a body
        /// dispose that is still running (#181). This continuation owns the rest of the dispose: it
        /// aborts the source if the dispose failed, then releases the request, the same order
        /// <see cref="DisposeAsync"/> uses after its await.
        /// </summary>
        private void CompleteDisposeWhenBodyDisposed(Task bodyDispose)
        {
            _ = bodyDispose.ContinueWith(
                static (task, state) =>
                {
                    var response = (UHttpStreamingResponse)state;
                    try
                    {
                        if (task.Status != TaskStatus.RanToCompletion)
                            response.AbortAfterFailedBodyDispose(task);
                    }
                    catch (Exception ex)
                    {
                        TurboDiagnostics.Log("[TurboHTTP] Aborting a response body after its dispose failed threw.", ex);
                    }

                    try
                    {
                        response.InvokeDisposeCallbacks();
                    }
                    catch (Exception ex)
                    {
                        TurboDiagnostics.Log("[TurboHTTP] A response dispose callback threw.", ex);
                    }
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void AbortAfterFailedBodyDispose(Task failedDispose)
        {
            // Reading Exception marks the fault observed, so it never reaches UnobservedTaskException.
            _ = failedDispose.Exception;
            _bodySource.Abort();
        }

        private async ValueTask DisposeAsyncCore()
        {
            try
            {
                await _bodySource.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                InvokeDisposeCallbacks();
            }
        }

        private void InvokeDisposeCallbacks()
        {
            Action callbacks;
            lock (_disposeCallbackGate)
            {
                callbacks = _onDispose;
                _onDispose = null;
            }

            callbacks?.Invoke();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(UHttpStreamingResponse));
        }
    }
}
