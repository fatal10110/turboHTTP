using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Http1
{
    /// <summary>
    /// Request-side counterpart of the head and body read races (#293, #345): HTTP/1.1 request writes
    /// do not trust the platform to end a pending send once the token fired (#633). Mono and IL2CPP
    /// may not end a pending socket operation after its token fired and its socket was shut down and
    /// disposed (#384 measured reads doing so on iOS), and a stalled upload would then hold the lease,
    /// the per-host permit and the pooled send buffer past its timeout. A write that completes
    /// synchronously, or whose token cannot fire, is awaited as is (no allocation); a pending one is
    /// raced against the token and abandoned when the token wins. The abandoned write is observed,
    /// tracked in <see cref="AbandonedSocketIo"/> and disposes the stream once it ends, and the buffer
    /// it still reads stays out of the pool (<see cref="ReleaseBuffer"/>). The caller's own
    /// cancellation path disposes the connection, so an abandoned write never shares a pooled
    /// connection with a later request.
    /// </summary>
    internal static class Http11RequestWriteRace
    {
        /// <summary>
        /// Writes <paramref name="data"/> to <paramref name="stream"/>, or throws
        /// <see cref="OperationCanceledException"/> as soon as <paramref name="ct"/> fires while the
        /// write is pending.
        /// </summary>
        /// <param name="pendingWrite">
        /// The underlying write when it was raced, else null. After a failure it tells the buffer
        /// owner whether the write may still read the buffer (<see cref="IsStillRunning"/>).
        /// </param>
        internal static ValueTask WriteAsync(
            Stream stream,
            ReadOnlyMemory<byte> data,
            CancellationToken ct,
            out Task pendingWrite)
        {
            var write = stream.WriteAsync(data, ct);
            if (write.IsCompleted || !ct.CanBeCanceled)
            {
                pendingWrite = null;
                return write;
            }

            pendingWrite = write.AsTask();
            return new ValueTask(AbandonableTask.WaitAsync(pendingWrite, ct, stream));
        }

        /// <summary>
        /// Flushes <paramref name="stream"/>, abandoning a pending flush like
        /// <see cref="WriteAsync"/> abandons a pending write.
        /// </summary>
        internal static ValueTask FlushAsync(Stream stream, CancellationToken ct)
        {
            var flush = stream.FlushAsync(ct);
            if (flush.IsCompleted || !ct.CanBeCanceled)
                return new ValueTask(flush);

            return new ValueTask(AbandonableTask.WaitAsync(flush, ct, stream));
        }

        /// <summary>
        /// True when <paramref name="pendingWrite"/> was abandoned and has not ended yet: the platform
        /// may still read the buffer it was given.
        /// </summary>
        internal static bool IsStillRunning(Task pendingWrite) =>
            pendingWrite != null && !pendingWrite.IsCompleted;

        /// <summary>
        /// Returns a rented send buffer to the pool, unless the last write over it was abandoned and
        /// is still running: then the buffer is left to the GC (<see cref="ByteArrayPool.Abandon"/>)
        /// so the next renter never shares it with a send the platform has not ended.
        /// </summary>
        internal static void ReleaseBuffer(byte[] buffer, Task pendingWrite)
        {
            if (IsStillRunning(pendingWrite))
                ByteArrayPool.Abandon(buffer);
            else
                ByteArrayPool.Return(buffer);
        }
    }
}
