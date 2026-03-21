using System;
using System.Buffers;
using System.Threading;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Ownership-carrying buffered response body detached from an <see cref="IResponseBodySource"/>.
    /// </summary>
    /// <remarks>
    /// The optional <paramref name="owner"/> is transferred to the eventual
    /// <see cref="UHttpResponse"/> and must be safe to dispose from the response finalizer path.
    /// Owner transfer state is shared through a reference-type token so defensive copies of this
    /// value remain safe even when methods are invoked through readonly fields or interface flows.
    /// </remarks>
    public readonly struct DetachedBufferedBody
    {
        private readonly ReadOnlyMemory<byte> _memory;
        private readonly ReadOnlySequence<byte> _sequence;
        private readonly OwnershipState _ownerState;
        private readonly bool _hasSequence;

        public DetachedBufferedBody(ReadOnlyMemory<byte> body, IDisposable owner = null)
        {
            _memory = body;
            _sequence = default;
            _ownerState = owner != null ? new OwnershipState(owner) : null;
            _hasSequence = false;
        }

        public DetachedBufferedBody(ReadOnlySequence<byte> body, IDisposable owner = null)
        {
            _memory = default;
            _sequence = body;
            _ownerState = owner != null ? new OwnershipState(owner) : null;
            _hasSequence = true;
        }

        internal ReadOnlySequence<byte> Sequence =>
            _hasSequence
                ? _sequence
                : (_memory.IsEmpty ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(_memory));

        internal IDisposable DetachOwner()
        {
            return _ownerState?.DetachOwner();
        }

        internal void DisposeOwnedResources()
        {
            _ownerState?.DisposeOwner();
        }

        private sealed class OwnershipState
        {
            private object _owner;

            public OwnershipState(IDisposable owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }

            public IDisposable DetachOwner()
            {
                return Interlocked.Exchange(ref _owner, null) as IDisposable;
            }

            public void DisposeOwner()
            {
                (Interlocked.Exchange(ref _owner, null) as IDisposable)?.Dispose();
            }
        }
    }
}
