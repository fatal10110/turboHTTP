using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class CoreTypesTests
    {
        [Test]
        public void UHttpRequest_Constructor_NullUri_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new UHttpRequest(HttpMethod.GET, null));
        }

        [Test]
        public void UHttpRequest_Constructor_ClonesHeaders_DefensiveCopy()
        {
            var sourceHeaders = new HttpHeaders();
            sourceHeaders.Set("X-Test", "one");

            var request = new UHttpRequest(
                HttpMethod.GET,
                new Uri("https://example.test"),
                sourceHeaders);

            sourceHeaders.Set("X-Test", "mutated");

            Assert.AreEqual("one", request.Headers.Get("X-Test"));
        }

        [Test]
        public void UHttpRequest_WithHeaders_DoesNotMutateOriginalRequest()
        {
            var originalHeaders = new HttpHeaders();
            originalHeaders.Set("X-Original", "value");

            var request = new UHttpRequest(
                HttpMethod.GET,
                new Uri("https://example.test"),
                originalHeaders);

            var replacementHeaders = new HttpHeaders();
            replacementHeaders.Set("X-New", "value");

            var changed = request.WithHeaders(replacementHeaders);
            replacementHeaders.Set("X-New", "mutated");

            Assert.IsNull(request.Headers.Get("X-New"));
            Assert.AreEqual("value", changed.Headers.Get("X-New"));
        }

        [Test]
        public void UHttpRequest_WithBody_PreservesOtherFields()
        {
            var headers = new HttpHeaders();
            headers.Set("X-Test", "a");
            var metadata = new Dictionary<string, object> { { "id", 5 } };
            var request = new UHttpRequest(
                HttpMethod.POST,
                new Uri("https://example.test/path"),
                headers,
                body: Encoding.UTF8.GetBytes("first"),
                timeout: TimeSpan.FromSeconds(9),
                metadata: metadata);

            var changed = request.WithBody(Encoding.UTF8.GetBytes("second"));

            Assert.AreEqual(request.Method, changed.Method);
            Assert.AreEqual(request.Uri, changed.Uri);
            Assert.AreEqual(request.Timeout, changed.Timeout);
            Assert.AreEqual("a", changed.Headers.Get("X-Test"));
            Assert.AreEqual(5, changed.Metadata["id"]);
            Assert.AreEqual("second", Encoding.UTF8.GetString(changed.Body));
        }

        [Test]
        public void UHttpRequest_WithTimeout_AcceptsBoundaryValues()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test"));

            var zeroTimeout = request.WithTimeout(TimeSpan.Zero);
            var negativeOne = request.WithTimeout(TimeSpan.FromMilliseconds(-1));

            Assert.AreEqual(TimeSpan.Zero, zeroTimeout.Timeout);
            Assert.AreEqual(TimeSpan.FromMilliseconds(-1), negativeOne.Timeout);
        }

        [Test]
        public void UHttpResponse_IsSuccessStatusCode_Matches2xxRange()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test"));

            var success = new UHttpResponse(HttpStatusCode.OK, new HttpHeaders(), null, TimeSpan.Zero, request);
            var notFound = new UHttpResponse(HttpStatusCode.NotFound, new HttpHeaders(), null, TimeSpan.Zero, request);

            Assert.IsTrue(success.IsSuccessStatusCode);
            Assert.IsFalse(notFound.IsSuccessStatusCode);
        }

        [Test]
        public void UHttpResponse_GetBodyAsString_Utf8_RoundTrip()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test"));
            var body = Encoding.UTF8.GetBytes("Hello TurboHTTP");
            var response = new UHttpResponse(HttpStatusCode.OK, new HttpHeaders(), body, TimeSpan.Zero, request);

            Assert.AreEqual("Hello TurboHTTP", response.GetBodyAsString());
        }

        [Test]
        public void UHttpResponse_GetBodyAsString_EmptyOrNullBody_ReturnsNull()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test"));
            var withNull = new UHttpResponse(HttpStatusCode.OK, new HttpHeaders(), null, TimeSpan.Zero, request);
            var withEmpty = new UHttpResponse(HttpStatusCode.OK, new HttpHeaders(), Array.Empty<byte>(), TimeSpan.Zero, request);

            Assert.IsNull(withNull.GetBodyAsString());
            Assert.IsNull(withEmpty.GetBodyAsString());
        }

        [Test]
        public void UHttpResponse_EnsureSuccessStatusCode_WithHttpError_ThrowsUHttpException()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test"));
            var response = new UHttpResponse(HttpStatusCode.BadRequest, new HttpHeaders(), null, TimeSpan.Zero, request);

            var ex = Assert.Throws<UHttpException>(() => response.EnsureSuccessStatusCode());
            Assert.AreEqual(UHttpErrorType.HttpError, ex.HttpError.Type);
            Assert.AreEqual(HttpStatusCode.BadRequest, ex.HttpError.StatusCode);
        }

        [Test]
        public void UHttpResponse_EnsureSuccessStatusCode_WithExistingError_ThrowsOriginalError()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test"));
            var responseError = new UHttpError(UHttpErrorType.Timeout, "timed out");
            var response = new UHttpResponse(
                HttpStatusCode.RequestTimeout,
                new HttpHeaders(),
                null,
                TimeSpan.Zero,
                request,
                responseError);

            var ex = Assert.Throws<UHttpException>(() => response.EnsureSuccessStatusCode());
            Assert.AreEqual(UHttpErrorType.Timeout, ex.HttpError.Type);
            Assert.AreEqual("timed out", ex.HttpError.Message);
        }

        [Test]
        public void UHttpResponse_Constructor_NullRequest_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new UHttpResponse(HttpStatusCode.OK, new HttpHeaders(), null, TimeSpan.Zero, null));
        }

        [Test]
        public void HttpHeaders_Set_And_Add_RejectInvalidNames()
        {
            var headers = new HttpHeaders();

            Assert.Throws<ArgumentException>(() => headers.Set(null, "value"));
            Assert.Throws<ArgumentException>(() => headers.Set(" ", "value"));
            Assert.Throws<ArgumentException>(() => headers.Add(null, "value"));
            Assert.Throws<ArgumentException>(() => headers.Add(" ", "value"));
        }

        [Test]
        public void RequestBuilder_WithHeader_CRLFInjection_ThrowsArgumentException()
        {
            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = new TurboHTTP.Testing.MockTransport(),
                DisposeTransport = true
            });

            Assert.Throws<ArgumentException>(() =>
                client.Get("https://example.test").WithHeader("X-Test", "a\r\nb"));
            Assert.Throws<ArgumentException>(() =>
                client.Get("https://example.test").WithHeader("X\r\nTest", "value"));
        }
    }
}
