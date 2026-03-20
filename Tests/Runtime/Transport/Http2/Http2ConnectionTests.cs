using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    [TestFixture]
    public partial class Http2ConnectionTests
    {
        /// <summary>
        /// Helper: create a duplex stream, start a simulated server that sends SETTINGS + SETTINGS ACK,
        /// and return an initialized Http2Connection.
        /// </summary>
        private async Task<(Http2Connection conn, Stream serverStream, TestDuplexStream duplex)>
            CreateInitializedConnectionAsync(CancellationToken ct = default, Http2Options options = null)
        {
            var duplex = new TestDuplexStream();
            var conn = new Http2Connection(
                duplex.ClientStream,
                "test.example.com",
                443,
                options ?? new Http2Options());

            var serverCodec = new Http2FrameCodec(duplex.ServerStream);
            var serverTask = Task.Run(async () =>
            {
                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read, ct);
                    if (n == 0) throw new IOException("Unexpected end of stream");
                    read += n;
                }

                await serverCodec.ReadFrameAsync(16384, ct);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, ct);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, ct);

                await serverCodec.ReadFrameAsync(16384, ct);
            }, ct);

            await conn.InitializeAsync(ct);
            await serverTask;

            return (conn, duplex.ServerStream, duplex);
        }

        private static Http2Options CreateTestHttp2Options(
            int? perStreamReceiveBufferBytes = null,
            int? maxConnectionBufferedBytes = null,
            int? stallTimeoutMilliseconds = null,
            int? maintenanceIntervalMilliseconds = null)
        {
            var options = new Http2Options();
            if (perStreamReceiveBufferBytes.HasValue)
                options.TestPerStreamReceiveBufferBytesOverride = perStreamReceiveBufferBytes.Value;
            if (maxConnectionBufferedBytes.HasValue)
                options.TestMaxConnectionBufferedBytesOverride = maxConnectionBufferedBytes.Value;
            if (stallTimeoutMilliseconds.HasValue)
                options.TestStallTimeoutMillisecondsOverride = stallTimeoutMilliseconds.Value;
            if (maintenanceIntervalMilliseconds.HasValue)
                options.TestMaintenanceIntervalMillisecondsOverride = maintenanceIntervalMilliseconds.Value;

            return options;
        }

        /// <summary>
        /// Helper: encode response headers as HPACK and build a HEADERS frame.
        /// </summary>
        private Http2Frame BuildResponseHeadersFrame(
            int streamId,
            int statusCode,
            Dictionary<string, string> headers = null,
            bool endStream = false)
        {
            var encoder = new HpackEncoder();
            var headerList = new List<(string, string)>
            {
                (":status", statusCode.ToString())
            };

            if (headers != null)
            {
                foreach (var kvp in headers)
                    headerList.Add((kvp.Key, kvp.Value));
            }

            byte[] headerBlock = encoder.Encode(headerList).ToArray();

            var flags = Http2FrameFlags.EndHeaders;
            if (endStream)
                flags |= Http2FrameFlags.EndStream;

            return new Http2Frame
            {
                Type = Http2FrameType.Headers,
                Flags = flags,
                StreamId = streamId,
                Payload = headerBlock,
                Length = headerBlock.Length
            };
        }

        private static byte[] BuildSettingsPayload(Http2SettingId id, uint value)
        {
            return new[]
            {
                (byte)(((ushort)id >> 8) & 0xFF),
                (byte)((ushort)id & 0xFF),
                (byte)((value >> 24) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF)
            };
        }

        private async Task<(Http2Connection conn, Stream serverStream, TestDuplexStream duplex)>
            CreateInitializedConnectionAsyncWithInitialWindow(
                int initialWindowSize,
                CancellationToken ct = default)
        {
            var duplex = new TestDuplexStream();
            var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);

            var serverCodec = new Http2FrameCodec(duplex.ServerStream);
            var serverTask = Task.Run(async () =>
            {
                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read, ct);
                    if (n == 0) throw new IOException("Unexpected end of stream");
                    read += n;
                }

                await serverCodec.ReadFrameAsync(16384, ct);

                var initialWindowPayload = BuildSettingsPayload(
                    Http2SettingId.InitialWindowSize,
                    (uint)initialWindowSize);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = initialWindowPayload,
                    Length = initialWindowPayload.Length
                }, ct);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, ct);

                await serverCodec.ReadFrameAsync(16384, ct);
            }, ct);

            await conn.InitializeAsync(ct);
            await serverTask;
            return (conn, duplex.ServerStream, duplex);
        }

        private sealed class DisposeTrackingStream : Stream
        {
            private readonly Stream _inner;
            private bool _disposed;

            public bool DisposeCalled { get; private set; }

            public DisposeTrackingStream(Stream inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _inner.Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                return _inner.Seek(offset, origin);
            }

            public override void SetLength(long value)
            {
                _inner.SetLength(value);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                return _inner.ReadAsync(buffer, offset, count, cancellationToken);
            }

            public override Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                return _inner.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                return _inner.FlushAsync(cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
                if (_disposed)
                {
                    base.Dispose(disposing);
                    return;
                }

                _disposed = true;
                if (disposing)
                {
                    DisposeCalled = true;
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        private sealed class ThrowingResponseStartHandler : IHttpHandler
        {
            public bool ResponseErrorCalled { get; private set; }
            public UHttpException LastError { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                throw new InvalidOperationException("handler-start-failure");
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                ResponseErrorCalled = true;
                LastError = error;
            }
        }

        private sealed class TimelineRecordingErrorHandler : IHttpHandler
        {
            private readonly TaskCompletionSource<UHttpException> _errorSource =
                new TaskCompletionSource<UHttpException>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<UHttpException> ErrorTask => _errorSource.Task;
            public bool SawRequestFailedBeforeError { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                body?.Abort();
                return default;
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                SawRequestFailedBeforeError = context.Timeline.Any(evt => evt.Name == "RequestFailed");
                _errorSource.TrySetResult(error);
            }
        }
    }
}
