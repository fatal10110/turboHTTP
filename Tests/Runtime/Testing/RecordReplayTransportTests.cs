using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Testing
{
    [TestFixture]
    public class RecordReplayTransportTests
    {
        [Test]
        public void DispatchAsync_RecordMode_WritesRecordingAndForwardsCallbacks()
        {
            Task.Run(async () =>
            {
                var recordingPath = Path.Combine(
                    Path.GetTempPath(),
                    "turbohttp-record-replay-record-" + Guid.NewGuid().ToString("N") + ".json");

                try
                {
                    var innerTransport = new MockTransport();
                    innerTransport.EnqueueResponse(HttpStatusCode.OK, body: Encoding.UTF8.GetBytes("recorded"));

                    using var transport = new RecordReplayTransport(
                        innerTransport,
                        new RecordReplayTransportOptions
                        {
                            Mode = RecordReplayMode.Record,
                            RecordingPath = recordingPath
                        });

                    var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/record"));
                    var context = new RequestContext(request);
                    var handler = new CallbackRecorder();

                    await transport.DispatchAsync(request, handler, context, CancellationToken.None);
                    transport.SaveRecordings();

                    CollectionAssert.AreEqual(
                        new[] { "request", "start:200", "data:8", "end" },
                        handler.Events);
                    Assert.AreEqual("recorded", handler.GetBodyAsString());
                    Assert.That(File.Exists(recordingPath), Is.True);
                    StringAssert.Contains("https://example.test/record", File.ReadAllText(recordingPath));
                }
                finally
                {
                    if (File.Exists(recordingPath))
                        File.Delete(recordingPath);
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DispatchAsync_ReplayMode_CallsRequestStartBeforeReplayedCallbacks()
        {
            Task.Run(async () =>
            {
                var recordingPath = Path.Combine(
                    Path.GetTempPath(),
                    "turbohttp-record-replay-replay-" + Guid.NewGuid().ToString("N") + ".json");

                try
                {
                    var innerTransport = new MockTransport();
                    innerTransport.EnqueueResponse(HttpStatusCode.OK, body: Encoding.UTF8.GetBytes("served"));

                    using (var recordTransport = new RecordReplayTransport(
                        innerTransport,
                        new RecordReplayTransportOptions
                        {
                            Mode = RecordReplayMode.Record,
                            RecordingPath = recordingPath
                        }))
                    {
                        var recordRequest = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/replay"));
                        var recordContext = new RequestContext(recordRequest);
                        using var recorded = await TransportDispatchHelper.CollectResponseAsync(
                            recordTransport,
                            recordRequest,
                            recordContext,
                            CancellationToken.None);
                        recordTransport.SaveRecordings();
                    }

                    using var replayTransport = new RecordReplayTransport(
                        innerTransport: null,
                        new RecordReplayTransportOptions
                        {
                            Mode = RecordReplayMode.Replay,
                            RecordingPath = recordingPath
                        });

                    var replayRequest = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/replay"));
                    var replayContext = new RequestContext(replayRequest);
                    var handler = new CallbackRecorder();

                    await replayTransport.DispatchAsync(replayRequest, handler, replayContext, CancellationToken.None);

                    CollectionAssert.AreEqual(
                        new[] { "request", "start:200", "data:6", "end" },
                        handler.Events);
                    Assert.AreEqual("served", handler.GetBodyAsString());
                }
                finally
                {
                    if (File.Exists(recordingPath))
                        File.Delete(recordingPath);
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DispatchAsync_ReplayStrictMismatch_CallsRequestStartThenResponseError()
        {
            Task.Run(async () =>
            {
                var recordingPath = Path.Combine(
                    Path.GetTempPath(),
                    "turbohttp-record-replay-mismatch-" + Guid.NewGuid().ToString("N") + ".json");

                try
                {
                    var innerTransport = new MockTransport();
                    innerTransport.EnqueueResponse(HttpStatusCode.OK, body: Encoding.UTF8.GetBytes("ok"));

                    using (var recordTransport = new RecordReplayTransport(
                        innerTransport,
                        new RecordReplayTransportOptions
                        {
                            Mode = RecordReplayMode.Record,
                            RecordingPath = recordingPath
                        }))
                    {
                        var recordRequest = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/match"));
                        var recordContext = new RequestContext(recordRequest);
                        using var recorded = await TransportDispatchHelper.CollectResponseAsync(
                            recordTransport,
                            recordRequest,
                            recordContext,
                            CancellationToken.None);
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

                    var replayRequest = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/miss"));
                    var replayContext = new RequestContext(replayRequest);
                    var handler = new CallbackRecorder();

                    await replayTransport.DispatchAsync(replayRequest, handler, replayContext, CancellationToken.None);

                    CollectionAssert.AreEqual(new[] { "request", "error" }, handler.Events);
                    Assert.IsNotNull(handler.Error);
                    StringAssert.Contains("No replay recording matched", handler.Error.Message);
                }
                finally
                {
                    if (File.Exists(recordingPath))
                        File.Delete(recordingPath);
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Dispose_SwallowsSaveAndInnerDisposeExceptions()
        {
            var recordingPath = Path.Combine(
                Path.GetTempPath(),
                "turbohttp-record-replay-dispose-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recordingPath);

            try
            {
                var transport = new RecordReplayTransport(
                    new ThrowingDisposeTransport(),
                    new RecordReplayTransportOptions
                    {
                        Mode = RecordReplayMode.Record,
                        RecordingPath = recordingPath,
                        AutoFlushOnDispose = true
                    });

                Assert.DoesNotThrow(() => transport.Dispose());
            }
            finally
            {
                if (Directory.Exists(recordingPath))
                    Directory.Delete(recordingPath, recursive: true);
            }
        }

        private sealed class CallbackRecorder : IHttpHandler
        {
            private readonly List<byte> _body = new List<byte>();

            public List<string> Events { get; } = new List<string>();
            public UHttpException Error { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                Events.Add("request");
            }

            public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
            {
                Events.Add("start:" + statusCode);
            }

            public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
            {
                Events.Add("data:" + chunk.Length);
                if (!chunk.IsEmpty)
                    _body.AddRange(chunk.ToArray());
            }

            public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
            {
                Events.Add("end");
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Error = error;
                Events.Add("error");
            }

            public string GetBodyAsString()
            {
                return _body.Count == 0 ? null : Encoding.UTF8.GetString(_body.ToArray());
            }
        }

        private sealed class ThrowingDisposeTransport : IHttpTransport
        {
            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
                throw new InvalidOperationException("inner-dispose-failure");
            }
        }
    }
}
