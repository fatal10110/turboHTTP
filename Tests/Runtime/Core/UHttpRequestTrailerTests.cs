using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using CoreHttpMethod = TurboHTTP.Core.HttpMethod;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class UHttpRequestTrailerTests
    {
        [Test]
        public void WithRequestTrailers_ConfiguresBodyAndDeclarationHeader()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
            Func<HttpHeaders> provider = () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Digest", "sha-256=abc");
                return headers;
            };

            var request = new UHttpRequest(CoreHttpMethod.POST, new Uri("https://example.test/upload"))
                .WithStreamBody(stream, contentLength: null, leaveOpen: true)
                .WithRequestTrailers(new[] { "Digest", "X-Chunk-Count" }, provider);

            Assert.AreEqual("Digest, X-Chunk-Count", request.Headers.Get("Trailer"));
            Assert.AreSame(provider, request.Content.TrailerProvider);
            Assert.AreEqual(2, request.Content.DeclaredTrailerNames.Count);
            Assert.AreEqual("Digest", request.Content.DeclaredTrailerNames[0]);
            Assert.AreEqual("X-Chunk-Count", request.Content.DeclaredTrailerNames[1]);
        }

        [Test]
        public void WithRequestTrailers_WithHeaders_ReappliesManagedTrailerDeclaration()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
            var replacementHeaders = new HttpHeaders();
            replacementHeaders.Set("X-Test", "value");

            var request = new UHttpRequest(CoreHttpMethod.POST, new Uri("https://example.test/upload"))
                .WithStreamBody(stream, contentLength: null, leaveOpen: true)
                .WithRequestTrailers(new[] { "Digest" }, CreateDigestProvider())
                .WithHeaders(replacementHeaders);

            Assert.AreEqual("value", request.Headers.Get("X-Test"));
            Assert.AreEqual("Digest", request.Headers.Get("Trailer"));
            Assert.AreEqual(1, request.Headers.GetValues("Trailer").Count);
        }

        [Test]
        public void ReplacingBody_ClearsManagedTrailerDeclaration()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
            var request = new UHttpRequest(CoreHttpMethod.POST, new Uri("https://example.test/upload"))
                .WithStreamBody(stream, contentLength: null, leaveOpen: true)
                .WithRequestTrailers(new[] { "Digest" }, CreateDigestProvider())
                .WithBody("buffered");

            Assert.IsNull(request.Headers.Get("Trailer"));
            Assert.IsNull(request.Content.TrailerProvider);
            Assert.AreEqual(0, request.Content.DeclaredTrailerNames.Count);
        }

        [Test]
        public void Clone_PreservesRequestTrailersAndDeclarationHeader()
        {
            var request = new UHttpRequest(CoreHttpMethod.POST, new Uri("https://example.test/upload"))
                .WithBodyFactory(
                    _ => new ValueTask<Stream>(
                        new MemoryStream(Encoding.UTF8.GetBytes("payload"), writable: false)),
                    contentLength: null)
                .WithRequestTrailers(new[] { "Digest" }, CreateDigestProvider());

            var clone = request.Clone();

            Assert.AreEqual("Digest", clone.Headers.Get("Trailer"));
            Assert.IsNotNull(clone.Content.TrailerProvider);
            Assert.AreEqual(1, clone.Content.DeclaredTrailerNames.Count);
            Assert.AreEqual("Digest", clone.Content.DeclaredTrailerNames[0]);
        }

        [Test]
        public void WithRequestTrailers_DuplicateDeclaredNames_ThrowsArgumentException()
        {
            var request = new UHttpRequest(CoreHttpMethod.POST, new Uri("https://example.test/upload"))
                .WithBodyFactory(
                    _ => new ValueTask<Stream>(
                        new MemoryStream(Encoding.UTF8.GetBytes("payload"), writable: false)),
                    contentLength: null);

            var ex = Assert.Throws<ArgumentException>(() =>
                request.WithRequestTrailers(new[] { "Digest", "digest" }, CreateDigestProvider()));

            StringAssert.Contains("Duplicate trailer field name declared", ex.Message);
        }

        private static Func<HttpHeaders> CreateDigestProvider()
        {
            return () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Digest", "sha-256=abc");
                return headers;
            };
        }
    }
}
