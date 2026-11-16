using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Content of a request. Use <see cref="EmptyRequestBody"/>, <see cref="BufferedRequestBody"/>, <see cref="OwnedMemoryRequestBody"/>,
    /// <see cref="StreamRequestBody"/> or <see cref="FactoryRequestBody"/>. The request owns its body and disposes it once;
    /// a body that can be read only once is <see cref="RequestBodyReplayability.NonReplayable"/> and is not retried.
    /// </summary>
    public abstract class UHttpRequestBody : IDisposable
    {
        private static readonly string[] EmptyDeclaredTrailerNames = Array.Empty<string>();
        private int _activeSession;
        private int _openedNonReplayable;
        private int _disposed;
        private int _resourcesDisposed;
        private int _sessionFaulted;
        private Task _abandonedBufferedWrite;
        private Func<HttpHeaders> _trailerProvider;
        private string[] _declaredTrailerNames = EmptyDeclaredTrailerNames;

        /// <summary>
        /// Gets whether the body has no content.
        /// </summary>
        public abstract bool IsEmpty { get; }

        /// <summary>
        /// Gets the body length in bytes, or null when unknown.
        /// </summary>
        public abstract long? Length { get; }

        /// <summary>
        /// Gets whether the body can be sent again (retry, redirect, authentication challenge).
        /// </summary>
        public abstract RequestBodyReplayability Replayability { get; }

        /// <summary>
        /// Returns the body without copying when it is held in memory.
        /// </summary>
        /// <param name="data">The body bytes when this returns <c>true</c>.</param>
        public abstract bool TryGetBufferedData(out ReadOnlyMemory<byte> data);

        internal Func<HttpHeaders> TrailerProvider => Volatile.Read(ref _trailerProvider);

        internal IReadOnlyList<string> DeclaredTrailerNames => Volatile.Read(ref _declaredTrailerNames);

        internal UHttpRequestBody CloneDetached()
        {
            var clone = CloneDetachedCore();
            if (clone == null)
                throw new InvalidOperationException("Request body clone factory returned null.");

            clone.CopyTrailerConfigurationFrom(this);
            return clone;
        }

        internal abstract UHttpRequestBody CloneDetachedCore();

        internal void SetRequestTrailers(
            IReadOnlyList<string> declaredTrailerNames,
            Func<HttpHeaders> trailerProvider)
        {
            if (trailerProvider == null)
            {
                Volatile.Write(ref _trailerProvider, null);
                Volatile.Write(ref _declaredTrailerNames, EmptyDeclaredTrailerNames);
                return;
            }

            if (declaredTrailerNames == null)
                throw new ArgumentNullException(nameof(declaredTrailerNames));

            Volatile.Write(ref _declaredTrailerNames, CloneDeclaredTrailerNames(declaredTrailerNames));
            Volatile.Write(ref _trailerProvider, trailerProvider);
        }

        internal void CopyTrailerConfigurationFrom(UHttpRequestBody source)
        {
            if (source == null)
            {
                Volatile.Write(ref _trailerProvider, null);
                Volatile.Write(ref _declaredTrailerNames, EmptyDeclaredTrailerNames);
                return;
            }

            var sourceProvider = Volatile.Read(ref source._trailerProvider);
            if (sourceProvider == null)
            {
                Volatile.Write(ref _trailerProvider, null);
                Volatile.Write(ref _declaredTrailerNames, EmptyDeclaredTrailerNames);
                return;
            }

            Volatile.Write(
                ref _declaredTrailerNames,
                CloneDeclaredTrailerNames(Volatile.Read(ref source._declaredTrailerNames)));
            Volatile.Write(ref _trailerProvider, sourceProvider);
        }

        private static string[] CloneDeclaredTrailerNames(IReadOnlyList<string> declaredTrailerNames)
        {
            if (declaredTrailerNames == null || declaredTrailerNames.Count == 0)
                return EmptyDeclaredTrailerNames;

            var copy = new string[declaredTrailerNames.Count];
            for (int i = 0; i < declaredTrailerNames.Count; i++)
                copy[i] = declaredTrailerNames[i];

            return copy;
        }

        internal ValueTask<RequestBodyReadSession> OpenReadSessionAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            ThrowIfSessionFaulted();
            BeginSession();

            ValueTask<RequestBodyReadSession> pending;
            try
            {
                pending = OpenReadSessionCoreAsync(ct);
            }
            catch
            {
                ReleaseSession();
                throw;
            }

            if (pending.IsCompletedSuccessfully)
            {
                try
                {
                    return new ValueTask<RequestBodyReadSession>(ValidateSession(pending.Result));
                }
                catch
                {
                    ReleaseSession();
                    throw;
                }
            }

            return AwaitSessionAsync(pending);
        }

        internal abstract ValueTask<RequestBodyReadSession> OpenReadSessionCoreAsync(CancellationToken ct);

        /// <summary>
        /// Called by a transport when a write over the memory <see cref="TryGetBufferedData"/> exposed
        /// was abandoned on cancellation and may still read that memory (#633: Mono and IL2CPP may not
        /// end a pending send once its token fired). The body's resources (a leased buffer of
        /// <see cref="OwnedMemoryRequestBody"/>) are released only after <paramref name="pendingWrite"/>
        /// ends, so the pool never hands that memory to another renter while the platform still sends
        /// it. Must be called before the body is disposed; the request owner disposes it only after
        /// the send that abandoned the write has returned.
        /// </summary>
        internal void DeferResourceReleaseUntil(Task pendingWrite)
        {
            if (pendingWrite == null || pendingWrite.IsCompleted)
                return;

            while (true)
            {
                var current = Volatile.Read(ref _abandonedBufferedWrite);
                var next = current == null || current.IsCompleted
                    ? pendingWrite
                    : Task.WhenAll(current, pendingWrite);
                if (Interlocked.CompareExchange(ref _abandonedBufferedWrite, next, current) == current)
                    return;
            }
        }

        internal RequestBodyReadSession CreateReadSession(
            Stream stream,
            long? contentLength,
            bool disposeStream = true)
        {
            return new RequestBodyReadSession(
                stream,
                contentLength,
                disposeStream,
                ReleaseSession,
                FailSession);
        }

        /// <summary>
        /// Releases the body. If a send is still using it, release happens when that send ends. Safe to call more than once.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (Volatile.Read(ref _activeSession) == 0)
                DisposeCoreOnce();
        }

        /// <summary>
        /// Releases resources owned by the body. Called once.
        /// </summary>
        protected virtual void DisposeCore()
        {
        }

        /// <summary>
        /// Throws <see cref="ObjectDisposedException"/> when the body is disposed.
        /// </summary>
        protected void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(GetType().Name);
        }

        /// <summary>
        /// Releases resources from a finalizer for a body that was never disposed.
        /// </summary>
        protected void DisposeCoreFromFinalizer()
        {
            DisposeCoreOnce();
        }

        private void BeginSession()
        {
            if (Interlocked.CompareExchange(ref _activeSession, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "Request body already has an active read session.");
            }

            if (Replayability == RequestBodyReplayability.NonReplayable &&
                Interlocked.CompareExchange(ref _openedNonReplayable, 1, 0) != 0)
            {
                Interlocked.Exchange(ref _activeSession, 0);
                throw new InvalidOperationException(
                    "This request body cannot be reopened after the first read session completes.");
            }
        }

        private async ValueTask<RequestBodyReadSession> AwaitSessionAsync(
            ValueTask<RequestBodyReadSession> pending)
        {
            try
            {
                return ValidateSession(await pending.ConfigureAwait(false));
            }
            catch
            {
                ReleaseSession();
                throw;
            }
        }

        private RequestBodyReadSession ValidateSession(RequestBodyReadSession session)
        {
            if (session == null)
                throw new InvalidOperationException("Request body session factory returned null.");

            return session;
        }

        private void ThrowIfSessionFaulted()
        {
            if (Volatile.Read(ref _sessionFaulted) != 0)
            {
                throw new InvalidOperationException(
                    "This request body cannot be reopened because a prior read session failed during disposal.");
            }
        }

        private void ReleaseSession()
        {
            Interlocked.Exchange(ref _activeSession, 0);

            if (Volatile.Read(ref _disposed) != 0)
                DisposeCoreOnce();
        }

        private void FailSession()
        {
            Interlocked.Exchange(ref _sessionFaulted, 1);
        }

        private void DisposeCoreOnce()
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
                return;

            GC.SuppressFinalize(this);
            var abandonedWrite = Volatile.Read(ref _abandonedBufferedWrite);
            if (abandonedWrite != null && !abandonedWrite.IsCompleted)
            {
                // The abandoned write's observer (AbandonableTask) observes its fault; this
                // continuation only releases the memory once the platform no longer reads it.
                abandonedWrite.ContinueWith(
                    static (_, state) => ((UHttpRequestBody)state).DisposeCoreAfterAbandonedWrite(),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }

            DisposeCore();
        }

        private void DisposeCoreAfterAbandonedWrite()
        {
            try
            {
                DisposeCore();
            }
            catch (Exception)
            {
                // No caller is left to report to: the request was disposed before the write ended.
            }
        }

        protected static int ComputeStableHash(ReadOnlySpan<byte> data)
        {
            unchecked
            {
                var hash = 17;
                for (int i = 0; i < data.Length; i++)
                {
                    hash = (hash * 31) ^ data[i];
                }

                return hash;
            }
        }
    }

    /// <summary>
    /// A request without content.
    /// </summary>
    public sealed class EmptyRequestBody : UHttpRequestBody
    {
        public override bool IsEmpty => true;

        public override long? Length => 0;

        public override RequestBodyReplayability Replayability => RequestBodyReplayability.Replayable;

        public override bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            data = ReadOnlyMemory<byte>.Empty;
            return true;
        }

        internal override ValueTask<RequestBodyReadSession> OpenReadSessionCoreAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return new ValueTask<RequestBodyReadSession>(
                CreateReadSession(
                    new ReadOnlySequenceStream(new ReadOnlySequence<byte>(ReadOnlyMemory<byte>.Empty)),
                    0));
        }

        internal override UHttpRequestBody CloneDetachedCore()
        {
            return new EmptyRequestBody();
        }

        public override int GetHashCode()
        {
            return 17;
        }
    }

    /// <summary>
    /// Request body backed by an in-memory buffer the caller keeps alive and unchanged until the request completes. Replayable.
    /// </summary>
    public sealed class BufferedRequestBody : UHttpRequestBody
    {
        private readonly ReadOnlyMemory<byte> _data;

        /// <summary>
        /// Creates the body.
        /// </summary>
        /// <param name="data">The bytes to send.</param>
        public BufferedRequestBody(ReadOnlyMemory<byte> data)
        {
            _data = data;
        }

        public override bool IsEmpty => _data.IsEmpty;

        public override long? Length => _data.Length;

        public override RequestBodyReplayability Replayability => RequestBodyReplayability.Replayable;

        public override bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            data = _data;
            return true;
        }

        internal override ValueTask<RequestBodyReadSession> OpenReadSessionCoreAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return new ValueTask<RequestBodyReadSession>(
                CreateReadSession(
                    new ReadOnlySequenceStream(new ReadOnlySequence<byte>(_data)),
                    _data.Length));
        }

        internal override UHttpRequestBody CloneDetachedCore()
        {
            return _data.IsEmpty
                ? (UHttpRequestBody)new EmptyRequestBody()
                : new BufferedRequestBody(_data.ToArray());
        }

        public override int GetHashCode()
        {
            return ComputeStableHash(_data.Span);
        }
    }

    /// <summary>
    /// Request body backed by pooled or owned memory. The body disposes the <see cref="IMemoryOwner{T}"/> when it is disposed.
    /// </summary>
    public sealed class OwnedMemoryRequestBody : UHttpRequestBody
    {
        private IMemoryOwner<byte> _owner;
        private readonly int _length;

        /// <summary>
        /// Creates the body and takes ownership of <paramref name="owner"/>.
        /// </summary>
        /// <param name="owner">The memory owner; disposed with this body.</param>
        /// <param name="length">Number of valid bytes at the start of the memory.</param>
        /// <exception cref="ArgumentNullException"><paramref name="owner"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative or exceeds the memory length.</exception>
        public OwnedMemoryRequestBody(IMemoryOwner<byte> owner, int length)
        {
            if (owner == null)
                throw new ArgumentNullException(nameof(owner));
            if (length < 0 || length > owner.Memory.Length)
                throw new ArgumentOutOfRangeException(nameof(length));

            _owner = owner;
            _length = length;
        }

        public override bool IsEmpty => _length == 0;

        public override long? Length => _length;

        public override RequestBodyReplayability Replayability => RequestBodyReplayability.Replayable;

        public override bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            ThrowIfDisposed();
            data = _owner.Memory.Slice(0, _length);
            return true;
        }

        internal override ValueTask<RequestBodyReadSession> OpenReadSessionCoreAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return new ValueTask<RequestBodyReadSession>(
                CreateReadSession(
                    new ReadOnlySequenceStream(new ReadOnlySequence<byte>(_owner.Memory.Slice(0, _length))),
                    _length));
        }

        internal override UHttpRequestBody CloneDetachedCore()
        {
            ThrowIfDisposed();
            return _length == 0
                ? (UHttpRequestBody)new EmptyRequestBody()
                : new BufferedRequestBody(_owner.Memory.Slice(0, _length).ToArray());
        }

        protected override void DisposeCore()
        {
            Interlocked.Exchange(ref _owner, null)?.Dispose();
        }

        ~OwnedMemoryRequestBody()
        {
            DisposeCoreFromFinalizer();
        }

        public override int GetHashCode()
        {
            var owner = Volatile.Read(ref _owner);
            return owner == null
                ? 0
                : ComputeStableHash(owner.Memory.Span.Slice(0, _length));
        }
    }

    /// <summary>
    /// Request body backed by a caller-provided <see cref="Stream"/>.
    /// <para>By default the body starts sending right after the headers, so a server may reject the
    /// request only after the upload has begun. To let the server decide first, opt in to
    /// <c>Expect: 100-continue</c> (RFC 9110 section 10.1.1) with <see cref="UHttpRequest.WithExpectContinue"/>
    /// and tune it through <see cref="StreamingOptions"/>. A non-replayable stream is consumed by a
    /// rejected attempt; prefer <see cref="FactoryRequestBody"/> when the body must be recreated safely.</para>
    /// </summary>
    public sealed class StreamRequestBody : UHttpRequestBody
    {
        private readonly Stream _stream;
        private readonly long? _contentLength;
        private readonly bool _leaveOpen;
        private readonly long _startPosition;

        /// <summary>
        /// Creates the body.
        /// </summary>
        /// <param name="stream">The stream to read the content from.</param>
        /// <param name="contentLength">Length in bytes when known; null when unknown.</param>
        /// <param name="leaveOpen">True to keep <paramref name="stream"/> open when the body is disposed.</param>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public StreamRequestBody(Stream stream, long? contentLength = null, bool leaveOpen = false)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (contentLength < 0)
                throw new ArgumentOutOfRangeException(nameof(contentLength));

            _stream = stream;
            _contentLength = contentLength;
            _leaveOpen = leaveOpen;
            _startPosition = stream.CanSeek ? stream.Position : 0;
        }

        public override bool IsEmpty => false;

        public override long? Length => _contentLength;

        public override RequestBodyReplayability Replayability =>
            _stream.CanSeek ? RequestBodyReplayability.Replayable : RequestBodyReplayability.NonReplayable;

        public override bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            data = default;
            return false;
        }

        internal override ValueTask<RequestBodyReadSession> OpenReadSessionCoreAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (_stream.CanSeek)
                _stream.Seek(_startPosition, SeekOrigin.Begin);

            return new ValueTask<RequestBodyReadSession>(
                CreateReadSession(_stream, _contentLength, disposeStream: false));
        }

        internal override UHttpRequestBody CloneDetachedCore()
        {
            throw new InvalidOperationException(
                "Stream-backed request bodies cannot be detached-cloned. Use a replayable factory body or a shared-content copy for same-dispatch mutations.");
        }

        protected override void DisposeCore()
        {
            if (!_leaveOpen)
                _stream.Dispose();
        }

        ~StreamRequestBody()
        {
            DisposeCoreFromFinalizer();
        }

        public override int GetHashCode()
        {
            return RuntimeHelpers.GetHashCode(this);
        }
    }

    /// <summary>
    /// Request body backed by a factory that creates a new <see cref="Stream"/> per read session.
    /// <para>By default the body starts sending right after the headers, before the server confirms
    /// acceptance. Opt in to <c>Expect: 100-continue</c> (RFC 9110 section 10.1.1) with
    /// <see cref="UHttpRequest.WithExpectContinue"/> and tune it through <see cref="StreamingOptions"/>.
    /// Factory-backed bodies remain the recommended choice when uploads may need replay after a
    /// server-side rejection.</para>
    /// </summary>
    public sealed class FactoryRequestBody : UHttpRequestBody
    {
        private readonly Func<CancellationToken, ValueTask<Stream>> _factory;
        private readonly long? _contentLength;

        /// <summary>
        /// Creates the body.
        /// </summary>
        /// <param name="factory">Creates a fresh stream for each send attempt.</param>
        /// <param name="contentLength">Length in bytes when known; null when unknown.</param>
        /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="contentLength"/> is negative.</exception>
        public FactoryRequestBody(
            Func<CancellationToken, ValueTask<Stream>> factory,
            long? contentLength = null)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            if (contentLength < 0)
                throw new ArgumentOutOfRangeException(nameof(contentLength));

            _factory = factory;
            _contentLength = contentLength;
        }

        public override bool IsEmpty => false;

        public override long? Length => _contentLength;

        public override RequestBodyReplayability Replayability =>
            RequestBodyReplayability.ReplayableViaFactory;

        public override bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            data = default;
            return false;
        }

        internal override ValueTask<RequestBodyReadSession> OpenReadSessionCoreAsync(CancellationToken ct)
        {
            var pending = _factory(ct);

            if (pending.IsCompletedSuccessfully)
                return new ValueTask<RequestBodyReadSession>(CreateFactorySession(pending.Result));

            return AwaitFactoryStreamAsync(pending);
        }

        private async ValueTask<RequestBodyReadSession> AwaitFactoryStreamAsync(ValueTask<Stream> pending)
        {
            return CreateFactorySession(await pending.ConfigureAwait(false));
        }

        private RequestBodyReadSession CreateFactorySession(Stream stream)
        {
            if (stream == null)
                throw new InvalidOperationException("Request body factory returned null.");

            return CreateReadSession(stream, _contentLength);
        }

        internal override UHttpRequestBody CloneDetachedCore()
        {
            return new FactoryRequestBody(_factory, _contentLength);
        }

        public override int GetHashCode()
        {
            return RuntimeHelpers.GetHashCode(this);
        }
    }
}
