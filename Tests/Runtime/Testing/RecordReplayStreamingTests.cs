using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Testing;
using TurboHTTP.Transport;

namespace TurboHTTP.Tests.Testing
{
    /// <summary>
    /// #149: streaming (non-buffered) response bodies are recorded completely, bounded by MaxRecordBytes.
    /// </summary>
    [TestFixture]
    public class RecordReplayStreamingTests
    {
        private string _path;

        [SetUp]
        public void SetUp()
        {
            JsonModuleRegistration.EnsureRegistered();
            _path = Path.Combine(Path.GetTempPath(), "turbohttp-rr-stream-" + Guid.NewGuid().ToString("N") + ".json");
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }

        [Test]
        public void StreamingBody_64KbIn4KbChunks_IsRecordedCompletely()
        {
            var payload = Pattern(64 * 1024);
            var inner = new StreamingTransport(payload, chunkSize: 4096);

            byte[] forwarded;
            using (var transport = RecordTransport(inner))
            {
                forwarded = DispatchAndRead(transport, Get("https://stream.example.test/blob"), out var error);
                Assert.IsNull(error);
                transport.SaveRecordings();
            }

            CollectionAssert.AreEqual(payload, forwarded, "the caller still receives the whole body");
            Assert.IsTrue(inner.Source.Disposed, "the recorder disposes the streaming source it drained");
            var recorded = RecordedResponseBody();
            Assert.AreEqual(65536, recorded.Length);
            CollectionAssert.AreEqual(payload, recorded);

            using var replay = ReplayTransport();
            var replayed = DispatchAndRead(replay, Get("https://stream.example.test/blob"), out var replayError);
            Assert.IsNull(replayError);
            CollectionAssert.AreEqual(payload, replayed);
        }

        [Test]
        public void StreamingBody_OverMaxRecordBytes_FailsAndWritesNoFile()
        {
            const int max = 10000;
            var inner = new StreamingTransport(Pattern(max + 1), chunkSize: 4096);

            UHttpException error;
            using (var transport = RecordTransport(inner, max))
            {
                DispatchAndRead(transport, Get("https://stream.example.test/big?token=hidden"), out error);

                var save = Assert.Throws<InvalidOperationException>(() => transport.SaveRecordings());
                StringAssert.Contains("MaxRecordBytes", save.Message);
            }

            Assert.IsNotNull(error, "oversize must fail instead of truncating");
            StringAssert.Contains("MaxRecordBytes", error.Message);
            StringAssert.Contains(max.ToString(), error.Message);
            Assert.That(error.Message, Does.Not.Contain("hidden"));
            Assert.IsTrue(inner.Source.Disposed);
            Assert.IsFalse(File.Exists(_path), "no recording file may be written, not even on dispose");
        }

        [Test]
        public void BufferedBody_OverMaxRecordBytes_FailsAndWritesNoFile()
        {
            const int max = 1000;
            var inner = new MockTransport();
            inner.EnqueueResponse(HttpStatusCode.OK, body: Pattern(max + 1));

            UHttpException error;
            using (var transport = RecordTransport(inner, max))
            {
                DispatchAndRead(transport, Get("https://stream.example.test/buffered"), out error);
                Assert.Throws<InvalidOperationException>(() => transport.SaveRecordings());
            }

            Assert.IsNotNull(error);
            StringAssert.Contains("MaxRecordBytes", error.Message);
            Assert.IsFalse(File.Exists(_path));
        }

        [Test]
        public void StreamingBody_ExactlyMaxRecordBytes_IsRecorded()
        {
            const int max = 8192;
            var payload = Pattern(max);
            using (var transport = RecordTransport(new StreamingTransport(payload, 1000), max))
            {
                DispatchAndRead(transport, Get("https://stream.example.test/edge"), out var error);
                Assert.IsNull(error);
                transport.SaveRecordings();
            }

            CollectionAssert.AreEqual(payload, RecordedResponseBody());
        }

        [Test]
        public void RawSocketTransport_Http11ChunkedResponse_RecordsAndReplaysByteIdentical()
        {
            var payload = Pattern(48 * 1024 + 123);
            using var server = new ChunkedHttpServer(payload);
            var url = "http://127.0.0.1:" + server.Port + "/chunked";

            byte[] forwarded;
            using (var transport = RecordTransport(new RawSocketTransport()))
            {
                forwarded = DispatchAndRead(transport, Get(url), out var error);
                Assert.IsNull(error, error?.Message);
                transport.SaveRecordings();
            }

            CollectionAssert.AreEqual(payload, forwarded);
            CollectionAssert.AreEqual(payload, RecordedResponseBody());

            using var replay = ReplayTransport();
            var replayed = DispatchAndRead(replay, Get(url), out var replayError);
            Assert.IsNull(replayError);
            CollectionAssert.AreEqual(payload, replayed);
        }

        private RecordReplayTransport RecordTransport(IHttpTransport inner, long maxRecordBytes = RecordReplayTransport.DefaultMaxRecordBytes)
        {
            return new RecordReplayTransport(
                inner,
                new RecordReplayTransportOptions
                {
                    Mode = RecordReplayMode.Record,
                    RecordingPath = _path,
                    MaxRecordBytes = maxRecordBytes
                });
        }

        private RecordReplayTransport ReplayTransport()
        {
            return new RecordReplayTransport(
                innerTransport: null,
                new RecordReplayTransportOptions { Mode = RecordReplayMode.Replay, RecordingPath = _path });
        }

