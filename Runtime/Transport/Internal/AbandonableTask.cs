using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Transport.Internal
{
    /// <summary>
    /// Transport name for <see cref="AbandonOnCancel"/> (the implementation moved to Core so the
    /// WebSocket handshake reads can use it, #345): awaits an operation that does not honor its
    /// token and abandons it when the token fires, observing its later fault.
    /// </summary>
    internal static class AbandonableTask
    {
        /// <inheritdoc cref="AbandonOnCancel.WaitAsync(Task, CancellationToken, IDisposable)"/>
        internal static Task WaitAsync(Task task, CancellationToken ct, IDisposable abandonedResource = null) =>
            AbandonOnCancel.WaitAsync(task, ct, abandonedResource);

        /// <inheritdoc cref="AbandonOnCancel.WaitAsync{T}(Task{T}, CancellationToken, IDisposable)"/>
        internal static Task<T> WaitAsync<T>(Task<T> task, CancellationToken ct, IDisposable abandonedResource = null) =>
            AbandonOnCancel.WaitAsync(task, ct, abandonedResource);

        /// <inheritdoc cref="AbandonOnCancel.Observe(Task)"/>
        internal static void Observe(Task task) => AbandonOnCancel.Observe(task);

        /// <inheritdoc cref="AbandonOnCancel.Observe(Task, IDisposable)"/>
        internal static void Observe(Task task, IDisposable abandonedResource) =>
            AbandonOnCancel.Observe(task, abandonedResource);
    }
}
