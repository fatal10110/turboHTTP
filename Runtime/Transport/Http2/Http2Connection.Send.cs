using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Http2
{
    internal partial class Http2Connection
    {
        /// <summary>
        /// Writes a HEADERS frame (plus CONTINUATION frames) carrying <paramref name="headerBlock"/>.
        /// The caller holds <see cref="_writeLock"/> for the whole call, the block was encoded in the
        /// same hold, and the frames are written with the connection token only: a header block is
        /// contiguous on the wire and never left unterminated (RFC 9113 Sections 4.3 and 6.10).
        /// </summary>
        private async Task SendHeadersAsync(
            int streamId,
            ReadOnlyMemory<byte> headerBlock,
            bool endStream)
        {
            int maxPayload = _remoteSettings.MaxFrameSize;

            if (headerBlock.Length <= maxPayload)
            {
                var flags = Http2FrameFlags.EndHeaders;
                if (endStream) flags |= Http2FrameFlags.EndStream;

                await WriteFrameLockedAsync(
                        Http2FrameType.Headers,
                        flags,
                        streamId,
                        headerBlock)
                    .ConfigureAwait(false);
            }
            else
            {
                int offset = 0;
                bool first = true;

                while (offset < headerBlock.Length)
                {
                    int remaining = headerBlock.Length - offset;
                    int chunkSize = Math.Min(remaining, maxPayload);
                    var payload = headerBlock.Slice(offset, chunkSize);
                    offset += chunkSize;

                    bool isLast = offset >= headerBlock.Length;
                    var frameType = first ? Http2FrameType.Headers : Http2FrameType.Continuation;
                    var flags = first && endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None;
                    if (isLast)
                        flags |= Http2FrameFlags.EndHeaders;

                    await WriteFrameLockedAsync(
                            frameType,
                            flags,
                            streamId,
                            payload,
                            flush: isLast)
                        .ConfigureAwait(false);
                    first = false;
                }
            }
        }

        /// <summary>
        /// Writes one frame while the caller holds <see cref="_writeLock"/>. A started frame is
        /// finished with the connection token only, never the request token: request cancellation
        /// must not leave a partial frame on the wire (RFC 9113 Section 4.1). Any write failure leaves
        /// the peer's view of the framing undefined, so the connection is failed.
        /// </summary>
        private async Task WriteFrameLockedAsync(
            Http2FrameType type,
            Http2FrameFlags flags,
            int streamId,
            ReadOnlyMemory<byte> payload,
            bool flush = true)
        {
            try
            {
                await _codec.WriteFrameAsync(type, flags, streamId, payload, _cts.Token, flush)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FailConnectionAfterWriteError(ex);
                throw;
            }
        }

        private void FailConnectionAfterWriteError(Exception ex)
        {
            try
            {
                FailAllStreams(ex as UHttpException ?? new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError,
                    "HTTP/2 frame write failed; the connection is closed",
                    ex)));
            }
            catch (ObjectDisposedException)
            {
                // Connection resources were already released by Dispose.
            }
        }

        private async Task<long> SendDataAsync(
            int streamId,
            UHttpRequestBody content,
            Http2Stream stream,
            CancellationToken ct)
        {
            if (content == null)
                throw new ArgumentNullException(nameof(content));

            if (content.TryGetBufferedData(out var buffered))
            {
                if (buffered.IsEmpty)
                {
                    var trailers = CollectRequestTrailers(content.TrailerProvider, stream);
                    if (trailers != null)
                    {
                        await SendTrailingHeadersAsync(streamId, trailers, stream, ct)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await SendEmptyEndStreamDataFrameAsync(streamId, stream, ct).ConfigureAwait(false);
                    }

                    return 0;
                }

                // Trailers are collected (not encoded) before the body so the final DATA frame knows
                // whether it carries END_STREAM; they are encoded at send time.
                var bufferedTrailers = CollectRequestTrailers(content.TrailerProvider, stream);
                var totalBufferedBytes = await SendBufferedDataAsync(
                        streamId,
                        buffered,
                        stream,
                        endStreamOnFinalFrame: bufferedTrailers == null,
                        ct)
                    .ConfigureAwait(false);

                if (bufferedTrailers != null)
                {
                    await SendTrailingHeadersAsync(streamId, bufferedTrailers, stream, ct)
                        .ConfigureAwait(false);
                }

                return totalBufferedBytes;
            }

            using var session = await content.OpenReadSessionAsync(ct).ConfigureAwait(false);
            return await SendStreamingDataAsync(streamId, session, content.TrailerProvider, stream, ct)
                .ConfigureAwait(false);
        }

        private async Task<long> SendBufferedDataAsync(
            int streamId,
            ReadOnlyMemory<byte> body,
            Http2Stream stream,
            bool endStreamOnFinalFrame,
            CancellationToken ct)
        {
            int offset = 0;
            long totalBytesSent = 0;
            while (offset < body.Length)
            {
                // Stop when the stream was reset or abandoned; a started or completed response does not
                // stop the upload (RFC 9113 Section 8.1).
                if (stream.IsRequestBodyAborted)
                    break;

                int available = Math.Min(
                    Interlocked.CompareExchange(ref _connectionSendWindow, 0, 0),
                    stream.SendWindowSize);
                available = Math.Min(available, _remoteSettings.MaxFrameSize);
                available = Math.Min(available, body.Length - offset);

                if (available <= 0)
                {
                    await WaitForSendWindowAsync(stream, ct).ConfigureAwait(false);
                    continue;
                }

                await _writeLock.WaitAsync(ct);
                bool lockHeld = true;
                int bytesSent = 0;
                try
                {
                    // Re-checked under _writeLock: the read loop fails a stream before it writes that
                    // stream's RST_STREAM (also under _writeLock), so no DATA follows our own reset.
                    if (stream.IsRequestBodyAborted)
                        break;

                    int connWindow = Interlocked.CompareExchange(ref _connectionSendWindow, 0, 0);
                    int streamWindow = stream.SendWindowSize;
                    int actualAvailable = Math.Min(connWindow, streamWindow);
                    actualAvailable = Math.Min(actualAvailable, _remoteSettings.MaxFrameSize);
                    actualAvailable = Math.Min(actualAvailable, body.Length - offset);

                    if (actualAvailable <= 0)
                    {
                        _writeLock.Release();
                        lockHeld = false;
                        await WaitForSendWindowAsync(stream, ct).ConfigureAwait(false);
                        continue;
                    }

                    bool isLast = endStreamOnFinalFrame && (offset + actualAvailable) >= body.Length;

                    // Zero-copy: pass the body slice directly — no intermediate ArrayPool copy.
                    // Http2FrameCodec.WriteFrameAsync accepts ReadOnlyMemory<byte> and writes
                    // it straight to the network stream. Request body ownership stays on
                    // UHttpRequest until the outer request lifecycle ends.
                    // Debit BEFORE the write, still under _writeLock (RFC 9113 Section 6.9.1). Once
                    // the frame is on the wire the peer may legally answer with a WINDOW_UPDATE for it;
                    // the read loop must see the debited window or a window already at 2^31-1 would
                    // overflow into a spurious FLOW_CONTROL_ERROR. The write below is not
                    // request-cancellable and any write failure fails the whole connection, so a
                    // debit for a frame that never completes is never observed by a live sender.
                    Interlocked.Add(ref _connectionSendWindow, -actualAvailable);
                    stream.AdjustSendWindowSize(-actualAvailable);

                    await WriteFrameLockedAsync(
                            Http2FrameType.Data,
                            isLast ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                            streamId,
                            body.Slice(offset, actualAvailable))
                        .ConfigureAwait(false);
                    if (isLast)
                        OnRequestEndStreamSent(stream);

                    bytesSent = actualAvailable;
                }
                finally
                {
                    if (lockHeld) _writeLock.Release();
                }

                offset += bytesSent;
                totalBytesSent += bytesSent;
            }

            return totalBytesSent;
        }

        private async Task<long> SendStreamingDataAsync(
            int streamId,
            RequestBodyReadSession session,
            Func<HttpHeaders> trailerProvider,
            Http2Stream stream,
            CancellationToken ct)
        {
            byte[] buffer = null;
            long totalBytesSent = 0;
            try
            {
                long? remaining = session.ContentLength;
                if (remaining.HasValue)
                {
                    while (remaining.Value > 0)
                    {
                        if (stream.IsRequestBodyAborted)
                            return totalBytesSent;

                        if (buffer == null)
                            buffer = ArrayPool<byte>.Shared.Rent(_streamingSendBufferBytes);

                        int bytesToRead = remaining.Value > buffer.Length
                            ? buffer.Length
                            : (int)remaining.Value;
                        int bytesRead = await session.ReadAsync(
                                new Memory<byte>(buffer, 0, bytesToRead),
                                ct)
                            .ConfigureAwait(false);
                        if (bytesRead <= 0)
                        {
                            throw new IOException(
                                "Request body ended before the declared Content-Length was fully produced.");
                        }

                        remaining -= bytesRead;
                        List<(string Name, string Value)> trailers = null;
                        if (remaining.Value == 0)
                            trailers = CollectRequestTrailers(trailerProvider, stream);

                        totalBytesSent += await SendBufferedDataAsync(
                                streamId,
                                new ReadOnlyMemory<byte>(buffer, 0, bytesRead),
                                stream,
                                endStreamOnFinalFrame: remaining.Value == 0 && trailers == null,
                                ct)
                            .ConfigureAwait(false);

                        if (trailers != null)
                        {
                            await SendTrailingHeadersAsync(streamId, trailers, stream, ct)
                                .ConfigureAwait(false);
                        }
                    }

                    return totalBytesSent;
                }

                while (true)
                {
                    if (stream.IsRequestBodyAborted)
                        return totalBytesSent;

                    if (buffer == null)
                        buffer = ArrayPool<byte>.Shared.Rent(_streamingSendBufferBytes);

                    int bytesRead = await session.ReadAsync(
                            new Memory<byte>(buffer, 0, buffer.Length),
                            ct)
                        .ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        var trailers = CollectRequestTrailers(trailerProvider, stream);
                        if (trailers != null)
                        {
                            await SendTrailingHeadersAsync(streamId, trailers, stream, ct)
                                .ConfigureAwait(false);
                            return totalBytesSent;
                        }

                        // Unknown-length bodies signal stream completion with an explicit
                        // zero-length END_STREAM frame once EOF is observed.
                        await SendEmptyEndStreamDataFrameAsync(streamId, stream, ct).ConfigureAwait(false);
                        return totalBytesSent;
                    }

                    totalBytesSent += await SendBufferedDataAsync(
                            streamId,
                            new ReadOnlyMemory<byte>(buffer, 0, bytesRead),
                            stream,
                            endStreamOnFinalFrame: false,
                            ct)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                if (buffer != null)
                    ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task SendEmptyEndStreamDataFrameAsync(
            int streamId,
            Http2Stream stream,
            CancellationToken ct)
        {
            if (stream.IsRequestBodyAborted)
                return;

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (stream.IsRequestBodyAborted)
                    return;

                await WriteFrameLockedAsync(
                        Http2FrameType.Data,
                        Http2FrameFlags.EndStream,
                        streamId,
                        ReadOnlyMemory<byte>.Empty)
                    .ConfigureAwait(false);
                OnRequestEndStreamSent(stream);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Encodes and writes the request trailer block in ONE <see cref="_writeLock"/> hold, so the
        /// encoder output is consumed before any other block is encoded and the encoder's dynamic
        /// table evolves in wire order (RFC 7541 Section 2.2). When the trailers are skipped (the
        /// upload was aborted) nothing is encoded, so encoder state is untouched.
        /// </summary>
        private async Task SendTrailingHeadersAsync(
            int streamId,
            List<(string Name, string Value)> trailers,
            Http2Stream stream,
            CancellationToken ct)
        {
            if (trailers == null || trailers.Count == 0 || stream.IsRequestBodyAborted)
                return;

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (stream.IsRequestBodyAborted)
                    return;

                var trailerHeaderBlock = _hpackEncoder.Encode(trailers);
                await SendHeadersAsync(streamId, trailerHeaderBlock, endStream: true)
                    .ConfigureAwait(false);
                OnRequestEndStreamSent(stream);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Runs the caller's trailer provider (outside <see cref="_writeLock"/>) and returns the
        /// validated, lower-cased trailer fields to send, or null when there are none. Nothing is
        /// HPACK-encoded here.
        /// </summary>
        private static List<(string Name, string Value)> CollectRequestTrailers(
            Func<HttpHeaders> trailerProvider,
            Http2Stream stream)
        {
            if (trailerProvider == null || stream.IsRequestBodyAborted)
                return null;

            var trailers = trailerProvider();
            if (trailers == null || trailers.Count == 0)
                return null;

            List<(string Name, string Value)> fields = null;
            foreach (var name in trailers.Names)
            {
                if (TrailerFieldValidator.IsProhibitedRequestTrailer(name))
                    continue;

                var lowerName = ToLowerAsciiHeaderName(name);
                var values = trailers.GetValues(name);
                for (int i = 0; i < values.Count; i++)
                {
                    ValidateTrailerHeader(name, values[i]);
                    fields ??= new List<(string Name, string Value)>();
                    fields.Add((lowerName, values[i]));
                }
            }

            return fields;
        }

        private static void ValidateTrailerHeader(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Header name cannot be null or empty");

            var nameSpan = name.AsSpan();
            for (int i = 0; i < nameSpan.Length; i++)
            {
                if (!EncodingHelper.IsRfc9110TChar(nameSpan[i]))
                    throw new ArgumentException($"Header name contains invalid characters: {name}");
            }

            if (value != null && value.AsSpan().IndexOfAny('\r', '\n') >= 0)
                throw new ArgumentException($"Header value for '{name}' contains CRLF characters");
        }

        private static readonly Action<object> s_wakeSendWindowWaiters =
            static state => ((Http2Connection)state).WakeSendWindowWaiters();

        /// <summary>
        /// Waits until the send window of <paramref name="stream"/> may have changed: a stream or
        /// connection WINDOW_UPDATE, an INITIAL_WINDOW_SIZE increase, the stream being reset, failed or
        /// abandoned, connection failure, or <paramref name="ct"/>. Returns at once when the window is
        /// already positive or the upload is aborted; callers re-check and loop. A window may be
        /// negative after an INITIAL_WINDOW_SIZE decrease (RFC 9113 Section 6.9.2): that is a wait too.
        /// </summary>
        private async ValueTask WaitForSendWindowAsync(Http2Stream stream, CancellationToken ct)
        {
            // Take the signal BEFORE re-reading the state. Every waker publishes its state change first
            // and then swaps the signal out, so a change after this point completes this very signal.
            var signal = Volatile.Read(ref _sendWindowSignal);
            if (signal == null)
            {
                var created = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                signal = Interlocked.CompareExchange(ref _sendWindowSignal, created, null) ?? created;
            }

            ct.ThrowIfCancellationRequested();
            ThrowIfConnectionClosedForSend();
            if (stream.IsRequestBodyAborted ||
                Math.Min(Interlocked.CompareExchange(ref _connectionSendWindow, 0, 0), stream.SendWindowSize) > 0)
            {
                return;
            }

            // A non-cancelable token registers nothing; a cancelled one wakes every waiter once, and
            // the others simply re-check and wait again.
            using (ct.Register(s_wakeSendWindowWaiters, this))
            {
                await signal.Task.ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            ThrowIfConnectionClosedForSend();
        }

        private void ThrowIfConnectionClosedForSend()
        {
            // Not _cts.Token: the token source may already be disposed. DispatchAsync maps this
            // exception to a network error because the connection token is cancelled.
            if (_cts.IsCancellationRequested)
                throw new OperationCanceledException("HTTP/2 connection closed while waiting for send window");
        }

        /// <summary>
        /// Wakes every sender blocked in <see cref="WaitForSendWindowAsync"/>. Call after the state
        /// change is published. Cheap when nobody waits; never runs sender code inline.
        /// </summary>
        internal void WakeSendWindowWaiters()
        {
            if (Volatile.Read(ref _sendWindowSignal) == null)
                return;

            Interlocked.Exchange(ref _sendWindowSignal, null)?.TrySetResult(true);
        }

        private static string BuildAuthorityValue(Uri uri)
        {
            var host = uri.Host;
            if (uri.HostNameType == UriHostNameType.IPv6)
                host = $"[{host}]";

            bool isDefaultPort = (uri.Scheme == "https" && uri.Port == 443)
                              || (uri.Scheme == "http" && uri.Port == 80);
            return isDefaultPort ? host : $"{host}:{uri.Port}";
        }

        private static bool IsHttp2ForbiddenHeader(string name)
        {
            return string.Equals(name, "connection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "transfer-encoding", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "keep-alive", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "proxy-connection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "upgrade", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "host", StringComparison.OrdinalIgnoreCase);
        }
    }
}
