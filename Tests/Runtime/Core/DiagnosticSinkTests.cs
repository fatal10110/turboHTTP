using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Observability;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// Regression tests for #180: swallowed failures reach the diagnostic sink (they went to
    /// Debug.WriteLine, compiled out of release builds), the sink is replaceable and messages carry
    /// exception types only.
    /// </summary>
    [TestFixture]
    public class DiagnosticSinkTests
    {
        private const string SecretText = "token=s3cr3t";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private List<string> _messages;

        [SetUp]
        public void SetUp()
        {
            _messages = new List<string>();
            TurboDiagnostics.Reset();
            TurboDiagnostics.SetSink(Capture);
        }

        [TearDown]
        public void TearDown()
        {
            TurboDiagnostics.Reset();
        }

        [Test]
        public void FailingCacheStore_ReportsToSinkWithoutExceptionText()
        {
            AssertAsync.Run(async () =>
            {
                var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                TurboDiagnostics.SetSink(message =>
                {
                    Capture(message);
                    if (message.Contains("[TurboHTTP][Cache]"))
                        reported.TrySetResult(message);
                });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new MockTransport(HttpStatusCode.OK, headers, Encoding.UTF8.GetBytes("payload")),
                    DisposeTransport = true,
                    FollowRedirects = false,
                    Interceptors = { new CacheInterceptor(new CachePolicy { Storage = new ThrowingSetStorage() }) }
                });

                using (var response = await client.Get("https://example.test/cacheable").SendBufferedAsync())
                    Assert.AreEqual("payload", response.GetBodyAsString(), "a store failure never fails the request");

                var message = await TestHelpers.AssertCompletesWithinAsync(
                    reported.Task, Timeout, "the cache store failure never reached the sink");
                StringAssert.Contains(nameof(InvalidOperationException), message);
                StringAssert.DoesNotContain(SecretText, message);
            });
        }

        [Test]
        public void FailingTransportDispose_ReportsToSink()
        {
            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = new ThrowingDisposeTransport(),
                DisposeTransport = true,
                FollowRedirects = false
            });

            Assert.DoesNotThrow(client.Dispose);

            var message = Single("Transport dispose failed");
            StringAssert.Contains(nameof(InvalidOperationException), message);
            StringAssert.DoesNotContain(SecretText, message);
        }

        [Test]
        public void ThrowingSink_NeverBreaksTheReportingPath()
        {
            TurboDiagnostics.SetSink(_ => throw new InvalidOperationException("sink failure"));
            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = new ThrowingDisposeTransport(),
                DisposeTransport = true,
                FollowRedirects = false
            });

            Assert.DoesNotThrow(client.Dispose);
        }

        [Test]
        public void NullSink_Silences()
        {
            TurboDiagnostics.SetSink(null);
            Assert.DoesNotThrow(() => TurboDiagnostics.Log("dropped"));
            Assert.AreEqual(0, _messages.Count);
        }

        [Test]
        public void ObservabilitySink_IsPublicAndResettable()
        {
            var received = new List<string>();
            TurboHttpDiagnostics.SetSink(received.Add);
            TurboDiagnostics.Log("through the public sink");
            Assert.AreEqual(new[] { "through the public sink" }, received.ToArray());

            TurboHttpDiagnostics.ResetSink();
            TurboDiagnostics.SetSink(Capture);
            TurboDiagnostics.Log("after reset");
            Assert.AreEqual(1, received.Count, "the replaced sink no longer receives messages");
        }

        [Test]
        public void LogOnce_WritesOnlyTheFirstTime()
        {
            Assert.IsTrue(TurboDiagnostics.LogOnce(TurboDiagnostics.OnceFollowRedirectsWithoutInterceptor, "once"));
            Assert.IsFalse(TurboDiagnostics.LogOnce(TurboDiagnostics.OnceFollowRedirectsWithoutInterceptor, "once"));
            Assert.AreEqual(1, _messages.Count);
        }

        private void Capture(string message)
        {
            lock (_messages)
                _messages.Add(message);
        }

        private string Single(string fragment)
        {
            lock (_messages)
            {
                string found = null;
                foreach (var message in _messages)
                {
                    if (!message.Contains(fragment))
                        continue;
                    Assert.IsNull(found, "reported more than once: " + fragment);
                    found = message;
                }

                Assert.IsNotNull(found, "not reported: " + fragment);
                return found;
            }
        }

        private sealed class ThrowingSetStorage : ICacheStorage
        {
            private readonly MemoryCacheStorage _inner = new MemoryCacheStorage();

            public Task<CacheEntry> GetAsync(string key, CancellationToken cancellationToken = default)
                => _inner.GetAsync(key, cancellationToken);

            public Task SetAsync(string key, CacheEntry entry, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("store failed for /private?" + SecretText);

            public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
                => _inner.RemoveAsync(key, cancellationToken);

            public Task ClearAsync(CancellationToken cancellationToken = default)
                => _inner.ClearAsync(cancellationToken);

            public Task<int> GetCountAsync(CancellationToken cancellationToken = default)
                => _inner.GetCountAsync(cancellationToken);

            public Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
                => _inner.GetSizeAsync(cancellationToken);
        }

        private sealed class ThrowingDisposeTransport : IHttpTransport
        {
            private readonly MockTransport _inner = new MockTransport();

            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
                => _inner.DispatchAsync(request, handler, context, cancellationToken);

            public void Dispose()
            {
                _inner.Dispose();
                throw new InvalidOperationException("dispose failed for /private?" + SecretText);
            }
        }
    }
}
