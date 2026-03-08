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
            Assert.AreEqual("hello", Encoding.UTF8.GetString(clone.Body.ToArray()));
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
    }
}
