using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class UHttpRequestBodyTests
    {
        [Test]
        public async Task EmptyRequestBody_ReadsEmpty_AndCanBeReopened()
        {
            var body = new EmptyRequestBody();

            Assert.IsTrue(body.IsEmpty);
            Assert.IsTrue(body.TryGetBufferedData(out var data));
            Assert.AreEqual(0, data.Length);

            Assert.AreEqual(string.Empty, await ReadAllAsync(body.OpenReadSessionAsync(CancellationToken.None)));
            Assert.AreEqual(string.Empty, await ReadAllAsync(body.OpenReadSessionAsync(CancellationToken.None)));
        }

        [Test]
        public async Task BufferedRequestBody_ExposesBufferedData_AndCanBeReopened()
        {
            var body = new BufferedRequestBody(Encoding.UTF8.GetBytes("hello"));

            Assert.AreEqual(RequestBodyReplayability.Replayable, body.Replayability);
            Assert.IsTrue(body.TryGetBufferedData(out var data));
            Assert.AreEqual("hello", Encoding.UTF8.GetString(data.Span));

            Assert.AreEqual("hello", await ReadAllAsync(body.OpenReadSessionAsync(CancellationToken.None)));
            Assert.AreEqual("hello", await ReadAllAsync(body.OpenReadSessionAsync(CancellationToken.None)));
        }

        [Test]
        public async Task OwnedMemoryRequestBody_DisposesOwner_AndReadsExpectedSlice()
        {
            var owner = new TrackingMemoryOwner(Encoding.UTF8.GetBytes("payload-data"));
            var body = new OwnedMemoryRequestBody(owner, 7);

            Assert.IsTrue(body.TryGetBufferedData(out var data));
            Assert.AreEqual("payload", Encoding.UTF8.GetString(data.Span));
            Assert.AreEqual("payload", await ReadAllAsync(body.OpenReadSessionAsync(CancellationToken.None)));

            body.Dispose();

            Assert.IsTrue(owner.Disposed);
        }

        [Test]
        public async Task OwnedMemoryRequestBody_DisposeDuringActiveSession_DefersOwnerReleaseUntilSessionCloses()
        {
            var owner = new TrackingMemoryOwner(Encoding.UTF8.GetBytes("payload"));
            var body = new OwnedMemoryRequestBody(owner, 7);
            var session = await body.OpenReadSessionAsync(CancellationToken.None);

            body.Dispose();

            Assert.IsFalse(owner.Disposed);

            session.Dispose();

            Assert.IsTrue(owner.Disposed);
        }

        [Test]
        public async Task StreamRequestBody_RewindsToCapturedStartPosition_ForReplayableStreams()
        {
            var bytes = Encoding.UTF8.GetBytes("prefix-payload");
            using var stream = new MemoryStream(bytes);
            stream.Position = "prefix-".Length;

            var body = new StreamRequestBody(stream, contentLength: bytes.Length - "prefix-".Length, leaveOpen: true);

            Assert.AreEqual(RequestBodyReplayability.Replayable, body.Replayability);
            Assert.AreEqual("payload", await ReadAllAsync(body.OpenReadSessionAsync(CancellationToken.None)));

            stream.Position = bytes.Length;
            Assert.AreEqual("payload", await ReadAllAsync(body.OpenReadSessionAsync(CancellationToken.None)));
        }

        [Test]
        public async Task StreamRequestBody_NonReplayableStream_CannotBeReopened()
        {
            using var stream = new NonSeekableStream(Encoding.UTF8.GetBytes("once"));
            var body = new StreamRequestBody(stream, contentLength: 4, leaveOpen: true);

            Assert.AreEqual(RequestBodyReplayability.NonReplayable, body.Replayability);
            Assert.AreEqual("once", await ReadAllAsync(body.OpenReadSessionAsync(CancellationToken.None)));

            var ex = AssertAsync.ThrowsAsync<InvalidOperationException>(
                async () => await ReadAllAsync(body.OpenReadSessionAsync(CancellationToken.None)));
            StringAssert.Contains("cannot be reopened", ex.Message);
        }

        [Test]
        public async Task FactoryRequestBody_RequiresSingleActiveReader_ButSupportsSequentialReopen()
        {
            int openCount = 0;
            var body = new FactoryRequestBody(
                _ =>
                {
                    Interlocked.Increment(ref openCount);
                    return new ValueTask<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("factory")));
                },
                contentLength: 7);

            var session = await body.OpenReadSessionAsync(CancellationToken.None);
            try
            {
                var ex = AssertAsync.ThrowsAsync<InvalidOperationException>(
                    async () => await ReadAllAsync(body.OpenReadSessionAsync(CancellationToken.None)));
                StringAssert.Contains("active read session", ex.Message);
            }
            finally
            {
                session.Dispose();
            }

            Assert.AreEqual("factory", await ReadAllAsync(body.OpenReadSessionAsync(CancellationToken.None)));
            Assert.AreEqual(2, openCount);
        }

        [Test]
        public void RequestBodyReadSession_ThrowsAfterDispose()
        {
            var body = new BufferedRequestBody(Encoding.UTF8.GetBytes("abc"));
            var session = body.OpenReadSessionAsync(CancellationToken.None).Result;
            session.Dispose();

            var ex = AssertAsync.ThrowsAsync<ObjectDisposedException>(
                async () => await session.ReadAsync(new byte[4], CancellationToken.None));
            StringAssert.Contains("RequestBodyReadSession", ex.ObjectName);
        }

        [Test]
        public async Task RequestBodyReadSession_DisposeFailure_BlocksBodyReopen()
        {
            int openCount = 0;
            var body = new FactoryRequestBody(
                _ =>
                {
                    Interlocked.Increment(ref openCount);
                    return new ValueTask<Stream>(new ThrowingDisposeStream(Encoding.UTF8.GetBytes("payload")));
                },
                contentLength: 7);

            var session = await body.OpenReadSessionAsync(CancellationToken.None);

            var disposeEx = Assert.Throws<IOException>(() => session.Dispose());
            StringAssert.Contains("dispose failed", disposeEx.Message);

            var reopenEx = AssertAsync.ThrowsAsync<InvalidOperationException>(
                async () => await body.OpenReadSessionAsync(CancellationToken.None));
            StringAssert.Contains("failed during disposal", reopenEx.Message);
            Assert.AreEqual(1, openCount);
        }

        private static async Task<string> ReadAllAsync(ValueTask<RequestBodyReadSession> pendingSession)
        {
            return await ReadAllAsync(await pendingSession.ConfigureAwait(false));
        }

        private static async Task<string> ReadAllAsync(RequestBodyReadSession session)
        {
            using (session)
            {
                var buffer = new byte[4];
                using var stream = new MemoryStream();
                while (true)
                {
                    int read = await session.ReadAsync(buffer, CancellationToken.None);
                    if (read == 0)
                        break;

                    await stream.WriteAsync(buffer, 0, read);
                }

                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private sealed class TrackingMemoryOwner : IMemoryOwner<byte>
        {
            private byte[] _buffer;

            public TrackingMemoryOwner(byte[] buffer)
            {
                _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            }

            public bool Disposed { get; private set; }

            public Memory<byte> Memory => _buffer;

            public void Dispose()
            {
                Disposed = true;
                _buffer = Array.Empty<byte>();
            }
        }

        private sealed class NonSeekableStream : MemoryStream
        {
            public NonSeekableStream(byte[] buffer)
                : base(buffer, writable: false)
            {
            }

            public override bool CanSeek => false;

            public override long Position
            {
                get => base.Position;
                set => throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin loc)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class ThrowingDisposeStream : MemoryStream
        {
            public ThrowingDisposeStream(byte[] buffer)
                : base(buffer, writable: false)
            {
            }

            protected override void Dispose(bool disposing)
            {
                throw new IOException("dispose failed");
            }
        }
    }
}
