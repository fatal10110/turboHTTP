using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Files
{
    public sealed class FileRequestBody : UHttpRequestBody
    {
        private readonly string _path;
        private readonly int _bufferSize;
        private readonly long _length;

        public FileRequestBody(string path, int bufferSize = 32768)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("File path is required.", nameof(path));
            if (bufferSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(bufferSize), bufferSize, "Must be > 0.");

            _path = path;
            _bufferSize = bufferSize;
            _length = new FileInfo(path).Length;
        }

        public string Path => _path;

        public int BufferSize => _bufferSize;

        public override bool IsEmpty => _length == 0;

        public override long? Length => _length;

        public override RequestBodyReplayability Replayability => RequestBodyReplayability.Replayable;

        public override bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            data = default;
            return false;
        }

        internal override ValueTask<RequestBodyReadSession> OpenReadSessionCoreAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                _bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return new ValueTask<RequestBodyReadSession>(
                CreateReadSession(stream, _length));
        }

        internal override UHttpRequestBody CloneDetached()
        {
            return new FileRequestBody(_path, _bufferSize);
        }

        public override int GetHashCode()
        {
            return RuntimeHelpers.GetHashCode(this);
        }
    }
}
