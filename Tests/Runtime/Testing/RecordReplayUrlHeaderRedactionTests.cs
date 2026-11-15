using System;
using System.IO;
using System.Net;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Testing
{
    /// <summary>
    /// #1067: recordings redact user-info and sensitive query values inside URL-valued headers
    /// (Location, Content-Location, Referer, Link, Refresh), and a replayed redirect keeps the
    /// non-sensitive parameters.
    /// </summary>
    [TestFixture]
    public class RecordReplayUrlHeaderRedactionTests
    {
        private const string SignatureSecret = "AMZSIG-SECRET-0d7";
        private const string CodeSecret = "CODE-SECRET-e15";
        private const string RefererSecret = "REFERER-SECRET-a3f";
        private const string PasswordSecret = "USERINFO-SECRET-b62";

        private string _path;

        [SetUp]
        public void SetUp()
        {
            JsonModuleRegistration.EnsureRegistered();
            _path = Path.Combine(
                TestTempPath.Root,
                "turbohttp-rr-urlheaders-" + Guid.NewGuid().ToString("N") + ".json");
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }

        [Test]
        public void Redirect_LocationAndLinkSecrets_AreNotRecorded_AndReplayKeepsOtherParameters()
        {
            var responseHeaders = new HttpHeaders();
            responseHeaders.Set(
                "Location",
                "https://cdn.example.test/obj?X-Amz-Date=20260101T000000Z&X-Amz-Signature=" + SignatureSecret);
            responseHeaders.Set("Content-Location", "https://user:" + PasswordSecret + "@cdn.example.test/obj");
            responseHeaders.Set("Link", "<https://auth.example.test/cb?code=" + CodeSecret + "&state=s1>; rel=\"next\"");
            responseHeaders.Set("Refresh", "0; url=https://auth.example.test/cb?code=" + CodeSecret);
            var requestHeaders = new HttpHeaders();
            requestHeaders.Set("Referer", "https://app.example.test/page?token=" + RefererSecret + "&tab=2");

            var inner = new MockTransport();
            inner.EnqueueResponse(HttpStatusCode.Found, responseHeaders, null);
            using (var recorder = new RecordReplayTransport(
                       inner,
                       new RecordReplayTransportOptions { Mode = RecordReplayMode.Record, RecordingPath = _path }))
            {
                var recorded = Send(recorder, requestHeaders);
                Assert.AreEqual(HttpStatusCode.Found, recorded.StatusCode);
                StringAssert.Contains(SignatureSecret, recorded.Headers.Get("Location"), "the caller gets the real value");
                recorder.SaveRecordings();
            }

            var text = File.ReadAllText(_path);
            foreach (var secret in new[] { SignatureSecret, CodeSecret, RefererSecret, PasswordSecret })
                Assert.That(text, Does.Not.Contain(secret), "recording contains " + secret);
            StringAssert.Contains("X-Amz-Date=20260101T000000Z", text);
            StringAssert.Contains("tab=2", text);

            using (var replay = new RecordReplayTransport(
                       innerTransport: null,
                       new RecordReplayTransportOptions { Mode = RecordReplayMode.Replay, RecordingPath = _path }))
            {
                var replayed = Send(replay, requestHeaders);
                Assert.AreEqual(HttpStatusCode.Found, replayed.StatusCode);
                Assert.AreEqual(
                    "https://cdn.example.test/obj?X-Amz-Date=20260101T000000Z&X-Amz-Signature=[REDACTED]",
                    replayed.Headers.Get("Location"));
                Assert.AreEqual(
                    "<https://auth.example.test/cb?code=[REDACTED]&state=s1>; rel=\"next\"",
                    replayed.Headers.Get("Link"));
                Assert.AreEqual("https://[REDACTED]@cdn.example.test/obj", replayed.Headers.Get("Content-Location"));
                Assert.AreEqual("0; url=https://auth.example.test/cb?code=[REDACTED]", replayed.Headers.Get("Refresh"));
            }
        }

        private static UHttpResponse Send(RecordReplayTransport transport, HttpHeaders requestHeaders)
        {
            UHttpResponse response = null;
            AssertAsync.Run(async () =>
            {
                var pipeline = new TestInterceptorPipeline(Array.Empty<IHttpInterceptor>(), transport);
                var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("https://api.example.test/download"),
                    requestHeaders.Clone());
                response = await pipeline.ExecuteAsync(request, new RequestContext(request));
            });

            return response;
        }
    }
}
