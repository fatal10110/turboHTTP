using System;
using System.Linq;
using System.Threading.Tasks;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Observability;
using TurboHTTP.RateLimit;
using TurboHTTP.Retry;
using UnityEngine;

namespace TurboHTTP.Samples.AdvancedFeatures
{
    /// <summary>
    /// Example of advanced features working together as interceptors:
    /// - Metrics/Observability
    /// - Redirects
    /// - Caching
    /// - Retry logic
    /// - Concurrency limits
    /// The order below is the canonical interceptor order (outermost first); see
    /// Documentation~/Modules/Interceptors.md.
    /// </summary>
    public class AdvancedFeaturesExample : MonoBehaviour
    {
        private UHttpClient _client;
        private MetricsInterceptor _metrics;

        void Start()
        {
            var options = new UHttpClientOptions
            {
                BaseUrl = "https://httpbin.org"
            };

            // 1. Metrics: outermost, sees every request as the caller sent it.
            _metrics = new MetricsInterceptor();
            options.Interceptors.Add(_metrics);

            // 2. Redirects: FollowRedirects/MaxRedirects only take effect with this interceptor.
            options.Interceptors.Add(new RedirectInterceptor());

            // 3. Cache: honours Cache-Control, ETag and Last-Modified (RFC 9111).
            options.Interceptors.Add(new CacheInterceptor(new CachePolicy
            {
                EnableRevalidation = true
            }));

            // 4. Retry: exponential backoff for idempotent requests; below the cache so that
            //    cache hits are never retried.
            options.Interceptors.Add(new RetryInterceptor(new RetryPolicy
            {
                MaxRetries = 3,
                InitialDelay = TimeSpan.FromSeconds(0.5),
                BackoffMultiplier = 2.0,
                OnlyRetryIdempotent = true // GET/HEAD/PUT/DELETE/OPTIONS
            }));

            // 5. Concurrency: innermost, so a permit is held only while a request is on the wire.
            options.Interceptors.Add(new ConcurrencyInterceptor(maxConnectionsPerHost: 2, maxTotalConnections: 8));

            _client = new UHttpClient(options);

            RunExamples();
        }

        async void RunExamples()
        {
            await Example1_Caching();
            await Example2_Retries();
            await Example3_ConcurrencyLimit();

            PrintMetrics();
        }

        async Task Example1_Caching()
        {
            Debug.Log("=== Example 1: Caching ===");

            // httpbin.org/cache/60 answers with Cache-Control: public, max-age=60.
            Debug.Log("Request 1 (network)...");
            using var r1 = await _client.Get("/cache/60").SendBufferedAsync();
            Debug.Log($"Status: {r1.StatusCode}, Time: {r1.ElapsedTime.TotalMilliseconds}ms");

            Debug.Log("Request 2 (cache)...");
            using var r2 = await _client.Get("/cache/60").SendBufferedAsync();
            Debug.Log($"Status: {r2.StatusCode}, Time: {r2.ElapsedTime.TotalMilliseconds}ms");
        }

        async Task Example2_Retries()
        {
            Debug.Log("=== Example 2: Retries ===");

            try
            {
                // 503 is retryable: the interceptor retries with backoff before giving up.
                using var response = await _client.Get("/status/503")
                    .WithTimeout(TimeSpan.FromSeconds(5))
                    .SendBufferedAsync();

                Debug.Log($"Final status: {response.StatusCode}");
            }
            catch (UHttpException ex)
            {
                Debug.Log($"Request failed after retries: {ex.HttpError.Type}");
            }
        }

        async Task Example3_ConcurrencyLimit()
        {
            Debug.Log("=== Example 3: Concurrency limit ===");

            // Six requests start together, but at most two are on the wire to the host at a time.
            var tasks = Enumerable.Range(0, 6).Select(async i =>
            {
                using var response = await _client.Get("/delay/1").SendBufferedAsync();
                Debug.Log($"Request {i + 1}: {response.StatusCode}");
            });
            await Task.WhenAll(tasks);
        }

        void PrintMetrics()
        {
            Debug.Log("=== Metrics ===");
            var m = _metrics.Metrics;
            Debug.Log($"Total Requests: {m.TotalRequests}");
            Debug.Log($"Successful: {m.SuccessfulRequests}");
            Debug.Log($"Failed: {m.FailedRequests}");
            Debug.Log($"Average response time: {m.GetAverageResponseTimeMs():F0}ms");
        }

        void OnDestroy()
        {
            _client?.Dispose();
        }
    }
}
