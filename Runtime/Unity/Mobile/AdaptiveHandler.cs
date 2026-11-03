using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Unity.Mobile
{
    /// <summary>
    /// Records one <see cref="NetworkQualitySample"/> per request. Latency is the time to the response
    /// head; <see cref="NetworkQualitySample.BytesTransferred"/> is the request body length plus the
    /// response body bytes the consumer took, as this handler sees them (decoded when a decompression
    /// interceptor runs inside the adaptive one, which is the canonical order). A buffered body is
    /// counted at the head; any other body (multi-segment decoded or streamed) is counted as it is
    /// read, detached or drained, and the sample is recorded when the body completes (#261).
    /// </summary>
    internal sealed class AdaptiveHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly NetworkQualityDetector _detector;
        private readonly long _requestBytes;
        private readonly TimeSpan _started;

        private long _responseBytes;
        private long _latencyTicks;
        private long _headTimestamp;
        private int _statusCode;
        private int _sampleRecorded;

        internal AdaptiveHandler(
            IHttpHandler inner,
            NetworkQualityDetector detector,
            long requestBytes,
            TimeSpan started)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
            _requestBytes = requestBytes;
            _started = started;
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            _inner.OnRequestStart(request, context);
        }

        public async ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            Volatile.Write(ref _statusCode, statusCode);
            Interlocked.Exchange(ref _latencyTicks, (context.Elapsed - _started).Ticks);
            Interlocked.Exchange(ref _headTimestamp, Stopwatch.GetTimestamp());

            var forwarded = body;
            if (body != null)
            {
                if (body.TryGetBufferedData(out var buffered) && !buffered.IsEmpty)
                    Interlocked.Add(ref _responseBytes, buffered.Length);
                else
                    forwarded = new CountingResponseBodySource(body, OnBodyComplete);
            }

            try
            {
                await _inner.OnResponseStartAsync(statusCode, headers, forwarded, context).ConfigureAwait(false);
            }
            catch
            {
                RecordSample(wasTimeout: false, wasTransportFailure: false, wasSuccess: false);
                throw;
            }

            if (ReferenceEquals(forwarded, body))
                RecordSample(wasTimeout: false, wasTransportFailure: false, wasSuccess: IsSuccessStatus());
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            if (Interlocked.Read(ref _headTimestamp) == 0)
                Interlocked.Exchange(ref _latencyTicks, (context.Elapsed - _started).Ticks);

            var errorType = error?.HttpError?.Type ?? UHttpErrorType.Unknown;
            RecordSample(
                wasTimeout: errorType == UHttpErrorType.Timeout,
                wasTransportFailure: errorType == UHttpErrorType.NetworkError,
                wasSuccess: false);

            _inner.OnResponseError(error, context);
        }

        private void OnBodyComplete(long bodyBytes)
        {
            Interlocked.Add(ref _responseBytes, bodyBytes);
            RecordSample(wasTimeout: false, wasTransportFailure: false, wasSuccess: IsSuccessStatus());
        }

        private bool IsSuccessStatus()
        {
            var statusCode = Volatile.Read(ref _statusCode);
            return statusCode >= 200 && statusCode < 300;
        }

        // One sample per request: a body that completes after an error, or an error after a
        // buffered head, does not add a second one.
        private void RecordSample(bool wasTimeout, bool wasTransportFailure, bool wasSuccess)
        {
            if (Interlocked.Exchange(ref _sampleRecorded, 1) != 0)
                return;

            var latencyMs = TimeSpan.FromTicks(Interlocked.Read(ref _latencyTicks)).TotalMilliseconds;
            var totalMs = latencyMs;
            var headTimestamp = Interlocked.Read(ref _headTimestamp);
            if (headTimestamp != 0)
            {
                var bodyTicks = Stopwatch.GetTimestamp() - headTimestamp;
                if (bodyTicks > 0)
                    totalMs += bodyTicks * 1000d / Stopwatch.Frequency;
            }

            _detector.AddSample(new NetworkQualitySample(
                latencyMs: latencyMs,
                totalDurationMs: totalMs,
                wasTimeout: wasTimeout,
                wasTransportFailure: wasTransportFailure,
                bytesTransferred: _requestBytes + Interlocked.Read(ref _responseBytes),
                wasSuccess: wasSuccess));
        }
    }
}
