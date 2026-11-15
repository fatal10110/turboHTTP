namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// A pooled response-body owner that can give up its buffers without returning them to the pool.
    /// </summary>
    /// <remarks>
    /// The <see cref="UHttpResponse"/> finalizer uses this instead of <see cref="System.IDisposable.Dispose"/>
    /// (#582): <see cref="UHttpResponse.Body"/> is a <see cref="System.Buffers.ReadOnlySequence{T}"/> that does
    /// not keep the response alive, so a caller may still read the buffers after the response has been
    /// finalized. Returning them would let another rent overwrite those bytes. An implementation counts every
    /// array it rented from <see cref="ByteArrayPool"/> with <see cref="ByteArrayPool.Abandon"/> (so the #292
    /// leak counter stays exact), drops its own references and leaves the arrays to the garbage collector.
    /// It takes no locks and invokes no callbacks, so it is safe on the finalizer thread. After it, a
    /// <see cref="System.IDisposable.Dispose"/> call is a no-op.
    /// </remarks>
    internal interface IAbandonableBufferOwner
    {
        /// <summary>
        /// Counts the owned pooled arrays as abandoned and leaves them to the garbage collector.
        /// Idempotent, and a no-op after <see cref="System.IDisposable.Dispose"/>.
        /// </summary>
        void AbandonToGc();
    }
}
