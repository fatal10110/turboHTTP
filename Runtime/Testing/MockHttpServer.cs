using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Testing
{
    /// <summary>
    /// In-process mock server for deterministic runtime tests.
    /// </summary>
    public sealed class MockHttpServer
    {
        private const int RegexCacheHardLimit = 512;
        private static readonly ConcurrentDictionary<string, Regex> s_regexCache =
            new ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);
        private readonly object _lock = new object();
        private readonly List<MockRoute> _routes = new List<MockRoute>();
        private readonly Queue<MockHistoryEntry> _history = new Queue<MockHistoryEntry>();
        private readonly int _historyCapacity;
        private long _nextRouteOrder;

        public MockHttpServer(int historyCapacity = 256)
        {
            if (historyCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(historyCapacity), "History capacity must be > 0.");

            _historyCapacity = historyCapacity;
        }

        public MockRouteBuilder When(HttpMethod method, string pathPattern)
        {
            if (string.IsNullOrWhiteSpace(pathPattern))
                throw new ArgumentException("Path pattern is required.", nameof(pathPattern));

            var route = new MockRoute
            {
                Method = method,
                PathPattern = pathPattern,
                Handler = _ => new ValueTask<MockResponse>(new MockResponse()),
                Priority = 0,
                RemainingInvocations = null,
                RouteId = "route-" + Guid.NewGuid().ToString("N")
            };

            lock (_lock)
            {
                route.RegistrationOrder = _nextRouteOrder++;
                _routes.Add(route);
            }

            return new MockRouteBuilder(route);
        }

        public bool RemoveRoute(string routeId)
        {
            if (string.IsNullOrWhiteSpace(routeId))
                return false;

            lock (_lock)
            {
                for (int i = 0; i < _routes.Count; i++)
                {
                    if (string.Equals(_routes[i].RouteId, routeId, StringComparison.OrdinalIgnoreCase))
                    {
                        _routes.RemoveAt(i);
                        return true;
                    }
                }
            }

            return false;
        }

        public void ClearRoutes()
        {
            lock (_lock)
            {
                _routes.Clear();
            }
        }

        public IReadOnlyList<MockHistoryEntry> GetHistory()
        {
            lock (_lock)
            {
                return _history.ToArray();
            }
        }

        public void ClearHistory()
        {
            lock (_lock)
            {
                _history.Clear();
            }
        }

        public IHttpTransport CreateTransport()
        {
            return new MockTransport(DispatchAsync);
        }

        public async Task<UHttpResponse> DispatchAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (context == null) throw new ArgumentNullException(nameof(context));

            cancellationToken.ThrowIfCancellationRequested();

            var started = Stopwatch.StartNew();
            var route = TryMatchRoute(request, context, cancellationToken);
            var matchedRouteId = route?.RouteId;

            try
            {
                MockResponse responseModel;
                if (route == null)
                {
                    responseModel = new MockResponse
                    {
                        StatusCode = 404,
                        Headers = new HttpHeaders(),
                        Body = Encoding.UTF8.GetBytes("No mock route matched request.")
                    };
                }
                else
                {
                    var mockContext = new MockRequestContext(request, context, cancellationToken);
                    responseModel = await route.Handler(mockContext);
                    if (responseModel == null)
                        responseModel = new MockResponse();
                }

                if (responseModel.Delay > TimeSpan.Zero)
                    await Task.Delay(responseModel.Delay, cancellationToken);

                var response = new UHttpResponse(
                    statusCode: (HttpStatusCode)responseModel.StatusCode,
                    headers: responseModel.Headers?.Clone() ?? new HttpHeaders(),
                    body: responseModel.Body != null ? (byte[])responseModel.Body.Clone() : Array.Empty<byte>(),
                    elapsedTime: context.Elapsed,
                    request: request);

                started.Stop();
                RecordHistory(request, matchedRouteId, (int)response.StatusCode, started.Elapsed);
                return response;
            }
            catch (OperationCanceledException)
            {
                started.Stop();
                RecordHistory(request, matchedRouteId, 499, started.Elapsed);
                throw;
            }
            catch (Exception ex)
            {
                started.Stop();
                RecordHistory(request, matchedRouteId, 500, started.Elapsed);

                return new UHttpResponse(
                    statusCode: HttpStatusCode.InternalServerError,
                    headers: new HttpHeaders(),
                    body: Encoding.UTF8.GetBytes("Mock route failed: " + ex.Message),
                    elapsedTime: context.Elapsed,
                    request: request,
                    error: new UHttpError(UHttpErrorType.Unknown, ex.Message, ex, HttpStatusCode.InternalServerError));
            }
        }

        public void AssertReceived(string path, int count)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

            int actual;
            lock (_lock)
            {
                actual = _history.Count(entry =>
                    string.Equals(entry.Path, path, StringComparison.Ordinal));
            }

            if (actual != count)
            {
                throw new InvalidOperationException(
                    $"Expected {count} request(s) for path '{path}', but got {actual}.");
            }
        }

        public void AssertNoUnexpectedRequests()
        {
            lock (_lock)
            {
                foreach (var entry in _history)
                {
                    if (string.IsNullOrEmpty(entry.RouteId))
                    {
                        throw new InvalidOperationException(
                            $"Unexpected request: {entry.Method} {entry.Path}.");
                    }
                }
            }
        }

        public void AssertLastRequest(Func<MockHistoryEntry, bool> predicate, string failureMessage = null)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            MockHistoryEntry last;
            lock (_lock)
            {
                if (_history.Count <= 0)
                    throw new InvalidOperationException("No request history available.");

                last = default;
                foreach (var entry in _history)
                    last = entry;
            }

            if (!predicate(last))
            {
                throw new InvalidOperationException(
                    failureMessage ??
                    $"Last request assertion failed for {last.Method} {last.Path}.");
            }
        }

        private MockRoute TryMatchRoute(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            MockRoute best = null;
            int bestPriority = int.MinValue;
            long bestOrder = long.MaxValue;

            var evalContext = new MockRequestContext(request, context, cancellationToken);

            lock (_lock)
            {
                for (int i = 0; i < _routes.Count; i++)
                {
                    var route = _routes[i];
                    if (route == null)
                        continue;
                    if (route.Method != request.Method)
                        continue;
                    if (!PathMatches(route.PathPattern, request.Uri.AbsolutePath))
                        continue;

                    if (route.RemainingInvocations.HasValue && route.RemainingInvocations.Value <= 0)
                        continue;

                    var matched = true;
                    for (int matcherIndex = 0; matcherIndex < route.Matchers.Count; matcherIndex++)
                    {
                        if (!route.Matchers[matcherIndex](evalContext))
                        {
                            matched = false;
                            break;
                        }
                    }

                    if (!matched)
                        continue;

                    if (route.Priority > bestPriority ||
                        (route.Priority == bestPriority && route.RegistrationOrder < bestOrder))
                    {
                        best = route;
                        bestPriority = route.Priority;
                        bestOrder = route.RegistrationOrder;
                    }
                }

                if (best != null && best.RemainingInvocations.HasValue)
                {
                    best.RemainingInvocations = best.RemainingInvocations.Value - 1;
                }
            }

            return best;
        }

        private void RecordHistory(
            UHttpRequest request,
            string routeId,
            int responseStatusCode,
            TimeSpan duration)
        {
            var entry = new MockHistoryEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Method = request.Method,
                Path = request.Uri.AbsolutePath,
                RequestHeaders = request.Headers.Clone(),
                RequestBody = request.Body != null ? (byte[])request.Body.Clone() : Array.Empty<byte>(),
                RouteId = routeId,
                ResponseStatusCode = responseStatusCode,
                Duration = duration
            };

            lock (_lock)
            {
                _history.Enqueue(entry);
                while (_history.Count > _historyCapacity)
                    _history.Dequeue();
            }
        }

        private static bool PathMatches(string pattern, string path)
        {
            if (string.Equals(pattern, path, StringComparison.Ordinal))
                return true;

            if (pattern.StartsWith("regex:", StringComparison.Ordinal))
            {
                var regexPattern = pattern.Substring("regex:".Length);
                return GetCachedRegex(regexPattern).IsMatch(path);
            }

            var patternSegments = SplitPath(pattern);
            var pathSegments = SplitPath(path);

            if (patternSegments.Length != pathSegments.Length)
                return false;

            for (int i = 0; i < patternSegments.Length; i++)
            {
                var token = patternSegments[i];
                if (token == "*" ||
                    (token.Length >= 2 && token[0] == '{' && token[token.Length - 1] == '}'))
                {
                    continue;
                }

                if (!string.Equals(token, pathSegments[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static string[] SplitPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                return Array.Empty<string>();

            return path.Trim('/').Split('/');
        }

        private static Regex GetCachedRegex(string pattern)
        {
            if (s_regexCache.Count > RegexCacheHardLimit)
                s_regexCache.Clear();

            return s_regexCache.GetOrAdd(
                pattern,
                key => new Regex(key, RegexOptions.CultureInvariant));
        }

        public sealed class MockRouteBuilder
        {
            private readonly MockRoute _route;

            internal MockRouteBuilder(MockRoute route)
            {
                _route = route ?? throw new ArgumentNullException(nameof(route));
            }

            public string RouteId => _route.RouteId;

            public MockRouteBuilder Priority(int value)
            {
                _route.Priority = value;
                return this;
            }

            public MockRouteBuilder Times(int count)
            {
                if (count <= 0)
                    throw new ArgumentOutOfRangeException(nameof(count), "Times must be > 0.");
                _route.RemainingInvocations = count;
                return this;
            }

            public MockRouteBuilder WithHeader(string name, Func<string, bool> predicate)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("Header name is required.", nameof(name));
                if (predicate == null) throw new ArgumentNullException(nameof(predicate));

                _route.Matchers.Add(ctx =>
                {
                    var value = ctx.Headers.Get(name);
                    return value != null && predicate(value);
                });
                return this;
            }

            public MockRouteBuilder WithQuery(string name, Func<string, bool> predicate)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("Query name is required.", nameof(name));
                if (predicate == null) throw new ArgumentNullException(nameof(predicate));

                _route.Matchers.Add(ctx =>
                {
                    var value = GetQueryParameter(ctx.Query, name);
                    return value != null && predicate(value);
                });
                return this;
            }

            public MockRouteBuilder WithBody(Func<byte[], bool> predicate)
            {
                if (predicate == null) throw new ArgumentNullException(nameof(predicate));
                _route.Matchers.Add(ctx => predicate(ctx.Request.Body ?? Array.Empty<byte>()));
                return this;
            }

            public MockRouteBuilder WithJsonBody<T>(Func<T, bool> predicate)
            {
                if (predicate == null) throw new ArgumentNullException(nameof(predicate));

                _route.Matchers.Add(ctx =>
                {
                    var body = ctx.Request.Body;
                    if (body == null || body.Length == 0)
                        return false;

                    try
                    {
                        var value = (T)DeserializeViaProjectJson(Encoding.UTF8.GetString(body), typeof(T));
                        return predicate(value);
                    }
                    catch
                    {
                        return false;
                    }
                });

                return this;
            }

            public MockRouteBuilder Respond(Func<MockResponseBuilder, MockResponseBuilder> configure)
            {
                if (configure == null) throw new ArgumentNullException(nameof(configure));
                _route.Handler = _ =>
                {
                    var builder = configure(new MockResponseBuilder());
                    var response = (builder ?? new MockResponseBuilder()).Build();
                    return new ValueTask<MockResponse>(response);
                };
                return this;
            }

            public MockRouteBuilder Respond(Func<MockRequestContext, ValueTask<MockResponse>> handler)
            {
                _route.Handler = handler ?? throw new ArgumentNullException(nameof(handler));
                return this;
            }

            public MockRouteBuilder RespondSequence(params Func<MockResponseBuilder, MockResponseBuilder>[] sequence)
            {
                if (sequence == null || sequence.Length == 0)
                    throw new ArgumentException("Sequence must include at least one response.", nameof(sequence));

                var responses = new Queue<MockResponse>(sequence.Length);
                for (int i = 0; i < sequence.Length; i++)
                {
                    if (sequence[i] == null)
                        throw new ArgumentException("Sequence contains null response builder.", nameof(sequence));
                    var response = sequence[i](new MockResponseBuilder())?.Build() ?? new MockResponseBuilder().Build();
                    responses.Enqueue(response);
                }

                var gate = new object();
                _route.Handler = _ =>
                {
                    lock (gate)
                    {
                        if (responses.Count == 0)
                        {
                            return new ValueTask<MockResponse>(new MockResponse
                            {
                                StatusCode = 410,
                                Body = Encoding.UTF8.GetBytes("Mock response sequence exhausted.")
                            });
                        }

                        var next = responses.Dequeue();
                        return new ValueTask<MockResponse>(new MockResponse
                        {
                            StatusCode = next.StatusCode,
                            Headers = next.Headers?.Clone() ?? new HttpHeaders(),
                            Body = next.Body != null ? (byte[])next.Body.Clone() : null,
                            Delay = next.Delay
                        });
                    }
                };

                return this;
            }

            private static string GetQueryParameter(string query, string name)
            {
                if (string.IsNullOrEmpty(query))
                    return null;

                var trimmed = query[0] == '?' ? query.Substring(1) : query;
                var pairs = trimmed.Split('&');
                for (int i = 0; i < pairs.Length; i++)
                {
                    if (string.IsNullOrEmpty(pairs[i]))
                        continue;

                    var kv = pairs[i].Split(new[] { '=' }, 2);
                    var key = Uri.UnescapeDataString(kv[0]);
                    if (!string.Equals(key, name, StringComparison.Ordinal))
                        continue;

                    if (kv.Length == 1)
                        return string.Empty;
                    return Uri.UnescapeDataString(kv[1]);
                }

                return null;
            }

            private static object DeserializeViaProjectJson(string json, Type payloadType)
            {
                return ProjectJsonBridge.Deserialize(
                    json,
                    payloadType,
                    requiredBy: "MockHttpServer.WithJsonBody(...)");
            }
        }
    }
}
