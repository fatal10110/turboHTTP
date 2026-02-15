using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Testing;
using TurboHTTP.Tests;
using TurboHTTP.Transport;

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

                    var ex = await TestHelpers.AssertThrowsAsync<InvalidOperationException>(async () =>
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
        public void Http2IntegrationCoverage_DeferredToPhase9()
        {
            Assert.Ignore(
                "HTTP/2 platform ALPN/multiplexing integration is deferred to Phase 9 " +
                "(Platform Validation) for IL2CPP device-specific verification.");
        }
    }
}
