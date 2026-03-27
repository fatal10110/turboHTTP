using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    public partial class Http2ConnectionTests
    {
        // --- Connection Setup Tests ---

        [Test]
        public void InitializeAsync_SendsPreface()
        {
            AssertAsync.Run(async () =>
            {
                var duplex = new TestDuplexStream();

                var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);
                var initTask = conn.InitializeAsync(CancellationToken.None);

                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read);
                    if (n == 0)
                        break;

                    read += n;
                }

                Assert.AreEqual(24, read);
                Assert.AreEqual(Http2Constants.ConnectionPreface, preface);

                var serverCodec = new Http2FrameCodec(duplex.ServerStream);
                await serverCodec.ReadFrameAsync(16384, CancellationToken.None);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);

                await initTask;
                conn.Dispose();
            });
        }

        [Test]
        public void InitializeAsync_SendsSettings()
        {
            AssertAsync.Run(async () =>
            {
                var duplex = new TestDuplexStream();
                var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);
                var initTask = conn.InitializeAsync(CancellationToken.None);

                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read);
                    if (n == 0)
                        break;

                    read += n;
                }

                var serverCodec = new Http2FrameCodec(duplex.ServerStream);
                var settingsFrame = await serverCodec.ReadFrameAsync(16384, CancellationToken.None);

                Assert.AreEqual(Http2FrameType.Settings, settingsFrame.Type);
                Assert.AreEqual(0, settingsFrame.StreamId);
                Assert.IsFalse(settingsFrame.HasFlag(Http2FrameFlags.Ack));
                Assert.IsTrue(settingsFrame.Payload.Length > 0);
                Assert.AreEqual(0, settingsFrame.Payload.Length % 6);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);

                await initTask;
                conn.Dispose();
            });
        }

        [Test]
        public void InitializeAsync_AcksServerSettings()
        {
            AssertAsync.Run(async () =>
            {
                var duplex = new TestDuplexStream();
                var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);
                var initTask = conn.InitializeAsync(CancellationToken.None);

                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read);
                    read += n;
                }

                var serverCodec = new Http2FrameCodec(duplex.ServerStream);
                await serverCodec.ReadFrameAsync(16384, CancellationToken.None);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);

                await initTask;

                var clientAck = await serverCodec.ReadFrameAsync(16384, CancellationToken.None);
                Assert.AreEqual(Http2FrameType.Settings, clientAck.Type);
                Assert.IsTrue(clientAck.HasFlag(Http2FrameFlags.Ack));
                Assert.AreEqual(0, clientAck.Length);

                conn.Dispose();
            });
        }

        // --- Request/Response Lifecycle ---

        [Test]
        public void SendGetRequest_ReceiveResponse()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/path"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headersFrame.Type);
                Assert.IsTrue(headersFrame.HasFlag(Http2FrameFlags.EndStream));
                Assert.IsTrue(headersFrame.HasFlag(Http2FrameFlags.EndHeaders));
                int streamId = headersFrame.StreamId;
                Assert.AreEqual(1, streamId);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200),
                    cts.Token);

                var bodyBytes = System.Text.Encoding.UTF8.GetBytes("Hello, HTTP/2!");
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = bodyBytes,
                    Length = bodyBytes.Length
                }, cts.Token);

                var response = await responseTask;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("Hello, HTTP/2!", response.GetBodyAsString());

                conn.Dispose();
            });
        }

        [Test]
        public void SendPostRequest_WithBody()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var body = System.Text.Encoding.UTF8.GetBytes("request body");
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("https://test.example.com/api"),
                    body: body);
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headersFrame.Type);
                Assert.IsFalse(headersFrame.HasFlag(Http2FrameFlags.EndStream));
                int streamId = headersFrame.StreamId;

                var dataFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Data, dataFrame.Type);
                Assert.IsTrue(dataFrame.HasFlag(Http2FrameFlags.EndStream));
                Assert.AreEqual("request body", System.Text.Encoding.UTF8.GetString(dataFrame.Payload));

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 201, endStream: true),
                    cts.Token);

                var response = await responseTask;
                Assert.AreEqual((HttpStatusCode)201, response.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void SendPostRequest_WithKnownLengthStreamBody()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                using var bodyStream = new MemoryStream(Encoding.UTF8.GetBytes("stream body"), writable: false);
                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/api"))
                    .WithStreamBody(bodyStream, bodyStream.Length, leaveOpen: true);
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headersFrame.Type);
                Assert.IsFalse(headersFrame.HasFlag(Http2FrameFlags.EndStream));
                int streamId = headersFrame.StreamId;

                var dataFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Data, dataFrame.Type);
                Assert.IsTrue(dataFrame.HasFlag(Http2FrameFlags.EndStream));
                Assert.AreEqual("stream body", Encoding.UTF8.GetString(dataFrame.Payload));
                Assert.AreEqual(bodyStream.Length, bodyStream.Position);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 201, endStream: true),
                    cts.Token);

                var response = await responseTask;
                Assert.AreEqual((HttpStatusCode)201, response.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void SendPostRequest_WithUnknownLengthFactoryBody_SendsExplicitEndStreamFrame()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                int factoryCalls = 0;

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/api"))
                    .WithBodyFactory(_ =>
                    {
                        factoryCalls++;
                        return new ValueTask<Stream>(
                            new MemoryStream(Encoding.UTF8.GetBytes("factory body"), writable: false));
                    });
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headersFrame.Type);
                Assert.IsFalse(headersFrame.HasFlag(Http2FrameFlags.EndStream));
                int streamId = headersFrame.StreamId;

                var dataFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Data, dataFrame.Type);
                Assert.IsFalse(dataFrame.HasFlag(Http2FrameFlags.EndStream));
                Assert.AreEqual("factory body", Encoding.UTF8.GetString(dataFrame.Payload));

                var endFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Data, endFrame.Type);
                Assert.IsTrue(endFrame.HasFlag(Http2FrameFlags.EndStream));
                Assert.AreEqual(0, endFrame.Length);
                Assert.AreEqual(1, factoryCalls);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 202, endStream: true),
                    cts.Token);

                var response = await responseTask;
                Assert.AreEqual((HttpStatusCode)202, response.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void SendPostRequest_WithKnownLengthStreamBodyAndRequestTrailers_SendsTrailingHeaders()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                using var bodyStream = new MemoryStream(Encoding.UTF8.GetBytes("stream body"), writable: false);
                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/api"))
                    .WithStreamBody(bodyStream, bodyStream.Length, leaveOpen: true)
                    .WithRequestTrailers(
                        new[] { "Digest" },
                        () =>
                        {
                            var trailers = new HttpHeaders();
                            trailers.Set("Digest", "sha-256=abc");
                            return trailers;
                        });
                request.Headers.Set("Trailer", "X-Wrong");
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headersFrame.Type);
                Assert.IsFalse(headersFrame.HasFlag(Http2FrameFlags.EndStream));
                int streamId = headersFrame.StreamId;

                var decoder = new HpackDecoder();
                var decodedHeaders = decoder.Decode(headersFrame.Payload, 0, headersFrame.Length);
                Assert.AreEqual(1, decodedHeaders.Count(h => h.Name == "trailer"));
                Assert.IsTrue(decodedHeaders.Any(h => h.Name == "trailer" && h.Value == "Digest"));
                Assert.IsFalse(decodedHeaders.Any(h => h.Name == "trailer" && h.Value == "X-Wrong"));

                var dataFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Data, dataFrame.Type);
                Assert.IsFalse(dataFrame.HasFlag(Http2FrameFlags.EndStream));
                Assert.AreEqual("stream body", Encoding.UTF8.GetString(dataFrame.Payload));

                var trailerFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, trailerFrame.Type);
                Assert.IsTrue(trailerFrame.HasFlag(Http2FrameFlags.EndHeaders));
                Assert.IsTrue(trailerFrame.HasFlag(Http2FrameFlags.EndStream));
                var decodedTrailers = decoder.Decode(trailerFrame.Payload, 0, trailerFrame.Length);
                Assert.IsTrue(decodedTrailers.Any(h => h.Name == "digest" && h.Value == "sha-256=abc"));

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 201, endStream: true),
                    cts.Token);

                var response = await responseTask;
                Assert.AreEqual((HttpStatusCode)201, response.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void SendPostRequest_WithKnownLengthStreamBodyAndEmptyRequestTrailers_KeepsEndStreamOnData()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                using var bodyStream = new MemoryStream(Encoding.UTF8.GetBytes("stream body"), writable: false);
                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/api"))
                    .WithStreamBody(bodyStream, bodyStream.Length, leaveOpen: true)
                    .WithRequestTrailers(new[] { "Digest" }, () => null);
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headersFrame.Type);
                int streamId = headersFrame.StreamId;

                var dataFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Data, dataFrame.Type);
                Assert.IsTrue(dataFrame.HasFlag(Http2FrameFlags.EndStream));
                Assert.AreEqual("stream body", Encoding.UTF8.GetString(dataFrame.Payload));

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 201, endStream: true),
                    cts.Token);

                var response = await responseTask;
                Assert.AreEqual((HttpStatusCode)201, response.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void SendPostRequest_WithRequestTrailers_FiltersProhibitedFields_AndAllowsUndeclaredOnes()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                using var bodyStream = new MemoryStream(Encoding.UTF8.GetBytes("stream body"), writable: false);
                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/api"))
                    .WithStreamBody(bodyStream, bodyStream.Length, leaveOpen: true)
                    .WithRequestTrailers(
                        new[] { "Digest" },
                        () =>
                        {
                            var trailers = new HttpHeaders();
                            trailers.Set("Digest", "sha-256=abc");
                            trailers.Set("X-Extra", "ok");
                            trailers.Set("Authorization", "Bearer secret");
                            return trailers;
                        });
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = headersFrame.StreamId;
                await serverCodec.ReadFrameAsync(16384, cts.Token);

                var trailerFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, trailerFrame.Type);
                var decoder = new HpackDecoder();
                _ = decoder.Decode(headersFrame.Payload, 0, headersFrame.Length);
                var decodedTrailers = decoder.Decode(trailerFrame.Payload, 0, trailerFrame.Length);

                Assert.IsTrue(decodedTrailers.Any(h => h.Name == "digest" && h.Value == "sha-256=abc"));
                Assert.IsTrue(decodedTrailers.Any(h => h.Name == "x-extra" && h.Value == "ok"));
                Assert.IsFalse(decodedTrailers.Any(h => h.Name == "authorization"));

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 201, endStream: true),
                    cts.Token);

                var response = await responseTask;
                Assert.AreEqual((HttpStatusCode)201, response.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void SendPostRequest_WithInvalidRequestTrailerValue_FailsRequestAndResetsStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                using var bodyStream = new MemoryStream(Encoding.UTF8.GetBytes("stream body"), writable: false);
                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/api"))
                    .WithStreamBody(bodyStream, bodyStream.Length, leaveOpen: true)
                    .WithRequestTrailers(
                        new[] { "Digest" },
                        () =>
                        {
                            var trailers = new HttpHeaders();
                            trailers.Set("Digest", "sha-256=abc\r\nInjected: nope");
                            return trailers;
                        });
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headersFrame.Type);
                int streamId = headersFrame.StreamId;

                var rstFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.RstStream, rstFrame.Type);
                Assert.AreEqual(streamId, rstFrame.StreamId);

                UHttpException ex = null;
                try
                {
                    await responseTask;
                    Assert.Fail("Expected request trailer validation to fail.");
                }
                catch (UHttpException caught)
                {
                    ex = caught;
                }

                Assert.IsNotNull(ex);
                StringAssert.Contains("CRLF", ex.Message);
                Assert.IsInstanceOf<ArgumentException>(ex.InnerException);

                conn.Dispose();
            });
        }

        [Test]
        public void SendRequest_AfterGoaway_Throws()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var goawayPayload = new byte[8];
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.GoAway,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = goawayPayload,
                    Length = 8
                }, cts.Token);

                await Task.Delay(200, cts.Token);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/"));
                var context = new RequestContext(request);
                AssertAsync.ThrowsAsync<UHttpException, UHttpResponse>(
                    () => conn.SendRequestAsync(request, context, cts.Token));

                conn.Dispose();
            });
        }
    }
}
