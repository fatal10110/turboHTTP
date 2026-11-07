using System;
using System.Diagnostics;
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
        // The read loop counts every inbound frame (Interlocked): a probe whose ACK is late but that saw
        // frames arrive meanwhile (the ACK queued behind DATA or behind our own upload on a slow link)
        // does not declare the connection dead.
        // One PING is outstanding at a time: the keep-alive loop and the idle-reuse probe share the
        // probe in flight (_activeProbe, Interlocked), so concurrent callers never overwrite each other's
        // payload and a burst of requests on an idle connection sends one PING, not one each.
        private readonly TimeSpan _pingInterval;
        private readonly TimeSpan _pingTimeout;
        private long _pingPayloadCounter;
        private long _outstandingPingPayload;
        private TaskCompletionSource<bool> _pingAckSignal; // Volatile
        private int _inboundFrameCount;
        private Task<LivenessCycleResult> _activeProbe; // Interlocked / Volatile

        // Idle-reuse probe (Http2Options.ReuseProbeIdleThreshold, off when 0). Stopwatch ticks, written
        // by the read loop on every inbound frame while the probe is enabled; a long, so Interlocked only.
        private readonly long _reuseProbeIdleTicks;
        private long _lastInboundTimestamp;

        internal enum LivenessCycleResult
        {
            Idle,
            Acknowledged,
            PeerActive,
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
        /// ACK. Without an ACK, and without any other inbound frame since the PING was sent, the
        /// connection is presumed dead: its streams fail with a network error and it is disposed, so the
        /// manager hands new requests a fresh connection.
        /// </summary>
        internal async Task<LivenessCycleResult> RunLivenessCycleAsync(CancellationToken ct)
        {
            if (_activeStreams.IsEmpty || !IsAlive)
                return LivenessCycleResult.Idle;

            return await AwaitProbeAsync(GetOrStartProbe(), ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Idle-reuse probe: one PING before a request reuses a connection that received no frame for
        /// the configured threshold. Returns when the peer answered (ACK or any other frame). Throws a
        /// not-processed <see cref="Http2RetryableRequestException"/> when the probe timed out or the
        /// connection closed meanwhile; the connection is then disposed and nothing was sent for the
        /// request, so the transport replays it on a fresh connection.
        /// </summary>
        private async Task ProbeIdleConnectionBeforeReuseAsync(CancellationToken ct)
        {
            LivenessCycleResult result;
            try
            {
                result = await AwaitProbeAsync(GetOrStartProbe(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The probe was cancelled by the connection token: the connection closed meanwhile.
                result = LivenessCycleResult.TimedOut;
            }

            if (result == LivenessCycleResult.TimedOut)
            {
                throw new Http2RetryableRequestException(
                    new UHttpError(
                        UHttpErrorType.NetworkError,
                        "HTTP/2 idle connection did not answer its reuse PING; the request was not sent"),
                    requestNotProcessed: true);
            }
        }

        private bool IsIdleBeyondReuseProbeThreshold() =>
            Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastInboundTimestamp) >= _reuseProbeIdleTicks;

        private void RecordInboundActivity() =>
            Interlocked.Exchange(ref _lastInboundTimestamp, Stopwatch.GetTimestamp());

        /// <summary>Moves the last inbound activity into the past (tests only: no wall-clock waits).</summary>
        internal void BackdateInboundActivityForTest(TimeSpan by) =>
            Interlocked.Add(ref _lastInboundTimestamp, -ToStopwatchTicks(by));

        private static long ToStopwatchTicks(TimeSpan value) =>
            value <= TimeSpan.Zero ? 0 : Math.Max(1, (long)(value.TotalSeconds * Stopwatch.Frequency));

        /// <summary>
        /// The PING probe in flight, or a new one. The probe runs under the connection token, not a
        /// caller's, because several callers may share it; it always completes its task (result,
        /// or cancelled when the connection closes), which is its owner.
        /// </summary>
        private Task<LivenessCycleResult> GetOrStartProbe()
        {
            while (true)
            {
                var current = Volatile.Read(ref _activeProbe);
                if (current != null && !current.IsCompleted)
                    return current;

                var source = new TaskCompletionSource<LivenessCycleResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (Interlocked.CompareExchange(ref _activeProbe, source.Task, current) != current)
                    continue;

                _ = RunProbeIntoAsync(source);
                return source.Task;
            }
        }

        private async Task RunProbeIntoAsync(TaskCompletionSource<LivenessCycleResult> source)
        {
            try
            {
                source.TrySetResult(await RunProbeAsync(_cts.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                source.TrySetCanceled();
            }
            catch (ObjectDisposedException)
            {
                source.TrySetCanceled();
            }
            catch (Exception ex)
            {
                source.TrySetException(ex);
            }
        }

        /// <summary>Waits for a shared probe, giving up (only this caller) when <paramref name="ct"/> fires.</summary>
        private static async Task<LivenessCycleResult> AwaitProbeAsync(
            Task<LivenessCycleResult> probe,
            CancellationToken ct)
        {
            if (!probe.IsCompleted && ct.CanBeCanceled)
            {
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(
                           state => ((TaskCompletionSource<bool>)state).TrySetResult(true),
                           cancelled,
                           useSynchronizationContext: false))
                {
                    if (await Task.WhenAny(probe, cancelled.Task).ConfigureAwait(false) != probe)
                        ct.ThrowIfCancellationRequested();
                }
            }

            return await probe.ConfigureAwait(false);
        }

        private async Task<LivenessCycleResult> RunProbeAsync(CancellationToken ct)
        {
            long payload = Interlocked.Increment(ref _pingPayloadCounter);
            int framesBeforePing = Volatile.Read(ref _inboundFrameCount);
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

                // Frames kept arriving: the peer is alive and the ACK is only delayed behind them. The
                // outstanding payload is cleared above, so a late ACK is ignored; the next cycle re-probes.
                if (Volatile.Read(ref _inboundFrameCount) != framesBeforePing)
                    return LivenessCycleResult.PeerActive;

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
