using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Middleware
{
    /// <summary>
    /// Decodes one <c>gzip</c> or <c>deflate</c> content-coding layer (RFC 9110 section 8.4.1) on top of the
    /// platform's raw DEFLATE inflater (<see cref="DeflateStream"/>, the only inflater .NET Standard 2.1 has):
    /// <list type="bullet">
    /// <item><c>gzip</c> (RFC 1952): parses every member header itself, inflates the member's DEFLATE data and
    /// checks the member's CRC32 + ISIZE trailer. A body of several members (section 2.2) decodes member by
    /// member, the same on Mono and .NET (whose <see cref="GZipStream"/> differ on multi-member input).</item>
    /// <item><c>deflate</c>: the zlib format (RFC 1950) when the first two bytes are a valid zlib header, with the
    /// Adler-32 trailer checked; otherwise raw DEFLATE (RFC 1951), which some servers send and browsers accept.</item>
    /// </list>
    /// The layer reads its input to the end: after the last gzip member or the zlib trailer only end of input is
    /// accepted, after raw DEFLATE at most <see cref="MaxBytesAfterRawEnd"/> bytes are skipped. So a decoder
    /// stacked on another layer always drives the layer below to its own end and checks. A truncated or corrupt
    /// body fails with <see cref="InvalidDataException"/> instead of reading as a clean end of stream; disposing
    /// before the end skips the checks by design (early dispose is not a completed body).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Finding a member end: <see cref="DeflateStream"/> does not report how much input it consumed, and the
    /// bytes after the DEFLATE data (trailer, next member) may already sit in its input buffer. The inflater pulls
    /// input only once it has consumed what it was given, so its data ends inside the last chunks it pulled. The
    /// input is therefore handed out in chunks of at most <see cref="MaxHandBytes"/>, the last
    /// <see cref="HistoryBytes"/> handed bytes (at least two chunks) stay in <see cref="_in"/>, and at the member
    /// end the trailer is the first position in that window whose 8 bytes equal the computed CRC32 and ISIZE and
    /// are followed by end of input or the next member's ID1 ID2. A trailer that does not match fails the body.
    /// </para>
    /// <para>
    /// Threading: one reader at a time (the response body source contract), so the parse and buffer fields need
    /// no synchronization; async continuations order the accesses. <see cref="_state"/>,
    /// <see cref="_disposed"/> and <see cref="_inflater"/> are read with <see cref="Volatile"/> or swapped with
    /// <see cref="Interlocked"/> because an abort may dispose the stream from another thread.
    /// <see cref="_in"/> is a plain array (not pooled), so a dispose racing a read never returns a buffer in use.
    /// </para>
    /// </remarks>
    internal sealed class InflateLayerStream : Stream
    {
        internal const int InputBufferBytes = 8 * 1024;
        internal const int MaxHandBytes = 2 * 1024;
        internal const int HistoryBytes = 2 * MaxHandBytes;
        internal const int MaxGzipHeaderBytes = 128 * 1024;
        internal const int MaxBytesAfterRawEnd = 64 * 1024;

        private const int GzipFixedHeaderBytes = 10;
        private const int GzipTrailerBytes = 8;
        private const int ZlibTrailerBytes = 4;
        private const byte GzipId1 = 0x1F;
        private const byte GzipId2 = 0x8B;
        private const byte DeflateMethod = 8;
        private const int GzipFlagHeaderCrc = 0x02;
        private const int GzipFlagExtra = 0x04;
        private const int GzipFlagName = 0x08;
        private const int GzipFlagComment = 0x10;
        private const int GzipReservedFlags = 0xE0;
        private const int ZlibFlagDictionary = 0x20;
        private const uint AdlerModulus = 65521;
        private const int AdlerBlockBytes = 5552;

        private const int StateReading = 0;
        private const int StateValidated = 1;
        private const int StateFailed = 2;

        private static readonly uint[] Crc32Table = BuildCrc32Table();

        private readonly Stream _source;
        private readonly bool _leaveOpen;
        private readonly bool _gzip;
        private readonly byte[] _in = new byte[InputBufferBytes];
        private readonly InflaterInput _inflaterInput;

        // _in holds [0, _inEnd); bytes before _inPos were consumed (header) or handed to the inflater.
        private int _inPos;
        private int _inEnd;
        private bool _sourceEnded;

        private Phase _phase;
        private Format _format;
        private HeaderStep _headerStep;
        private int _headerFlags;
        private int _headerSkip;
        private int _headerBytes;

        private DeflateStream _inflater;
        private bool _inflaterStarved;

        // Trailer search window: [_windowStart, _handEnd] in _in, the candidate positions of the member end.
        private int _windowStart;
        private int _handEnd;
        private long _rawTrailingBytes;

        private uint _crc32;
        private long _memberBytes;
        private uint _adlerA;
        private uint _adlerB;

        private int _state;
        private int _disposed;

        private enum Phase
        {
            DeflateHeader,
            GzipHeader,
            Body,
            Trailer,
            RawDrain,
            Done
        }

        private enum Format
        {
            Gzip,
            Zlib,
            RawDeflate
        }

        private enum HeaderStep
        {
            Fixed,
            ExtraLength,
            Extra,
            Name,
            Comment,
            HeaderCrc
        }

        /// <param name="source">The layer input (wire bytes or the decoder below).</param>
        /// <param name="leaveOpen">True when the caller owns <paramref name="source"/>.</param>
        /// <param name="gzip">True for the <c>gzip</c> coding, false for <c>deflate</c>.</param>
        internal InflateLayerStream(Stream source, bool leaveOpen, bool gzip)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _leaveOpen = leaveOpen;
            _gzip = gzip;
            _inflaterInput = new InflaterInput(this);
            _phase = gzip ? Phase.GzipHeader : Phase.DeflateHeader;
            _format = gzip ? Format.Gzip : Format.RawDeflate;
        }

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(new Span<byte>(buffer, offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || !CheckReadable())
                return 0;

            try
            {
                while (true)
                {
                    if (_phase == Phase.Body)
                    {
                        var read = CurrentInflater().Read(buffer);
                        if (read > 0)
                        {
                            Observe(buffer.Slice(0, read));
                            return read;
                        }

                        EndBody();
                        continue;
                    }

                    if (_phase == Phase.Done)
                        return 0;

                    if (!TryAdvance())
                    {
                        var start = PrepareFill();
                        FillFromSource(_source.Read(_in, start, _in.Length - start));
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
            {
                Volatile.Write(ref _state, StateFailed);
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty || !CheckReadable())
                return new ValueTask<int>(0);

            return ReadCoreAsync(buffer, cancellationToken);
        }

        private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    if (_phase == Phase.Body)
                    {
                        var read = await CurrentInflater().ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read > 0)
                        {
                            Observe(buffer.Span.Slice(0, read));
                            return read;
                        }

                        EndBody();
                        continue;
                    }

                    if (_phase == Phase.Done)
                        return 0;

                    if (!TryAdvance())
                    {
                        var start = PrepareFill();
                        FillFromSource(
                            await _source.ReadAsync(_in.AsMemory(start, _in.Length - start), cancellationToken)
                                .ConfigureAwait(false));
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
            {
                Volatile.Write(ref _state, StateFailed);
                throw;
            }
        }

        private bool CheckReadable()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(InflateLayerStream));

            var state = Volatile.Read(ref _state);
            if (state == StateFailed)
                throw new InvalidDataException("The compressed payload failed validation.");

            return state == StateReading;
        }

        private DeflateStream CurrentInflater()
        {
            return Volatile.Read(ref _inflater) ?? throw new ObjectDisposedException(nameof(InflateLayerStream));
        }

        /// <summary>
        /// Runs the non-body phases on the buffered input. Returns false when more input is needed; once the
        /// source has ended it always decides (moves on, finishes or throws).
        /// </summary>
        private bool TryAdvance()
        {
            switch (_phase)
            {
                case Phase.DeflateHeader:
                    return TryReadDeflateHeader();
                case Phase.GzipHeader:
                    return TryReadGzipHeader();
                case Phase.Trailer:
                    return _format == Format.Gzip ? TryReadGzipTrailer() : TryReadZlibTrailer();
                case Phase.RawDrain:
                    return TryDrainRaw();
                default:
                    throw new InvalidOperationException("Unexpected decoder phase.");
            }
        }

        private bool TryReadDeflateHeader()
        {
            var available = _inEnd - _inPos;
            if (available < 2 && !_sourceEnded)
                return false;

            // RFC 1950 section 2.2: CM = 8, CINFO <= 7, (CMF * 256 + FLG) is a multiple of 31. Anything else is
            // taken as raw DEFLATE (RFC 9110 section 8.4.1.2 names zlib; raw DEFLATE is a common server error).
            _format = Format.RawDeflate;
            if (available >= 2)
            {
                int cmf = _in[_inPos];
                int flg = _in[_inPos + 1];
                if ((cmf & 0x0F) == DeflateMethod && (cmf >> 4) <= 7 && ((cmf << 8) | flg) % 31 == 0)
                {
                    // RFC 1950 section 2.3: a decoder without the preset dictionary must report an error.
                    if ((flg & ZlibFlagDictionary) != 0)
                        throw new InvalidDataException("zlib streams with a preset dictionary are not supported.");

                    _format = Format.Zlib;
                    _inPos += 2;
                }
            }

            StartBody();
            return true;
        }

        private bool TryReadGzipHeader()
        {
            // RFC 1952 section 2.3: ID1 ID2 CM FLG MTIME(4) XFL OS, then FEXTRA, FNAME, FCOMMENT, FHCRC when flagged.
            while (true)
            {
                var available = _inEnd - _inPos;
                switch (_headerStep)
                {
                    case HeaderStep.Fixed:
                        if (available < GzipFixedHeaderBytes)
                            return NeedHeaderBytes();
                        if (_in[_inPos] != GzipId1 || _in[_inPos + 1] != GzipId2)
                            throw new InvalidDataException("The GZIP header is invalid.");
                        if (_in[_inPos + 2] != DeflateMethod)
                            throw new InvalidDataException("The GZIP compression method is not supported.");

                        _headerFlags = _in[_inPos + 3];
                        if ((_headerFlags & GzipReservedFlags) != 0)
                            throw new InvalidDataException("The GZIP header sets reserved flags.");

                        ConsumeHeader(GzipFixedHeaderBytes);
                        _headerStep = HeaderStep.ExtraLength;
                        break;
                    case HeaderStep.ExtraLength:
                        if ((_headerFlags & GzipFlagExtra) == 0)
                        {
                            _headerStep = HeaderStep.Name;
                            break;
                        }

                        if (available < 2)
                            return NeedHeaderBytes();

                        _headerSkip = _in[_inPos] | (_in[_inPos + 1] << 8);
                        ConsumeHeader(2);
                        _headerStep = HeaderStep.Extra;
                        break;
                    case HeaderStep.Extra:
                        if (_headerSkip == 0)
                        {
                            _headerStep = HeaderStep.Name;
                            break;
                        }

                        if (available == 0)
                            return NeedHeaderBytes();

                        var skip = Math.Min(available, _headerSkip);
                        ConsumeHeader(skip);
                        _headerSkip -= skip;
                        break;
                    case HeaderStep.Name:
                    case HeaderStep.Comment:
                        var flag = _headerStep == HeaderStep.Name ? GzipFlagName : GzipFlagComment;
                        if ((_headerFlags & flag) != 0)
                        {
                            if (available == 0)
                                return NeedHeaderBytes();

                            // Zero-terminated field: skip through the terminator.
                            var terminator = _in.AsSpan(_inPos, available).IndexOf((byte)0);
                            if (terminator < 0)
                            {
                                ConsumeHeader(available);
                                break;
                            }

                            ConsumeHeader(terminator + 1);
                        }

                        _headerStep = _headerStep == HeaderStep.Name ? HeaderStep.Comment : HeaderStep.HeaderCrc;
                        break;
                    case HeaderStep.HeaderCrc:
                        if ((_headerFlags & GzipFlagHeaderCrc) != 0)
                        {
                            // RFC 1952 section 2.3.1.2: a decoder may skip the header CRC16 without checking it.
                            if (available < 2)
                                return NeedHeaderBytes();

                            ConsumeHeader(2);
                        }

                        StartBody();
                        return true;
                }
            }
        }

        private bool NeedHeaderBytes()
        {
            if (_sourceEnded)
                throw new InvalidDataException("GZIP payload is truncated.");

            return false;
        }

        private void ConsumeHeader(int count)
        {
            _inPos += count;
            _headerBytes += count;
            if (_headerBytes > MaxGzipHeaderBytes)
                throw new InvalidDataException($"The GZIP header exceeds {MaxGzipHeaderBytes} bytes.");
        }

        private void StartBody()
        {
            _windowStart = _inPos;
            _handEnd = _inPos;
            _inflaterStarved = false;
            _crc32 = uint.MaxValue;
            _memberBytes = 0;
            _adlerA = 1;
            _adlerB = 0;
            Volatile.Write(ref _inflater, new DeflateStream(_inflaterInput, CompressionMode.Decompress, leaveOpen: true));
            _phase = Phase.Body;
        }

        private void EndBody()
        {
            Interlocked.Exchange(ref _inflater, null)?.Dispose();
            _handEnd = _inPos;
            if (_format == Format.RawDeflate)
            {
                _rawTrailingBytes = 0;
                _phase = Phase.RawDrain;
                return;
            }

            // The member end lies in the last chunks handed to the inflater (see the class remarks).
            _windowStart = Math.Max(_windowStart, _handEnd - HistoryBytes);
            _phase = Phase.Trailer;
        }

        private bool TryReadGzipTrailer()
        {
            // Every candidate needs its 8 trailer bytes plus 2 more to tell end of input from the next member.
            var needed = _handEnd - _windowStart + GzipTrailerBytes + 2;
            if (_inEnd - _windowStart < needed && !_sourceEnded)
                return false;

            var expectedCrc32 = _crc32 ^ uint.MaxValue;
            var expectedSize = unchecked((uint)_memberBytes);
            for (var position = _windowStart; position <= _handEnd; position++)
            {
                if (_inEnd - position < GzipTrailerBytes)
                    break;

                if (ReadUInt32LittleEndian(position) != expectedCrc32 ||
                    ReadUInt32LittleEndian(position + 4) != expectedSize)
                {
                    continue;
                }

                var next = position + GzipTrailerBytes;
                var after = _inEnd - next;
                if (after == 0 && _sourceEnded)
                {
                    _inPos = next;
                    Finish();
                    return true;
                }

                if (after >= 2 && _in[next] == GzipId1 && _in[next + 1] == GzipId2)
                {
                    // RFC 1952 section 2.2: another member follows.
                    _inPos = next;
                    _headerStep = HeaderStep.Fixed;
                    _headerBytes = 0;
                    _phase = Phase.GzipHeader;
                    return true;
                }
            }

            throw new InvalidDataException(
                _inflaterStarved || _inEnd - _windowStart < GzipTrailerBytes
                    ? "GZIP payload is truncated."
                    : "GZIP trailer validation failed.");
        }

        private bool TryReadZlibTrailer()
        {
            // A zlib stream is one stream: the Adler-32 trailer must be the last 4 bytes of the input.
            var needed = _handEnd - _windowStart + ZlibTrailerBytes + 1;
            if (_inEnd - _windowStart < needed && !_sourceEnded)
                return false;

            var position = _inEnd - ZlibTrailerBytes;
            if (!_sourceEnded || position > _handEnd)
                throw new InvalidDataException("Unexpected data after the end of the compressed payload.");
            if (_inflaterStarved || position < _windowStart)
                throw new InvalidDataException("zlib payload is truncated.");

            var expected = (_adlerB << 16) | _adlerA;
            var actual = (uint)((_in[position] << 24) | (_in[position + 1] << 16) | (_in[position + 2] << 8) |
                                _in[position + 3]);
            if (actual != expected)
                throw new InvalidDataException("zlib Adler-32 validation failed.");

            _inPos = _inEnd;
            Finish();
            return true;
        }

        private bool TryDrainRaw()
        {
            // Raw DEFLATE has no trailer; skip a bounded amount of trailing input so a layer below still ends.
            _rawTrailingBytes += _inEnd - _inPos;
            _inPos = _inEnd;
            if (_rawTrailingBytes > MaxBytesAfterRawEnd)
                throw new InvalidDataException("Unexpected data after the end of the compressed payload.");
            if (!_sourceEnded)
                return false;

            Finish();
            return true;
        }

        private void Finish()
        {
            _phase = Phase.Done;
            Volatile.Write(ref _state, StateValidated);
        }

        /// <summary>
        /// Makes room at the end of <see cref="_in"/> for a source read and returns the write offset. Keeps the
        /// trailer window during the trailer phase and only the unconsumed bytes otherwise.
        /// </summary>
        private int PrepareFill()
        {
            Compact(_phase == Phase.Trailer ? _windowStart : _inPos);
            if (_inEnd == _in.Length)
                throw new InvalidOperationException("Decoder input buffer has no room.");

            return _inEnd;
        }

        private void FillFromSource(int read)
        {
            if (read <= 0)
            {
                _sourceEnded = true;
                return;
            }

            _inEnd += read;
        }

        private void Compact(int keepFrom)
        {
            if (keepFrom <= 0)
                return;

            var keep = _inEnd - keepFrom;
            if (keep > 0)
                Buffer.BlockCopy(_in, keepFrom, _in, 0, keep);

            _inEnd = keep;
            _inPos -= keepFrom;
            _windowStart = Math.Max(0, _windowStart - keepFrom);
            _handEnd = Math.Max(0, _handEnd - keepFrom);
        }

        // Inflater input: hands out buffered bytes in chunks of at most MaxHandBytes and refills _in from the
        // source only when everything handed so far was pulled, keeping the last HistoryBytes for the trailer.
        private int HandToInflater(Span<byte> destination)
        {
            var count = Math.Min(destination.Length, Math.Min(_inEnd - _inPos, MaxHandBytes));
            _in.AsSpan(_inPos, count).CopyTo(destination);
            _inPos += count;
            return count;
        }

        private int PrepareInflaterFill()
        {
            Compact(Math.Max(_windowStart, _inPos - HistoryBytes));
            return _inEnd;
        }

        private int ReadForInflater(Span<byte> destination)
        {
            if (destination.IsEmpty)
                return 0;

            if (_inPos == _inEnd)
            {
                if (!_sourceEnded)
                {
                    var start = PrepareInflaterFill();
                    FillFromSource(_source.Read(_in, start, _in.Length - start));
                }

                if (_inPos == _inEnd)
                {
                    _inflaterStarved = true;
                    return 0;
                }
            }

            return HandToInflater(destination);
        }

        private ValueTask<int> ReadForInflaterAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (destination.IsEmpty)
                return new ValueTask<int>(0);

            if (_inPos < _inEnd)
                return new ValueTask<int>(HandToInflater(destination.Span));

            return RefillForInflaterAsync(destination, cancellationToken);
        }

        private async ValueTask<int> RefillForInflaterAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (!_sourceEnded)
            {
                var start = PrepareInflaterFill();
                FillFromSource(
                    await _source.ReadAsync(_in.AsMemory(start, _in.Length - start), cancellationToken)
                        .ConfigureAwait(false));
            }

            if (_inPos == _inEnd)
            {
                _inflaterStarved = true;
                return 0;
            }

            return HandToInflater(destination.Span);
        }

        private void Observe(ReadOnlySpan<byte> decoded)
        {
            if (_format == Format.Gzip)
            {
                _crc32 = UpdateCrc32(_crc32, decoded);
                _memberBytes += decoded.Length;
            }
            else if (_format == Format.Zlib)
            {
                UpdateAdler32(decoded);
            }
        }

        private void UpdateAdler32(ReadOnlySpan<byte> data)
        {
            // RFC 1950 section 8.2; reduce every 5552 bytes so the sums cannot overflow 32 bits.
            uint a = _adlerA;
            uint b = _adlerB;
            while (!data.IsEmpty)
            {
                var count = Math.Min(data.Length, AdlerBlockBytes);
                for (var i = 0; i < count; i++)
                {
                    a += data[i];
                    b += a;
                }

                a %= AdlerModulus;
                b %= AdlerModulus;
                data = data.Slice(count);
            }

            _adlerA = a;
            _adlerB = b;
        }

        private uint ReadUInt32LittleEndian(int offset)
        {
            return (uint)(_in[offset] | (_in[offset + 1] << 8) | (_in[offset + 2] << 16) | (_in[offset + 3] << 24));
        }

        private static uint UpdateCrc32(uint crc32, ReadOnlySpan<byte> data)
        {
            var crc = crc32;
            for (var i = 0; i < data.Length; i++)
                crc = Crc32Table[(int)((crc ^ data[i]) & 0xFF)] ^ (crc >> 8);

            return crc;
        }

        private static uint[] BuildCrc32Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                var value = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0
                        ? 0xEDB88320u ^ (value >> 1)
                        : value >> 1;
                }

                table[(int)i] = value;
            }

            return table;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    Interlocked.Exchange(ref _inflater, null)?.Dispose();
                }
                finally
                {
                    if (!_leaveOpen)
                        _source.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>The read-only stream the inflater pulls compressed bytes from; it reads the owning layer.</summary>
        private sealed class InflaterInput : Stream
        {
            private readonly InflateLayerStream _owner;

            internal InflaterInput(InflateLayerStream owner)
            {
                _owner = owner;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _owner.ReadForInflater(new Span<byte>(buffer, offset, count));
            }

            public override int Read(Span<byte> buffer)
            {
                return _owner.ReadForInflater(buffer);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return _owner.ReadForInflaterAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                return _owner.ReadForInflaterAsync(buffer, cancellationToken);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
