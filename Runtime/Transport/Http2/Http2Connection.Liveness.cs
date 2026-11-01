using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http2
{
    internal partial class Http2Connection
    {
        // PING liveness (RFC 9113 Section 6.7). Only while streams are open: an idle connection sends no
        // PINGs (battery, and peers may treat frequent PINGs as abuse, Section 10.5). Each PING carries a
        // unique non-zero payload; the read loop clears _outstandingPingPayload when the ACK echoes it.
        // The payload is a long, so it is accessed only through Interlocked (32-bit IL2CPP).
        private readonly TimeSpan _pingInterval;
        private readonly TimeSpan _pingTimeout;
        private long _pingPayloadCounter;
        private long _outstandingPingPayload;
        private TaskCompletionSource<bool> _pingAckSignal; // Volatile

        internal enum LivenessCycleResult
        {
            Idle,
            Acknowledged,
            TimedOut
        }

        private async Task KeepAliveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(_pingInterval, ct).ConfigureAwait(false);
                    if (await RunLivenessCycleAsync(ct).ConfigureAwait(false) == LivenessCycleResult.TimedOut)
                        return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Expected during disposal.
            }
            catch (ObjectDisposedException)
            {
                // Connection is shutting down.
            }
            catch
            {
                // Liveness must never crash the process.
            }
        }

        /// <summary>
        /// One liveness probe: with streams open, sends a PING and waits up to the PING timeout for its
        /// ACK. Without an ACK the connection is presumed dead: its streams fail with a network error and
        /// it is disposed, so the manager hands new requests a fresh connection.
        /// </summary>
        internal async Task<LivenessCycleResult> RunLivenessCycleAsync(CancellationToken ct)
        {
            if (_activeStreams.IsEmpty || !IsAlive)
                return LivenessCycleResult.Idle;

            long payload = Interlocked.Increment(ref _pingPayloadCounter);
            var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _pingAckSignal, ack);
            Interlocked.Exchange(ref _outstandingPingPayload, payload);

            Task send = null;
            try
            {
                using (var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    // The deadline starts before the write: a peer that stopped reading can block the
                    // write itself once the socket buffers are full.
                    var deadline = Task.Delay(_pingTimeout, deadlineCts.Token);
                    send = SendLivenessPingAsync(payload);
                    var first = await Task.WhenAny(ack.Task, deadline).ConfigureAwait(false);
                    deadlineCts.Cancel();
                    if (first == ack.Task)
                        return LivenessCycleResult.Acknowledged;
                }

                ct.ThrowIfCancellationRequested();
                if (Interlocked.CompareExchange(ref _outstandingPingPayload, 0, payload) != payload)
                    return LivenessCycleResult.Acknowledged; // the ACK raced the deadline

                var error = new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError,
                    $"HTTP/2 connection presumed dead: no PING ACK within {(long)_pingTimeout.TotalMilliseconds} ms"));
                FailAllStreams(error);
                Dispose();
                return LivenessCycleResult.TimedOut;
            }
            finally
            {
                if (send != null)
                {
                    try
                    {
                        // Completes promptly: written (ACK seen) or aborted by the connection token.
                        await send.ConfigureAwait(false);
                    }
                    catch
                    {
                        // The connection is failing; the read loop or the timeout reports it.
                    }
                }
            }
        }

        private Task SendLivenessPingAsync(long payload)
        {
            return SendFrameWithPooledPayloadAsync(
                Http2FrameType.Ping,
                0,
                8,
                _cts.Token,
                buffer =>
                {
                    for (int i = 0; i < 8; i++)
                        buffer[i] = (byte)(payload >> (56 - (8 * i)));
                });
        }

        /// <summary>Read loop: a PING ACK completes the probe whose payload it echoes.</summary>
        private void OnPingAck(byte[] payload)
        {
            long value = 0;
            for (int i = 0; i < 8; i++)
                value = (value << 8) | payload[i];

            if (value != 0 && Interlocked.CompareExchange(ref _outstandingPingPayload, 0, value) == value)
                Volatile.Read(ref _pingAckSignal)?.TrySetResult(true);
        }
    }
}
