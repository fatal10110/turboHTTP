using System;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Cache
{
    internal interface IStreamingCacheAccumulator : IDisposable
    {
        long Length { get; }
        void Write(ReadOnlySpan<byte> chunk);
        SegmentedBuffer DetachBuffer();
    }

    internal sealed class SegmentedBufferStreamingCacheAccumulator : IStreamingCacheAccumulator
    {
        private SegmentedBuffer _buffer = new SegmentedBuffer();

        public long Length => _buffer?.Length ?? 0;

        public void Write(ReadOnlySpan<byte> chunk)
        {
            _buffer.Write(chunk);
        }

        public SegmentedBuffer DetachBuffer()
        {
            var buffer = _buffer;
            _buffer = null;
            return buffer;
        }

        public void Dispose()
        {
            _buffer?.Dispose();
            _buffer = null;
        }
    }
}
