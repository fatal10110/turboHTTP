using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests
{
    public static class TestHelpers
    {
        public static (UHttpClient Client, MockTransport Transport) CreateMockClient(
            HttpStatusCode statusCode = HttpStatusCode.OK,
            HttpHeaders responseHeaders = null,
            byte[] responseBody = null,
            UHttpError responseError = null,
            IEnumerable<IHttpMiddleware> middlewares = null)
        {
            var transport = new MockTransport(
                statusCode,
                responseHeaders,
                responseBody,
                responseError);

            var options = new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            };

            if (middlewares != null)
            {
                options.Middlewares = new List<IHttpMiddleware>(middlewares);
            }

            return (new UHttpClient(options), transport);
        }

        public static UHttpRequest CreateRequest(
            HttpMethod method = HttpMethod.GET,
            string url = "https://example.test/resource",
            HttpHeaders headers = null,
            byte[] body = null,
            TimeSpan? timeout = null,
            IReadOnlyDictionary<string, object> metadata = null)
        {
            return new UHttpRequest(
                method,
                new Uri(url),
                headers ?? new HttpHeaders(),
                body,
                timeout ?? TimeSpan.FromSeconds(30),
                metadata);
        }

        public static async Task AssertCompletesWithinAsync(
            Task task,
            TimeSpan timeout,
            string failureMessage = null,
            CancellationToken cancellationToken = default)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than zero.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeoutTask = Task.Delay(timeout, timeoutCts.Token);
            var completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
            if (completed != task)
            {
                throw new AssertionException(
                    failureMessage ??
                    $"Expected task to complete within {timeout.TotalMilliseconds:F0}ms.");
            }

            timeoutCts.Cancel();
            await task.ConfigureAwait(false);
        }

        public static async Task<T> AssertCompletesWithinAsync<T>(
            Task<T> task,
            TimeSpan timeout,
            string failureMessage = null,
            CancellationToken cancellationToken = default)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than zero.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeoutTask = Task.Delay(timeout, timeoutCts.Token);
            var completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
            if (completed != task)
            {
                throw new AssertionException(
                    failureMessage ??
                    $"Expected task to complete within {timeout.TotalMilliseconds:F0}ms.");
            }

            timeoutCts.Cancel();
            return await task.ConfigureAwait(false);
        }

        public static async Task<TException> AssertThrowsAsync<TException>(
            Func<Task> action,
            Func<TException, bool> predicate = null,
            string details = null)
            where TException : Exception
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            try
            {
                await action().ConfigureAwait(false);
            }
            catch (TException expected)
            {
                if (predicate != null && !predicate(expected))
                {
                    throw new AssertionException(
                        details ??
                        $"Exception predicate failed for {typeof(TException).Name}: {expected.Message}");
                }

                return expected;
            }
            catch (Exception ex)
            {
                throw new AssertionException(
                    $"Expected exception {typeof(TException).Name} but got {ex.GetType().Name}: {ex.Message}",
                    ex);
            }

            throw new AssertionException(
                details ??
                $"Expected exception {typeof(TException).Name} but no exception was thrown.");
        }
    }
}
