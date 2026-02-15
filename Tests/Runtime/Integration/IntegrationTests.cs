using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;
using TurboHTTP.Tests;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Integration
{
    [TestFixture]
    public class IntegrationTests
    {
        [Test]
        public void Deterministic_GetFlow_WithMockTransport()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                transport.ClearQueuedResponses();
                transport.EnqueueResponse(
                    HttpStatusCode.OK,
                    headers: new HttpHeaders(),
                    body: Encoding.UTF8.GetBytes("ok"));

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var response = await client.Get("https://example.test/ping").SendAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("ok", response.GetBodyAsString());
                Assert.AreEqual(1, transport.RequestCount);
                Assert.AreEqual("https://example.test/ping", transport.LastRequest.Uri.ToString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Deterministic_JsonPostRoundTrip_WithMockTransport()
        {
            Task.Run(async () =>
            {
                byte[] echoedRequestBody = null;
                var transport = new MockTransport((request, context, ct) =>
                {
                    echoedRequestBody = request.Body;
                    var headers = new HttpHeaders();
                    headers.Set("Content-Type", "application/json");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        headers,
                        echoedRequestBody,
                        context.Elapsed,
                        request));
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var payload = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["name"] = "phase7",
                    ["count"] = 2
                };

                var response = await client.Post("https://example.test/echo")
                    .WithJsonBody(payload)
                    .SendAsync();

                var echoed = response.AsJson<System.Collections.Generic.Dictionary<string, object>>();
                Assert.IsNotNull(echoed);
                Assert.AreEqual("phase7", echoed["name"].ToString());
                Assert.AreEqual("2", echoed["count"].ToString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Deterministic_Http404_StatusHandling()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.NotFound);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var response = await client.Get("https://example.test/missing").SendAsync();

                Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
                Assert.IsFalse(response.IsSuccessStatusCode);
                Assert.Throws<UHttpException>(() => response.EnsureSuccessStatusCode());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Deterministic_MockTransport_CapturesRequestOrder()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                transport.ClearQueuedResponses();
                transport.EnqueueResponse(HttpStatusCode.OK);
                transport.EnqueueResponse(HttpStatusCode.OK);

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                await client.Get("https://example.test/first").SendAsync();
                await client.Get("https://example.test/second").SendAsync();

                var captured = transport.CapturedRequests;
                Assert.AreEqual(2, captured.Count);
                Assert.AreEqual("https://example.test/first", captured[0].Uri.ToString());
                Assert.AreEqual("https://example.test/second", captured[1].Uri.ToString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Deterministic_MockTransport_DelayHonorsCancellation()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                transport.ClearQueuedResponses();
                transport.EnqueueResponse(
                    HttpStatusCode.OK,
                    delay: TimeSpan.FromSeconds(5));

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await client.Get("https://example.test/slow").SendAsync(cts.Token);
                });
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Deterministic_MockTransport_JsonAndErrorHelpers()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                transport.ClearQueuedResponses();
                transport.EnqueueJsonResponse(new Dictionary<string, object>
                {
                    ["kind"] = "json"
                });
                transport.EnqueueError(
                    new UHttpError(UHttpErrorType.NetworkError, "simulated"),
                    statusCode: HttpStatusCode.ServiceUnavailable);

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var jsonResponse = await client.Get("https://example.test/json").SendAsync();
                var parsed = jsonResponse.AsJson<Dictionary<string, object>>();
                Assert.AreEqual("json", parsed["kind"].ToString());

                var errorResponse = await client.Get("https://example.test/error").SendAsync();
                Assert.IsTrue(errorResponse.IsError);
                Assert.AreEqual(UHttpErrorType.NetworkError, errorResponse.Error.Type);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, errorResponse.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Deterministic_RecordThenReplay_ReturnsEquivalentResponse()
        {
            Task.Run(async () =>
            {
                var recordingPath = Path.Combine(
                    Path.GetTempPath(),
                    "turbohttp-phase7-" + Guid.NewGuid().ToString("N") + ".json");

                try
                {
                    var innerTransport = new MockTransport();
                    innerTransport.ClearQueuedResponses();
                    innerTransport.EnqueueJsonResponse(new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["result"] = "recorded"
                    });

                    using (var recordTransport = new RecordReplayTransport(
                        innerTransport,
                        new RecordReplayTransportOptions
                        {
                            Mode = RecordReplayMode.Record,
                            RecordingPath = recordingPath
                        }))
                    using (var recordClient = new UHttpClient(new UHttpClientOptions
                    {
                        Transport = recordTransport,
                        DisposeTransport = true
                    }))
                    {
                        var recordedResponse = await recordClient.Get("https://example.test/replay").SendAsync();
                        Assert.AreEqual(HttpStatusCode.OK, recordedResponse.StatusCode);
                        recordTransport.SaveRecordings();
                    }

                    using var replayTransport = new RecordReplayTransport(
                        innerTransport: null,
                        new RecordReplayTransportOptions
                        {
                            Mode = RecordReplayMode.Replay,
                            RecordingPath = recordingPath
                        });
                    using var replayClient = new UHttpClient(new UHttpClientOptions
                    {
                        Transport = replayTransport,
                        DisposeTransport = true
                    });

                    var replayResponse = await replayClient.Get("https://example.test/replay").SendAsync();
                    var body = replayResponse.AsJson<System.Collections.Generic.Dictionary<string, object>>();

                    Assert.AreEqual(HttpStatusCode.OK, replayResponse.StatusCode);
                    Assert.AreEqual("recorded", body["result"].ToString());
                }
                finally
                {
                    try
                    {
                        if (File.Exists(recordingPath))
                            File.Delete(recordingPath);
                    }
                    catch
                    {
                    }
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Deterministic_ReplayStrictMismatch_Throws()
        {
            Task.Run(async () =>
            {
                var recordingPath = Path.Combine(
                    Path.GetTempPath(),
                    "turbohttp-phase7-mismatch-" + Guid.NewGuid().ToString("N") + ".json");

                try
                {
                    var innerTransport = new MockTransport();
                    innerTransport.ClearQueuedResponses();
                    innerTransport.EnqueueResponse(HttpStatusCode.OK, body: Encoding.UTF8.GetBytes("ok"));

                    using (var recordTransport = new RecordReplayTransport(
                        innerTransport,
                        new RecordReplayTransportOptions
                        {
                            Mode = RecordReplayMode.Record,
                            RecordingPath = recordingPath
                        }))
                    using (var recordClient = new UHttpClient(new UHttpClientOptions
                    {
                        Transport = recordTransport,
                        DisposeTransport = true
                    }))
                    {
                        await recordClient.Get("https://example.test/match").SendAsync();
                        recordTransport.SaveRecordings();
                    }

                    using var replayTransport = new RecordReplayTransport(
                        innerTransport: null,
                        new RecordReplayTransportOptions
                        {
                            Mode = RecordReplayMode.Replay,
                            RecordingPath = recordingPath,
                            MismatchPolicy = RecordReplayMismatchPolicy.Strict
                        });
                    using var replayClient = new UHttpClient(new UHttpClientOptions
                    {
                        Transport = replayTransport,
                        DisposeTransport = true
                    });

                    var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                    {
                        await replayClient.Get("https://example.test/not-matching").SendAsync();
                    });

                    StringAssert.Contains("No replay recording matched", ex.Message);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(recordingPath))
                            File.Delete(recordingPath);
                    }
                    catch
                    {
                    }
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Deterministic_RecordReplay_AppliesRedactionPolicy()
        {
            Task.Run(async () =>
            {
                var recordingPath = Path.Combine(
                    Path.GetTempPath(),
                    "turbohttp-phase7-redaction-" + Guid.NewGuid().ToString("N") + ".json");

                try
                {
                    var innerTransport = new MockTransport();
                    innerTransport.ClearQueuedResponses();
                    var responseHeaders = new HttpHeaders();
                    responseHeaders.Set("Content-Type", "application/json");
                    responseHeaders.Add("Set-Cookie", "session=secret-cookie");
                    var responseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                        new Dictionary<string, object> { ["token"] = "response-secret" }));
                    innerTransport.EnqueueResponse(
                        HttpStatusCode.OK,
                        responseHeaders,
                        responseBody);

                    var options = new RecordReplayTransportOptions
                    {
                        Mode = RecordReplayMode.Record,
                        RecordingPath = recordingPath
                    };

                    using (var recordTransport = new RecordReplayTransport(innerTransport, options))
                    using (var recordClient = new UHttpClient(new UHttpClientOptions
                    {
                        Transport = recordTransport,
                        DisposeTransport = true
                    }))
                    {
                        await recordClient.Get("https://example.test/data?api_key=very-secret")
                            .WithHeader("Authorization", "Bearer very-secret")
                            .SendAsync();
                        recordTransport.SaveRecordings();
                    }

                    var artifact = File.ReadAllText(recordingPath);
                    StringAssert.Contains("[REDACTED]", artifact);
                    StringAssert.DoesNotContain("very-secret", artifact);
                    StringAssert.DoesNotContain("response-secret", artifact);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(recordingPath))
                            File.Delete(recordingPath);
                    }
                    catch
                    {
                    }
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Deterministic_ReplayParallelRequests_ConsumeRecordedQueueSafely()
        {
            Task.Run(async () =>
            {
                const int sampleCount = 20;
                var recordingPath = Path.Combine(
                    Path.GetTempPath(),
                    "turbohttp-phase7-parallel-" + Guid.NewGuid().ToString("N") + ".json");

                try
                {
                    var innerTransport = new MockTransport();
                    innerTransport.ClearQueuedResponses();
                    for (int i = 0; i < sampleCount; i++)
                    {
                        innerTransport.EnqueueJsonResponse(new Dictionary<string, object>
                        {
                            ["index"] = i
                        });
                    }

                    using (var recordTransport = new RecordReplayTransport(
                        innerTransport,
                        new RecordReplayTransportOptions
                        {
                            Mode = RecordReplayMode.Record,
                            RecordingPath = recordingPath
                        }))
                    using (var recordClient = new UHttpClient(new UHttpClientOptions
                    {
                        Transport = recordTransport,
                        DisposeTransport = true
                    }))
                    {
                        for (int i = 0; i < sampleCount; i++)
                        {
                            await recordClient.Get("https://example.test/repeat").SendAsync();
                        }
                        recordTransport.SaveRecordings();
                    }

                    using var replayTransport = new RecordReplayTransport(
                        innerTransport: null,
                        new RecordReplayTransportOptions
                        {
                            Mode = RecordReplayMode.Replay,
                            RecordingPath = recordingPath
                        });
                    using var replayClient = new UHttpClient(new UHttpClientOptions
                    {
                        Transport = replayTransport,
                        DisposeTransport = true
                    });

                    var replayTasks = new Task<UHttpResponse>[sampleCount];
                    for (int i = 0; i < sampleCount; i++)
                    {
                        replayTasks[i] = replayClient.Get("https://example.test/repeat").SendAsync();
                    }

                    var responses = await Task.WhenAll(replayTasks);
                    var indices = new HashSet<int>();
                    for (int i = 0; i < responses.Length; i++)
                    {
                        var parsed = responses[i].AsJson<Dictionary<string, object>>();
                        indices.Add(Convert.ToInt32(parsed["index"]));
                    }

                    Assert.AreEqual(sampleCount, indices.Count);
                    Assert.IsTrue(indices.SetEquals(Enumerable.Range(0, sampleCount)));
                }
                finally
                {
                    try
                    {
                        if (File.Exists(recordingPath))
                            File.Delete(recordingPath);
                    }
                    catch
                    {
                    }
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Deterministic_Phase10_RedirectCookieCacheRevalidationFlow()
        {
            Task.Run(async () =>
            {
                int startHits = 0;
                int resourceHits = 0;

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.AbsolutePath == "/start")
                    {
                        startHits++;
                        var redirectHeaders = new HttpHeaders();
                        redirectHeaders.Set("Location", "/resource");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.Found,
                            redirectHeaders,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    if (req.Uri.AbsolutePath == "/resource")
                    {
                        resourceHits++;
                        var headers = new HttpHeaders();
                        headers.Set("Cache-Control", "no-cache");
                        headers.Set("ETag", "\"v1\"");

                        if (resourceHits == 1)
                        {
                            headers.Add("Set-Cookie", "sid=abc; Path=/; HttpOnly");
                            return Task.FromResult(new UHttpResponse(
                                HttpStatusCode.OK,
                                headers,
                                Encoding.UTF8.GetBytes("resource-body"),
                                ctx.Elapsed,
                                req));
                        }

                        Assert.AreEqual("\"v1\"", req.Headers.Get("If-None-Match"));
                        StringAssert.Contains("sid=abc", req.Headers.Get("Cookie"));
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.NotModified,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.NotFound,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    FollowRedirects = true,
                    MaxRedirects = 10,
                    Middlewares = new List<IHttpMiddleware>
                    {
                        new RedirectMiddleware(),
                        new CookieMiddleware(),
                        new CacheMiddleware(new CachePolicy
                        {
                            Storage = new MemoryCacheStorage(),
                            AllowSetCookieResponses = true
                        })
                    }
                });

                var first = await client.Get("https://example.test/start").SendAsync();
                var second = await client.Get("https://example.test/start").SendAsync();

                Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
                Assert.AreEqual("resource-body", first.GetBodyAsString());
                Assert.AreEqual("resource-body", second.GetBodyAsString());
                Assert.AreEqual("REVALIDATED", second.Headers.Get("X-Cache"));
                Assert.AreEqual(2, startHits);
                Assert.AreEqual(2, resourceHits);
                Assert.AreEqual(4, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Category("ExternalNetwork")]
        public void ExternalNetwork_HttpBin_Get()
        {
            Task.Run(async () =>
            {
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                var response = await client.Get("https://httpbin.org/get")
                    .WithTimeout(TimeSpan.FromSeconds(20))
                    .SendAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Category("Integration")]
        public void Http2IntegrationCoverage_Phase9_ValidatesAlpnNegotiation()
        {
            Task.Run(async () =>
            {
                using var pool = new TcpConnectionPool(tlsBackend: TlsBackend.Auto);
                using var transport = new RawSocketTransport(pool);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var response = await client.Get("https://www.google.com")
                    .WithTimeout(TimeSpan.FromSeconds(25))
                    .SendAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                var alpn = TryGetNegotiatedAlpn(pool, "www.google.com");
                Assert.That(
                    alpn,
                    Is.EqualTo("h2").Or.EqualTo("http/1.1"),
                    "Expected ALPN negotiation result to be h2 or http/1.1.");
            }).GetAwaiter().GetResult();
        }

        private static string TryGetNegotiatedAlpn(TcpConnectionPool pool, string hostFragment)
        {
            var idleField = typeof(TcpConnectionPool).GetField(
                "_idleConnections",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);

            foreach (var kv in idle)
            {
                if (!kv.Key.Contains(hostFragment, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (kv.Value.TryPeek(out var conn))
                    return conn.NegotiatedAlpnProtocol;
            }

            return null;
        }
    }
}
