using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Core
{
    public abstract class UHttpRequestBody : IDisposable
    {
        private int _activeSession;
        private int _openedNonReplayable;
        private int _disposed;
        private int _resourcesDisposed;
        private int _sessionFaulted;

        public abstract bool IsEmpty { get; }

        public abstract long? Length { get; }

        public abstract RequestBodyReplayability Replayability { get; }

        public abstract bool TryGetBufferedData(out ReadOnlyMemory<byte> data);

        internal abstract UHttpRequestBody CloneDetached();

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
                FailSessionAndRelease);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (Volatile.Read(ref _activeSession) == 0)
                DisposeCoreOnce();
        }

        protected virtual void DisposeCore()
        {
        }

        protected void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(GetType().Name);
        }

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

        private void FailSessionAndRelease()
        {
            Interlocked.Exchange(ref _sessionFaulted, 1);
            ReleaseSession();
        }

        private void DisposeCoreOnce()
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
                return;

            DisposeCore();
            GC.SuppressFinalize(this);
        }

    }

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

        internal override UHttpRequestBody CloneDetached()
        {
            return new EmptyRequestBody();
        }
    }

    public sealed class BufferedRequestBody : UHttpRequestBody
    {
        private readonly ReadOnlyMemory<byte> _data;

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

        internal override UHttpRequestBody CloneDetached()
        {
            return _data.IsEmpty
                ? (UHttpRequestBody)new EmptyRequestBody()
                : new BufferedRequestBody(_data.ToArray());
        }
    }

    public sealed class OwnedMemoryRequestBody : UHttpRequestBody
    {
        private IMemoryOwner<byte> _owner;
        private readonly int _length;

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

        internal override UHttpRequestBody CloneDetached()
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
    }

    public sealed class StreamRequestBody : UHttpRequestBody
    {
        private readonly Stream _stream;
        private readonly long? _contentLength;
        private readonly bool _leaveOpen;
        private readonly long _startPosition;

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

        internal override UHttpRequestBody CloneDetached()
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
    }

    public sealed class FactoryRequestBody : UHttpRequestBody
    {
        private readonly Func<CancellationToken, ValueTask<Stream>> _factory;
        private readonly long? _contentLength;

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

        internal override UHttpRequestBody CloneDetached()
        {
            return new FactoryRequestBody(_factory, _contentLength);
        }
    }
}
