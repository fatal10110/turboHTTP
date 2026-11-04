using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls;

namespace TurboHTTP.Transport.BouncyCastle
{
    /// <summary>
    /// Full-duplex TLS stream over a non-blocking BouncyCastle <see cref="TlsClientProtocol"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// BouncyCastle's own <c>TlsStream</c> compiles no async overrides under Unity's scripting
    /// defines, so every async call falls back to the BCL <see cref="Stream"/> APM path, which
    /// serialises reads and writes on one semaphore: a pending read blocks every write (#36) and
    /// parks a pool thread inside a blocking read. This stream drives the protocol engine in
    /// non-blocking mode instead and performs all socket I/O itself through the inner stream's
    /// async API, so the handshake and every read/write run without blocking a thread (#169).
    /// </para>
    /// <para>
    /// Ownership and concurrency:
    /// <list type="bullet">
    /// <item><c>_protocolLock</c> guards every call into <see cref="TlsClientProtocol"/>. Each call
    /// is CPU-only in non-blocking mode (record encryption/decryption, buffer moves); no I/O and no
    /// <c>await</c> happens while it is held.</item>
    /// <item><c>_writeGate</c> serialises ciphertext output on the inner stream so TLS records
    /// leave in the order the engine produced them. Whoever holds it drains the engine's output
    /// queue, including records produced by the read path (alerts, key updates); see
    /// <see cref="FlushPendingOutputAsync"/>.</item>
    /// <item>The read path has a single owner at a time (<c>_readInProgress</c>); it never takes
    /// <c>_writeGate</c> while waiting, so a stalled write can never block a read.</item>
    /// </list>
    /// </para>
    /// </remarks>
    internal sealed class BouncyCastleTlsStream : Stream
    {
        // One TLS record of plaintext (RFC 8446 section 5.1: 2^14 bytes).
        private const int PlaintextChunkSize = 16 * 1024;

        // Ciphertext staging for inner reads/writes. Any size works (the engine buffers partial
        // records); one maximum record (2^14 + 2048 + header) keeps a full record per I/O call.
        private const int CiphertextBufferSize = 16 * 1024 + 2048 + 5;

        // Bound on records the read path leaves queued in the engine while a writer holds the
        // gate (CLAUDE.md section 3: peer-controlled sizes are bounded). A legitimate peer makes
        // the client queue at most a few small records (alerts, close_notify); a hostile one can
        // make it queue one alert per tiny record it sends (TLS 1.2 hello_request is answered
        // with a no_renegotiation warning, about 7x amplification) while the socket is stalled.
        internal const int DefaultMaxPendingOutputBytes = 64 * 1024;

        private readonly Stream _inner;
        private readonly TlsClientProtocol _protocol;
        private readonly object _protocolLock = new object();
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);

        // Owned by the current reader (_readInProgress == 1).
        private readonly byte[] _readBuffer = new byte[CiphertextBufferSize];
        private byte[] _readScratch;

        // Owned by the _writeGate holder.
        private readonly byte[] _writeBuffer = new byte[CiphertextBufferSize];
        private byte[] _writeScratch;

        private int _readInProgress;
        private int _disposed;

        /// <summary>
        /// Output bytes the engine may hold after the read path tried to flush; above it the read
        /// fails and the connection is aborted. Settable for tests; set before the first read.
        /// </summary>
        internal int MaxPendingOutputBytes { get; set; } = DefaultMaxPendingOutputBytes;

