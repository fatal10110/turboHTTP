using System;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class UHttpRequestCloneTests
    {
        [Test]
        public void Clone_CreatesDetachedCopy()
        {
            var body = Encoding.UTF8.GetBytes("hello");
            var request = new UHttpRequest(HttpMethod.POST, new Uri("https://example.test/resource"));
            request.WithHeader("X-Test", "one");
            request.WithBody(body);
            request.WithMetadata("TraceId", "abc");
            request.WithTimeout(TimeSpan.FromSeconds(9));

            var clone = request.Clone();

            body[0] = (byte)'j';
            request.WithHeader("X-Test", "mutated");
            request.WithMetadata("TraceId", "mutated");
            request.WithTimeout(TimeSpan.FromSeconds(3));

            Assert.AreEqual(HttpMethod.POST, clone.Method);
            Assert.AreEqual(request.Uri, clone.Uri);
            Assert.AreEqual("one", clone.Headers.Get("X-Test"));
            Assert.AreEqual("abc", clone.Metadata["TraceId"]);
            Assert.AreEqual(TimeSpan.FromSeconds(9), clone.Timeout);
            Assert.IsTrue(clone.Content.TryGetBufferedData(out var clonedBody));
            Assert.AreEqual("hello", Encoding.UTF8.GetString(clonedBody.ToArray()));
        }

        [Test]
        public void Clone_StreamBody_ThrowsInvalidOperationException()
        {
            using var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes("payload"));
            var request = new UHttpRequest(HttpMethod.POST, new Uri("https://example.test/upload"))
                .WithStreamBody(stream, stream.Length, leaveOpen: true);

            var ex = Assert.Throws<InvalidOperationException>(() => request.Clone());
            StringAssert.Contains("cannot be detached-cloned", ex.Message);
        }

        [Test]
        public void CopyWithSharedContent_DoesNotTakeOwnershipOfOriginalBody()
        {
            var owner = new TrackingMemoryOwner(Encoding.UTF8.GetBytes("shared"));
            var request = new UHttpRequest(HttpMethod.POST, new Uri("https://example.test/shared"))
                .WithLeasedBody(owner, 6);

            var copy = request.CopyWithSharedContent();
            copy.Dispose();

            Assert.IsFalse(owner.Disposed);

            request.Dispose();

            Assert.IsTrue(owner.Disposed);
        }

        [Test]
        public void CreateForBackground_ReturnsFreshContext()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/background"));
            var foreground = new RequestContext(request);
            foreground.RecordEvent("Foreground");
            foreground.SetState("key", 42);

            var background = RequestContext.CreateForBackground(request.Clone());

            Assert.AreEqual(request.Method, background.Request.Method);
            Assert.AreEqual(0, background.Timeline.Count);
            Assert.AreEqual(0, background.State.Count);
        }

        private sealed class TrackingMemoryOwner : System.Buffers.IMemoryOwner<byte>
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
    }
}