        private static UHttpRequest Get(string url) => new UHttpRequest(HttpMethod.GET, new Uri(url));

        private static byte[] Pattern(int length)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
                bytes[i] = (byte)((i * 31 + 7) & 0xFF);
            return bytes;
        }

        private byte[] RecordedResponseBody()
        {
            var match = Regex.Match(File.ReadAllText(_path), "\"ResponseBodyBase64\"\\s*:\\s*\"([^\"]*)\"");
            Assert.IsTrue(match.Success, "recording has a response body");
            return Convert.FromBase64String(match.Groups[1].Value);
        }

        private static byte[] DispatchAndRead(RecordReplayTransport transport, UHttpRequest request, out UHttpException error)
        {
            var handler = new StreamReadingHandler();
            Task.Run(async () =>
                await transport.DispatchAsync(request, handler, new RequestContext(request), CancellationToken.None))
                .GetAwaiter().GetResult();
            error = handler.Error;
            return handler.Body;
        }

        private sealed class StreamReadingHandler : IHttpHandler
        {
            public UHttpException Error { get; private set; }
            public byte[] Body { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                using var stream = new MemoryStream();
                if (body != null)
                {
                    var buffer = new byte[3000];
                    int read;
                    while ((read = await body.ReadAsync(buffer, CancellationToken.None)) > 0)
                        stream.Write(buffer, 0, read);
                    await body.DisposeAsync();
                }

                Body = stream.ToArray();
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Error = error;
            }
        }

        private sealed class StreamingTransport : IHttpTransport
        {
            private readonly byte[] _payload;
            private readonly int _chunkSize;

            internal StreamingTransport(byte[] payload, int chunkSize)
            {
                _payload = payload;
                _chunkSize = chunkSize;
            }

            internal ChunkedBodySource Source { get; private set; }

            public async Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                handler.OnRequestStart(request, context);
                Source = new ChunkedBodySource(_payload, _chunkSize);
                var headers = new HttpHeaders();
                headers.Set("Content-Type", "application/octet-stream");
                await handler.OnResponseStartAsync(200, headers, Source, context);
            }

            public ValueTask<UHttpResponse> SendAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
            }
        }

        // A body source that never exposes buffered data and yields fixed-size chunks.
        private sealed class ChunkedBodySource : IResponseBodySource
        {
            private readonly byte[] _payload;
            private readonly int _chunkSize;
            private int _offset;

            internal ChunkedBodySource(byte[] payload, int chunkSize)
            {
                _payload = payload;
                _chunkSize = chunkSize;
            }

            internal bool Disposed { get; private set; }

            public long? Length => null;

            public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
            {
                data = default;
                return false;
            }

            public bool TryDetachBufferedBody(out DetachedBufferedBody body)
            {
                body = default;
                return false;
            }

            public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
            {
                if (Disposed)
                    throw new ObjectDisposedException(nameof(ChunkedBodySource));

                var count = Math.Min(Math.Min(_chunkSize, destination.Length), _payload.Length - _offset);
                if (count <= 0)
                    return new ValueTask<int>(0);

                _payload.AsSpan(_offset, count).CopyTo(destination.Span);
                _offset += count;
                return new ValueTask<int>(count);
            }

            public async ValueTask DrainAsync(CancellationToken ct)
            {
                var buffer = new byte[_chunkSize];
                while (await ReadAsync(buffer, ct) > 0)
                {
                }
            }

            public void Abort()
            {
            }

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                return new ValueTask<HttpHeaders>(HttpHeaders.Empty);
            }

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return default;
            }
        }

        // One-shot loopback HTTP/1.1 server answering with a chunked body (RFC 9112 section 7.1).
        private sealed class ChunkedHttpServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly byte[] _payload;
            private readonly Task _serveTask;

            internal ChunkedHttpServer(byte[] payload)
            {
                _payload = payload;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _serveTask = Task.Run(ServeOnceAsync);
            }

            internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            private async Task ServeOnceAsync()
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var request = new List<byte>();
                var buffer = new byte[1024];
                while (!EndsWithHeaderTerminator(request))
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (read <= 0)
                        return;
                    for (int i = 0; i < read; i++)
                        request.Add(buffer[i]);
                }

                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" +
                    "Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, 0, head.Length);

                const int chunk = 4096;
                for (int offset = 0; offset < _payload.Length; offset += chunk)
                {
                    var size = Math.Min(chunk, _payload.Length - offset);
                    var sizeLine = Encoding.ASCII.GetBytes(size.ToString("x") + "\r\n");
                    await stream.WriteAsync(sizeLine, 0, sizeLine.Length);
                    await stream.WriteAsync(_payload, offset, size);
                    await stream.WriteAsync(new byte[] { 13, 10 }, 0, 2);
                }

                var last = Encoding.ASCII.GetBytes("0\r\n\r\n");
                await stream.WriteAsync(last, 0, last.Length);
                await stream.FlushAsync();
            }

            private static bool EndsWithHeaderTerminator(List<byte> data)
            {
                var n = data.Count;
                return n >= 4 && data[n - 4] == 13 && data[n - 3] == 10 && data[n - 2] == 13 && data[n - 1] == 10;
            }

            public void Dispose()
            {
                _listener.Stop();
                try
                {
                    _serveTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                    // The listener was stopped before a client connected.
                }
            }
        }
    }
}