        internal BouncyCastleTlsStream(Stream inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _protocol = new TlsClientProtocol();
        }

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanWrite => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Runs the client handshake with async I/O on the inner stream. On failure the inner
        /// stream is disposed (after a best-effort send of a locally raised fatal alert) and the
        /// BouncyCastle exception propagates unchanged for the provider to classify.
        /// </summary>
        internal async Task HandshakeAsync(TlsClient client, CancellationToken ct)
        {
            try
            {
                lock (_protocolLock)
                {
                    _protocol.Connect(client); // queues the ClientHello; never blocks in this mode
                }

                await FlushPendingOutputAsync().ConfigureAwait(false);

                while (true)
                {
                    lock (_protocolLock)
                    {
                        if (!_protocol.IsHandshaking)
                        {
                            if (!_protocol.IsConnected)
                                throw new IOException("The TLS handshake did not complete.");
                            break;
                        }
                    }

                    int read = await _inner.ReadAsync(_readBuffer, 0, _readBuffer.Length, ct)
                        .ConfigureAwait(false);
                    if (read <= 0)
                    {
                        // A transport close, not a TLS verdict: stays a (retryable) network error.
                        throw new IOException("The server closed the connection during the TLS handshake.");
                    }

                    lock (_protocolLock)
                    {
                        _protocol.OfferInput(_readBuffer, 0, read);
                    }

                    await FlushPendingOutputAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // RFC 8446 section 6.2: a locally detected fatal error is reported with an alert.
                // The engine queued it in its output buffer; deliver it if the socket still works.
                try
                {
                    await FlushPendingOutputAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best effort only: the original failure is what the caller needs.
                }

                Abort();
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            CheckBufferArguments(buffer, offset, count);
            return ReadCoreAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ReadCoreAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            CheckBufferArguments(buffer, offset, count);
            ThrowIfDisposed();
            if (count == 0)
                return 0;

            EnterRead();
            try
            {
                var destination = new Memory<byte>(buffer, offset, count);
                while (true)
                {
                    if (TryReadPlaintext(destination, out int plaintext))
                        return plaintext;

                    int read = _inner.Read(_readBuffer, 0, _readBuffer.Length);
                    try
                    {
                        ProcessCiphertext(read);
                    }
                    catch
                    {
                        try { FlushPendingOutput(); } catch { }
                        throw;
                    }

                    FlushPendingOutput();
                    EnforceOutputBacklogLimit();
                }
            }
            finally
            {
                Volatile.Write(ref _readInProgress, 0);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            CheckBufferArguments(buffer, offset, count);
            return WriteCoreAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return WriteCoreAsync(buffer, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckBufferArguments(buffer, offset, count);
            ThrowIfDisposed();
            if (count == 0)
                return;

            _writeGate.Wait();
            try
            {
                var source = new ReadOnlyMemory<byte>(buffer, offset, count);
                for (int done = 0; done < source.Length;)
                {
                    done += EncryptChunk(source, done);
                    DrainOutputHoldingGate();
                }
            }
            finally
            {
                _writeGate.Release();
            }

            FlushPendingOutput();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            // Every write drains its ciphertext before returning; only the inner stream may buffer.
            ThrowIfDisposed();
            return _inner.FlushAsync(cancellationToken);
        }

        public override void Flush()
        {
            ThrowIfDisposed();
            _inner.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    // RFC 8446 section 6.1: send close_notify before closing the write side. Only
                    // when no write owns the record stream; otherwise the socket is closed as is.
                    lock (_protocolLock)
                    {
                        _protocol.Close();
                    }

                    if (_writeGate.Wait(0))
                    {
                        try
                        {
                            DrainOutputHoldingGate();
                        }
                        finally
                        {
                            _writeGate.Release();
                        }
                    }
                }
                catch
                {
                    // Best effort: the peer may already be gone.
                }
                finally
                {
                    try { _inner.Dispose(); } catch { }
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>Closes the transport without a close_notify (failed handshake).</summary>
        private void Abort()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { _inner.Dispose(); } catch { }
        }

        private async ValueTask<int> ReadCoreAsync(Memory<byte> destination, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (destination.Length == 0)
                return 0;

            EnterRead();
            try
            {
                while (true)
                {
                    if (TryReadPlaintext(destination, out int plaintext))
                        return plaintext;

                    int read = await _inner.ReadAsync(_readBuffer, 0, _readBuffer.Length, ct)
                        .ConfigureAwait(false);
                    try
                    {
                        ProcessCiphertext(read);
                    }
                    catch
                    {
                        // A locally raised fatal alert sits in the output queue (RFC 8446 6.2).
                        try { await FlushPendingOutputAsync().ConfigureAwait(false); } catch { }
                        throw;
                    }

                    await FlushPendingOutputAsync().ConfigureAwait(false);
                    EnforceOutputBacklogLimit();
                }
            }
            finally
            {
                Volatile.Write(ref _readInProgress, 0);
            }
        }

        private async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> source, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (source.IsEmpty)
                return;

            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                for (int done = 0; done < source.Length;)
                {
                    done += EncryptChunk(source, done);
                    await DrainOutputHoldingGateAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _writeGate.Release();
            }

            // Records a concurrent reader queued while this write held the gate.
            await FlushPendingOutputAsync().ConfigureAwait(false);
        }

        /// <summary>Encrypts up to one record of plaintext into the engine's output queue.</summary>
        /// <remarks>Caller holds <c>_writeGate</c>.</remarks>
        private int EncryptChunk(ReadOnlyMemory<byte> source, int offset)
        {
            int count = Math.Min(source.Length - offset, PlaintextChunkSize);
            var chunk = source.Slice(offset, count);
            byte[] array;
            int arrayOffset;
            if (MemoryMarshal.TryGetArray(chunk, out ArraySegment<byte> segment))
            {
                array = segment.Array;
                arrayOffset = segment.Offset;
            }
            else
            {
                array = _writeScratch ??= new byte[PlaintextChunkSize];
                chunk.CopyTo(array);
                arrayOffset = 0;
            }

            lock (_protocolLock)
            {
                _protocol.WriteApplicationData(array, arrayOffset, count);
            }

            return count;
        }

        /// <summary>
        /// Returns decrypted bytes if any are queued, or end of stream (0) once the peer's
        /// close_notify was processed. Caller owns the read path.
        /// </summary>
        private bool TryReadPlaintext(Memory<byte> destination, out int read)
        {
            lock (_protocolLock)
            {
                if (_protocol.GetAvailableInputBytes() > 0)
                {
                    if (MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination, out ArraySegment<byte> segment))
                    {
                        read = _protocol.ReadInput(segment.Array, segment.Offset, destination.Length);
                    }
                    else
                    {
                        var scratch = _readScratch ??= new byte[PlaintextChunkSize];
                        read = _protocol.ReadInput(scratch, 0, Math.Min(destination.Length, scratch.Length));
                        scratch.AsSpan(0, read).CopyTo(destination.Span);
                    }

                    return true;
                }

                if (_protocol.IsClosed)
                {
                    if (_protocol.IsFailed)
                        throw new IOException("The TLS connection has failed.");

                    read = 0;
                    return true;
                }
            }

            read = 0;
            return false;
        }

        /// <summary>Feeds ciphertext (or end of stream) read from the inner stream to the engine.</summary>
        private void ProcessCiphertext(int read)
        {
            lock (_protocolLock)
            {
                if (read <= 0)
                {
                    // Same contract as the blocking engine: end of stream without close_notify
                    // throws TlsNoCloseNotifyException (an IOException).
                    _protocol.CloseInput();
                }
                else
                {
                    _protocol.OfferInput(_readBuffer, 0, read);
                }
            }
        }

        private bool HasPendingOutput()
        {
            lock (_protocolLock)
            {
                return _protocol.GetAvailableOutputBytes() > 0;
            }
        }

        /// <summary>
        /// Called by the reader after its flush attempt. When a writer holds the gate (stalled on
        /// a full socket) the flush cannot run, and every record the peer makes the engine queue
        /// stays in memory; past the bound the connection is aborted instead of growing the queue.
        /// Disposing the inner stream also fails the stalled write.
        /// </summary>
        private void EnforceOutputBacklogLimit()
        {
            int pending;
            lock (_protocolLock)
            {
                pending = _protocol.GetAvailableOutputBytes();
            }

            if (pending <= MaxPendingOutputBytes)
                return;

            Abort();
            throw new IOException(
                "The TLS peer caused more queued output than the connection can send; connection aborted.");
        }

        private int TakeOutput()
        {
            lock (_protocolLock)
            {
                return _protocol.ReadOutput(_writeBuffer, 0, _writeBuffer.Length);
            }
        }

        /// <remarks>
        /// Not cancelable: ciphertext taken from the engine must reach the socket, or the record
        /// stream is desynchronised for every later write. A stalled socket is broken by disposal.
        /// </remarks>
        private async ValueTask DrainOutputHoldingGateAsync()
        {
            int count;
            while ((count = TakeOutput()) > 0)
            {
                await _inner.WriteAsync(_writeBuffer, 0, count, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private void DrainOutputHoldingGate()
        {
            int count;
            while ((count = TakeOutput()) > 0)
            {
                _inner.Write(_writeBuffer, 0, count);
            }
        }

        /// <summary>
        /// Sends queued records when no writer holds the gate. A writer that holds it drains the
        /// queue before releasing and calls this again afterwards, and every producer checks the
        /// queue after enqueuing, so no record is stranded and no caller ever waits for the gate.
        /// </summary>
        private async ValueTask FlushPendingOutputAsync()
        {
            while (HasPendingOutput() && _writeGate.Wait(0))
            {
                try
                {
                    await DrainOutputHoldingGateAsync().ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
        }

        private void FlushPendingOutput()
        {
            while (HasPendingOutput() && _writeGate.Wait(0))
            {
                try
                {
                    DrainOutputHoldingGate();
                }
                finally
                {
                    _writeGate.Release();
                }
            }
        }

        private void EnterRead()
        {
            if (Interlocked.Exchange(ref _readInProgress, 1) != 0)
                throw new InvalidOperationException("Concurrent reads on a TLS stream are not supported.");
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(BouncyCastleTlsStream));
        }

        private static void CheckBufferArguments(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || count > buffer.Length - offset)
                throw new ArgumentOutOfRangeException(nameof(count));
        }
    }
}
