using System;
using System.Net.Sockets;
using System.Threading.Tasks.Sources;

namespace TurboHTTP.Transport.Tcp
{
    /// <summary>
    /// Bridges <see cref="SocketAsyncEventArgs.Completed"/> to a reusable
    /// <see cref="System.Threading.Tasks.ValueTask{T}"/> via the <see cref="IValueTaskSource{T}"/>
    /// pattern. One instance is created per SAEA object and reused across operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usage pattern per operation:
    /// <list type="number">
    ///   <item>Call <see cref="Reset"/> to prepare for the next operation.</item>
    ///   <item>Issue the socket call (e.g. <c>Socket.ReceiveAsync(saea)</c>).</item>
    ///   <item>If the call returns <c>false</c> (synchronous completion), call
    ///         <see cref="SetResult"/> directly — the <c>Completed</c> event is NOT raised
    ///         for synchronous completions (BCL contract), so there is no double-SetResult risk.</item>
    ///   <item>Otherwise await <see cref="AsValueTask"/>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Thread safety:</b> <see cref="ManualResetValueTaskSourceCore{T}"/> is designed for the
    /// producer/consumer pattern used here and is safe when the completion callback and the
    /// awaiter run on different threads.
    /// </para>
    /// <para>
    /// <b>Cancellation:</b> Handled at the call site by disposing the socket, which triggers
    /// <see cref="SocketError.OperationAborted"/>. <see cref="SaeaSocketChannel"/> is responsible
    /// for mapping that error to <see cref="OperationCanceledException"/>.
    /// </para>
    /// </remarks>
    internal sealed class SaeaCompletionSource : IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> _core;

        /// <summary>
        /// Prepares the source for a new operation. Must be called before each socket call.
        /// </summary>
        public void Reset() => _core.Reset();

        /// <summary>
        /// Returns a <see cref="System.Threading.Tasks.ValueTask{T}"/> that completes when
        /// <see cref="SetResult"/> or <see cref="SetException"/> is called.
        /// </summary>
        public System.Threading.Tasks.ValueTask<int> AsValueTask()
            => new System.Threading.Tasks.ValueTask<int>(this, _core.Version);

        /// <summary>Signals successful completion with the number of bytes transferred.</summary>
        public void SetResult(int bytesTransferred) => _core.SetResult(bytesTransferred);

        /// <summary>Signals faulted completion.</summary>
        public void SetException(Exception exception) => _core.SetException(exception);

        // ── IValueTaskSource<int> ──────────────────────────────────────────────

        int IValueTaskSource<int>.GetResult(short token) => _core.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _core.GetStatus(token);
        void IValueTaskSource<int>.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }
}
