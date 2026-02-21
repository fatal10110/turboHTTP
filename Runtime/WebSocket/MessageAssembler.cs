using System;
using System.Buffers;
using System.Collections.Generic;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Reassembles fragmented data frames into complete messages.
    /// Control frames are surfaced immediately and do not affect fragment state.
    /// </summary>
    public sealed class MessageAssembler
    {
        private readonly int _maxMessageSize;
        private readonly int _maxFragmentCount;
        private readonly List<FragmentBufferLease> _fragments;

        private bool _fragmentedMessageInProgress;
        private WebSocketOpcode _fragmentedOpcode;
        private byte _fragmentedFirstRsvBits;
        private int _fragmentCount;
        private int _accumulatedPayloadBytes;

        public MessageAssembler(
            int maxMessageSize = WebSocketConstants.DefaultMaxMessageSize,
            int maxFragmentCount = WebSocketConstants.DefaultMaxFragmentCount)
        {
            if (maxMessageSize < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxMessageSize),
                    maxMessageSize,
                    "Max message size must be positive.");
            }

            if (maxFragmentCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxFragmentCount),
                    maxFragmentCount,
                    "Max fragment count must be positive.");
            }

            _maxMessageSize = maxMessageSize;
            _maxFragmentCount = maxFragmentCount;
            _fragments = new List<FragmentBufferLease>(4);
        }

        public bool FragmentedMessageInProgress => _fragmentedMessageInProgress;

        /// <summary>
        /// Processes a frame and returns a complete assembled message when available.
        /// Ownership of <paramref name="frameLease"/> is consumed by this method.
        /// </summary>
        public bool TryAssemble(WebSocketFrameReadLease frameLease, out WebSocketAssembledMessage message)
        {
            if (frameLease == null)
                throw new ArgumentNullException(nameof(frameLease));

            message = null;
            var frame = frameLease.Frame;

            if (frame.IsControlFrame)
            {
                message = CreateMessageFromLease(frameLease, frame.Opcode);
                return true;
            }

            if (frame.Opcode == WebSocketOpcode.Continuation)
            {
                if (!_fragmentedMessageInProgress)
                {
                    frameLease.Dispose();
                    throw new WebSocketProtocolException(
                        WebSocketError.UnexpectedContinuation,
                        "Continuation frame received without an active fragmented message.");
                }

                if (frame.IsRsv1Set)
                {
                    frameLease.Dispose();
                    Reset();
                    throw new WebSocketProtocolException(
                        WebSocketError.ProtocolViolation,
                        "Continuation frame must not set RSV1.");
                }

                if (!TryStageFragment(frameLease))
                    return false;

                message = AssembleFragmentedMessageAndReset();
                return true;
            }

            if (frame.Opcode != WebSocketOpcode.Text && frame.Opcode != WebSocketOpcode.Binary)
            {
                frameLease.Dispose();
                throw new WebSocketProtocolException(
                    WebSocketError.InvalidFrame,
                    "Only text, binary, or continuation frames are valid data frames.");
            }

            if (_fragmentedMessageInProgress)
            {
                frameLease.Dispose();
                Reset();
                throw new WebSocketProtocolException(
                    WebSocketError.ProtocolViolation,
                    "New data frame received while previous fragmented message is still in progress.");
            }

            if (frame.IsFinal)
            {
                message = CreateMessageFromLease(frameLease, frame.Opcode);
                return true;
            }

            _fragmentedMessageInProgress = true;
            _fragmentedOpcode = frame.Opcode;
            _fragmentedFirstRsvBits = frame.RsvBits;

            if (!TryStageFragment(frameLease))
                return false;

            message = AssembleFragmentedMessageAndReset();
            return true;
        }

        /// <summary>
        /// Clears fragment state and returns all retained pooled buffers.
        /// </summary>
        public void Reset()
        {
            ReturnStagedFragments();
            _fragmentedMessageInProgress = false;
            _fragmentedOpcode = WebSocketOpcode.Continuation;
            _fragmentedFirstRsvBits = 0;
            _fragmentCount = 0;
            _accumulatedPayloadBytes = 0;
        }

        private bool TryStageFragment(WebSocketFrameReadLease frameLease)
        {
            var frame = frameLease.Frame;
            int payloadLength = checked((int)frame.PayloadLength);

            int projectedFragmentCount = _fragmentCount + 1;
            if (projectedFragmentCount > _maxFragmentCount)
            {
                frameLease.Dispose();
                Reset();
                throw new WebSocketProtocolException(
                    WebSocketError.ProtocolViolation,
                    "Fragment count exceeds configured limit.",
                    WebSocketCloseCode.MessageTooBig);
            }

            int projectedSize;
            try
            {
                projectedSize = checked(_accumulatedPayloadBytes + payloadLength);
            }
            catch (OverflowException)
            {
                frameLease.Dispose();
                Reset();
                throw new WebSocketProtocolException(
                    WebSocketError.PayloadLengthOverflow,
                    "Message payload size overflowed while assembling fragments.",
                    WebSocketCloseCode.MessageTooBig);
            }

            if (projectedSize > _maxMessageSize)
            {
                frameLease.Dispose();
                Reset();
                throw new WebSocketProtocolException(
                    WebSocketError.MessageTooLarge,
                    "Message size exceeds configured limit.",
                    WebSocketCloseCode.MessageTooBig);
            }

            var buffer = frameLease.DetachPayloadBuffer(out var detachedLength);
            frameLease.Dispose();

            if (detachedLength > 0 && buffer == null)
            {
                Reset();
                throw new WebSocketProtocolException(
                    WebSocketError.InvalidFrame,
                    "Missing payload buffer for non-empty fragment.");
            }

            _fragments.Add(new FragmentBufferLease(buffer, detachedLength));
            _fragmentCount = projectedFragmentCount;
            _accumulatedPayloadBytes = projectedSize;

            return frame.IsFinal;
        }

        private WebSocketAssembledMessage AssembleFragmentedMessageAndReset()
        {
            byte[] payload = null;
            int payloadLength = _accumulatedPayloadBytes;

            try
            {
                if (payloadLength > 0)
                {
                    payload = ArrayPool<byte>.Shared.Rent(payloadLength);

                    int copyOffset = 0;
                    for (int i = 0; i < _fragments.Count; i++)
                    {
                        var fragment = _fragments[i];
                        if (fragment.Length == 0)
                            continue;

                        Buffer.BlockCopy(fragment.Buffer, 0, payload, copyOffset, fragment.Length);
                        copyOffset += fragment.Length;
                    }
                }

                var opcode = _fragmentedOpcode;
                var rsvBits = _fragmentedFirstRsvBits;
                Reset();
                return new WebSocketAssembledMessage(opcode, payload, payloadLength, rsvBits);
            }
            catch
            {
                if (payload != null)
                    ArrayPool<byte>.Shared.Return(payload);

                Reset();
                throw;
            }
        }

        private static WebSocketAssembledMessage CreateMessageFromLease(
            WebSocketFrameReadLease frameLease,
            WebSocketOpcode opcode)
        {
            var rsvBits = frameLease.Frame.RsvBits;
            var buffer = frameLease.DetachPayloadBuffer(out var length);
            frameLease.Dispose();
            return new WebSocketAssembledMessage(opcode, buffer, length, rsvBits);
        }

        private void ReturnStagedFragments()
        {
            for (int i = 0; i < _fragments.Count; i++)
            {
                _fragments[i].Dispose();
            }

            _fragments.Clear();
        }

        private readonly struct FragmentBufferLease : IDisposable
        {
            public FragmentBufferLease(byte[] buffer, int length)
            {
                Buffer = buffer;
                Length = length;
            }

            public byte[] Buffer { get; }

            public int Length { get; }

            public void Dispose()
            {
                if (Buffer != null)
                    ArrayPool<byte>.Shared.Return(Buffer);
            }
        }
    }

    /// <summary>
    /// Leased assembled message payload.
    /// Dispose to return pooled payload buffer to ArrayPool.
    /// </summary>
    public sealed class WebSocketAssembledMessage : IDisposable
    {
        private byte[] _payloadBuffer;
        private int _payloadLength;

        internal WebSocketAssembledMessage(
            WebSocketOpcode opcode,
            byte[] payloadBuffer,
            int payloadLength,
            byte rsvBits = 0)
        {
            if (payloadLength < 0)
                throw new ArgumentOutOfRangeException(nameof(payloadLength), payloadLength, "Length cannot be negative.");

            if (payloadLength > 0 && payloadBuffer == null)
            {
                throw new ArgumentNullException(
                    nameof(payloadBuffer),
                    "Non-empty message requires a payload buffer.");
            }

            if ((rsvBits & ~0x70) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rsvBits),
                    rsvBits,
                    "RSV bits must be a subset of mask 0x70.");
            }

            Opcode = opcode;
            RsvBits = (byte)(rsvBits & 0x70);
            _payloadBuffer = payloadBuffer;
            _payloadLength = payloadLength;
        }

        public WebSocketOpcode Opcode { get; }

        public byte RsvBits { get; }

        public bool IsRsv1Set => (RsvBits & 0x40) != 0;

        public bool IsRsv2Set => (RsvBits & 0x20) != 0;

        public bool IsRsv3Set => (RsvBits & 0x10) != 0;

        public ReadOnlyMemory<byte> Payload
        {
            get
            {
                var buffer = _payloadBuffer;
                if (buffer == null || _payloadLength == 0)
                    return ReadOnlyMemory<byte>.Empty;

                return new ReadOnlyMemory<byte>(buffer, 0, _payloadLength);
            }
        }

        public int PayloadLength => _payloadLength;

        public bool IsText => Opcode == WebSocketOpcode.Text;

        public bool IsBinary => Opcode == WebSocketOpcode.Binary;

        public bool IsControl => WebSocketFrame.IsControlOpcode(Opcode);

        internal byte[] DetachPayloadBuffer(out int length)
        {
            var buffer = _payloadBuffer;
            length = _payloadLength;
            _payloadBuffer = null;
            _payloadLength = 0;
            return buffer;
        }

        public void Dispose()
        {
            var buffer = _payloadBuffer;
            if (buffer == null)
                return;

            _payloadBuffer = null;
            _payloadLength = 0;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
