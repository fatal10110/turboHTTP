using System;
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
    /// Example of advanced features working together:
    /// - Retry logic
    /// - Caching
    /// - Rate limiting
    /// - Metrics/Observability
    /// </summary>
    public class AdvancedFeaturesExample : MonoBehaviour
    {
        private UHttpClient _client;
        private MetricsMiddleware _metrics;

        void Start()
        {
            var options = new UHttpClientOptions
            {
                BaseUrl = "https://httpbin.org"
            };

            // 1. Retry Policy
            var retryPolicy = new RetryPolicy
            {
                MaxRetries = 3,
                InitialDelay = TimeSpan.FromSeconds(0.5),
                BackoffMultiplier = 2.0,
                JitterFactor = 0.1,
                OnlyRetryIdempotent = true // GET/HEAD/PUT/DELETE
            };
            options.Middlewares.Add(new RetryMiddleware(retryPolicy));

            // 2. Cache Policy
            var cachePolicy = new CachePolicy
            {
                EnableCache = true,
                DefaultTtl = TimeSpan.FromSeconds(10), // Short TTL for demo
                EnableRevalidation = true
            };
            options.Middlewares.Add(new CacheMiddleware(cachePolicy));

            // 3. Rate Limit Policy
            var rateLimitPolicy = new RateLimitPolicy
            {
                MaxRequests = 5,
                TimeWindow = TimeSpan.FromSeconds(10),
                PerHost = true
            };
            options.Middlewares.Add(new RateLimitMiddleware(rateLimitPolicy));

            // 4. Metrics
            _metrics = new MetricsMiddleware();
            options.Middlewares.Add(_metrics);

            _client = new UHttpClient(options);

            RunExamples();
        }

        async void RunExamples()
        {
            await Example1_Caching();
            await Example2_Retries();
            await Example3_RateLimiting();
            
            PrintMetrics();
        }

        async Task Example1_Caching()
        {
            Debug.Log("=== Example 1: Caching ===");

            Debug.Log("Request 1 (Network)...");
            var r1 = await _client.Get("/get").SendAsync();
            Debug.Log($"Status: {r1.StatusCode}, Time: {r1.ElapsedTime.TotalMilliseconds}ms");

            Debug.Log("Request 2 (Cache)...");
            var r2 = await _client.Get("/get").SendAsync();
            Debug.Log($"Status: {r2.StatusCode}, Time: {r2.ElapsedTime.TotalMilliseconds}ms");
            
            if (r2.ElapsedTime.TotalMilliseconds < 10)
            {
                Debug.Log("Request served from cache instantly!");
            }
        }

        async Task Example2_Retries()
        {
            Debug.Log("=== Example 2: Retries ===");

            try
            {
                // Request a 503 Service Unavailable which triggers retry
                // Note: httpbin might not reliably accept /status/503 for retries if implementation expects specific errors
                // But let's assume standard behavior.
                var response = await _client.Get("/status/503").WithTimeout(TimeSpan.FromSeconds(2)).SendAsync();
                
                Debug.Log($"Final Status: {response.StatusCode}");
            }
            catch (UHttpException ex)
            {
                Debug.Log($"Request failed after retries: {ex.Message}");
            }
        }

        async Task Example3_RateLimiting()
        {
            Debug.Log("=== Example 3: Rate Limiting ===");

            for (int i = 0; i < 7; i++)
            {
                try
                {
                    await _client.Get("/get").SendAsync();
                    Debug.Log($"Request {i+1} success");
                }
                catch (UHttpException ex) when (ex.HttpError.Type == UHttpErrorType.HttpError && ex.HttpError.Code == (System.Net.HttpStatusCode)429)
                {
                    Debug.Log($"Request {i+1} rate limited (429)!");
                }
                catch (Exception ex)
                {
                    Debug.Log($"Request {i+1} failed: {ex.Message}");
                }
            }
        }

        void PrintMetrics()
        {
            Debug.Log("=== Metrics ===");
            var m = _metrics.Metrics;
            Debug.Log($"Total Requests: {m.TotalRequests}");
            Debug.Log($"Successful: {m.SuccessfulRequests}");
            Debug.Log($"Failed: {m.FailedRequests}");
            Debug.Log($"Cached: {m.CachedRequests}");
            Debug.Log($"Retried: {m.RetriedRequests}");
        }

        void OnDestroy()
        {
            _client?.Dispose();
        }
    }
}
