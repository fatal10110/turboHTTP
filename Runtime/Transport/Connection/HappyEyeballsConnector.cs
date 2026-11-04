using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Connection
{
    public static class HappyEyeballsConnector
    {
        private readonly struct ScheduledAttempt
        {
            public ScheduledAttempt(IPAddress address, TimeSpan due)
            {
                Address = address;
                Due = due;
            }

            public IPAddress Address { get; }
            public TimeSpan Due { get; }
        }

        private sealed class AttemptResult
        {
            public IPAddress Address { get; set; }
            public Socket Socket { get; set; }
            public Exception Exception { get; set; }
            public bool Success => Socket != null;
        }

        /// <summary>
        /// Connects to the first reachable address (RFC 8305 Happy Eyeballs v2). The address
        /// families interleave (RFC 8305 section 4): the first alternate-family address starts
        /// <see cref="HappyEyeballsOptions.FamilyStaggerDelay"/> after the first attempt and every
        /// later attempt <see cref="HappyEyeballsOptions.AttemptSpacingDelay"/> after the previous
        /// one, so an unroutable family never keeps the other family from being tried. When a slot
        /// of <see cref="HappyEyeballsOptions.MaxConcurrentAttempts"/> frees up, a family with no
        /// attempt in flight goes first.
        /// The first success wins; losing attempts are cancelled and their sockets disposed.
        /// </summary>
        /// <param name="addresses">Resolved addresses (duplicates and nulls are ignored).</param>
        /// <param name="port">Remote port.</param>
        /// <param name="connectTimeout">
        /// Wall-clock deadline for the whole connect (every attempt, sequential mode included).
        /// <see cref="Timeout.InfiniteTimeSpan"/>, zero or a negative value disables it.
        /// When it expires a <see cref="TimeoutException"/> is thrown.
        /// </param>
        /// <param name="options">Racing options.</param>
        /// <param name="cancellationToken">Caller cancellation; surfaces as <see cref="OperationCanceledException"/>.</param>
        /// <param name="connector">Dialer seam for tests; defaults to a TCP connect.</param>
        public static async Task<Socket> ConnectAsync(
            IPAddress[] addresses,
            int port,
            TimeSpan connectTimeout,
            HappyEyeballsOptions options,
            CancellationToken cancellationToken,
            Func<IPAddress, int, CancellationToken, Task<Socket>> connector = null)
        {
            if (addresses == null) throw new ArgumentNullException(nameof(addresses));
            if (addresses.Length == 0)
                throw new SocketException((int)SocketError.HostNotFound);
            if (options == null) throw new ArgumentNullException(nameof(options));

            options.Validate();
            connector ??= ConnectEndpointAsync;

            var deduped = Deduplicate(addresses);
            if (deduped.Count == 0)
                throw new SocketException((int)SocketError.HostNotFound);

            cancellationToken.ThrowIfCancellationRequested();

            // The deadline covers every path, including the sequential one (#163).
            CancellationTokenSource timeoutCts = connectTimeout > TimeSpan.Zero && connectTimeout != Timeout.InfiniteTimeSpan
                ? new CancellationTokenSource(connectTimeout)
                : null;
            CancellationTokenSource deadlineCts = timeoutCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
                : null;
            var token = deadlineCts?.Token ?? cancellationToken;

            try
            {
                if (!options.Enable)
                    return await ConnectSequentialAsync(deduped, port, token, connector).ConfigureAwait(false);

                // A single family races too: its addresses are staggered by AttemptSpacingDelay
                // instead of waiting for each attempt to fail (RFC 8305 section 5).
                var familyOrder = Partition(deduped, options.PreferIpv6);
                var schedule = BuildSchedule(familyOrder.preferred, familyOrder.alternate, options);
                return await RaceAsync(schedule, port, options, token, connector).ConfigureAwait(false);
            }
            catch (Exception) when (timeoutCts != null &&
                                    timeoutCts.IsCancellationRequested &&
                                    !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Connection attempt timed out after " + (long)connectTimeout.TotalMilliseconds + "ms.");
            }
            finally
            {
                try { deadlineCts?.Dispose(); } catch { }
                try { timeoutCts?.Dispose(); } catch { }
            }
        }

        private static async Task<Socket> RaceAsync(
            List<ScheduledAttempt> schedule,
            int port,
            HappyEyeballsOptions options,
            CancellationToken cancellationToken,
            Func<IPAddress, int, CancellationToken, Task<Socket>> connector)
        {
            CancellationTokenSource linkedCts = null;
            List<Task<AttemptResult>> activeTasks = null;
            // Address of each in-flight attempt, index-aligned with activeTasks.
            List<IPAddress> activeAddresses = null;

            try
            {
                // Own source so the winner can cancel the losers without touching the caller.
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var ct = linkedCts.Token;

                activeTasks = new List<Task<AttemptResult>>();
                activeAddresses = new List<IPAddress>();
                var failures = new List<Exception>();
                var started = Stopwatch.StartNew();
                int scheduleIndex = 0;

                while (scheduleIndex < schedule.Count || activeTasks.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    while (scheduleIndex < schedule.Count &&
                           activeTasks.Count < options.MaxConcurrentAttempts &&
                           schedule[scheduleIndex].Due <= started.Elapsed)
                    {
                        PromoteIdleFamily(schedule, scheduleIndex, activeAddresses);
                        var attempt = schedule[scheduleIndex++];
                        activeTasks.Add(AttemptAsync(attempt.Address, port, ct, connector));
                        activeAddresses.Add(attempt.Address);
                    }

                    if (activeTasks.Count == 0)
                    {
                        if (scheduleIndex >= schedule.Count)
                            break;

                        var wait = schedule[scheduleIndex].Due - started.Elapsed;
                        if (wait > TimeSpan.Zero)
                            await Task.Delay(wait, ct).ConfigureAwait(false);
                        continue;
                    }

                    Task<AttemptResult> completedTask;
                    if (scheduleIndex < schedule.Count &&
                        activeTasks.Count < options.MaxConcurrentAttempts)
                    {
                        var untilNext = schedule[scheduleIndex].Due - started.Elapsed;
                        if (untilNext <= TimeSpan.Zero)
                            continue;

                        var completionTask = Task.WhenAny(activeTasks);
                        var scheduleTickTask = Task.Delay(untilNext, ct);
                        var signaled = await Task.WhenAny(completionTask, scheduleTickTask)
                            .ConfigureAwait(false);

                        if (ReferenceEquals(signaled, scheduleTickTask))
                            continue;

                        completedTask = await completionTask.ConfigureAwait(false);
                    }
                    else
                    {
                        completedTask = await Task.WhenAny(activeTasks).ConfigureAwait(false);
                    }

                    int completedIndex = activeTasks.IndexOf(completedTask);
                    activeTasks.RemoveAt(completedIndex);
                    activeAddresses.RemoveAt(completedIndex);

                    var result = await completedTask.ConfigureAwait(false);
                    if (result.Success)
                    {
                        linkedCts.Cancel();
                        await DrainLosersAsync(activeTasks, result.Socket).ConfigureAwait(false);
                        activeTasks.Clear();
                        return result.Socket;
                    }

                    if (result.Exception != null)
                        failures.Add(result.Exception);
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (failures.Count == 0)
                    throw new SocketException((int)SocketError.HostUnreachable);

                if (failures.Count == 1)
                    ExceptionDispatchInfo.Capture(failures[0]).Throw();

                throw new AggregateException("All Happy Eyeballs connection attempts failed.", failures);
            }
            finally
            {
                if (activeTasks != null && activeTasks.Count > 0)
                {
                    try
                    {
                        linkedCts?.Cancel();
                    }
                    catch
                    {
                    }

                    await ObserveOutstandingAttemptsAsync(activeTasks).ConfigureAwait(false);
                }

                try { linkedCts?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Picks the attempt that takes a free slot. RFC 8305 section 5 has no in-flight cap; with
        /// <see cref="HappyEyeballsOptions.MaxConcurrentAttempts"/> a slot freed by a fast failure
        /// must not go to another address of a family that still has a stalled attempt while the
        /// other family has nothing in flight. Otherwise a blackholed family (IPv6 without a route)
        /// fills every slot again and the healthy family never gets another try: v6a hangs, v4a
        /// fails at once, v6b would join v6a and v4b would wait for the deadline. In that case the
        /// next scheduled address of the idle family moves to <paramref name="next"/> and starts
        /// now; the remaining order and due times are unchanged.
        /// </summary>
        private static void PromoteIdleFamily(List<ScheduledAttempt> schedule, int next, List<IPAddress> inFlight)
        {
            var family = schedule[next].Address.AddressFamily;
            if (!HasFamilyInFlight(inFlight, family))
                return;

            for (int i = next + 1; i < schedule.Count; i++)
            {
                var candidate = schedule[i];
                var candidateFamily = candidate.Address.AddressFamily;
                if (candidateFamily == family || HasFamilyInFlight(inFlight, candidateFamily))
                    continue;

                schedule.RemoveAt(i);
                schedule.Insert(next, candidate);
                return;
            }
        }

        private static bool HasFamilyInFlight(List<IPAddress> inFlight, AddressFamily family)
        {
            for (int i = 0; i < inFlight.Count; i++)
            {
                if (inFlight[i].AddressFamily == family)
                    return true;
            }

            return false;
        }

        private static async Task DrainLosersAsync(List<Task<AttemptResult>> activeTasks, Socket winner)
        {
            if (activeTasks == null || activeTasks.Count == 0)
                return;

            for (int i = 0; i < activeTasks.Count; i++)
            {
                try
                {
                    var result = await activeTasks[i].ConfigureAwait(false);
                    if (result.Success && !ReferenceEquals(result.Socket, winner))
                        result.Socket.Dispose();
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }
            }
        }

        private static async Task ObserveOutstandingAttemptsAsync(List<Task<AttemptResult>> activeTasks)
        {
            if (activeTasks == null || activeTasks.Count == 0)
                return;

            for (int i = 0; i < activeTasks.Count; i++)
            {
                try
                {
                    var result = await activeTasks[i].ConfigureAwait(false);
                    if (result.Success)
                    {
                        try
                        {
                            result.Socket.Dispose();
                        }
                        catch
                        {
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }
            }
        }

        private static List<ScheduledAttempt> BuildSchedule(
            IReadOnlyList<IPAddress> preferred,
            IReadOnlyList<IPAddress> alternate,
            HappyEyeballsOptions options)
        {
            var schedule = new List<ScheduledAttempt>(preferred.Count + alternate.Count);

            // RFC 8305 section 4: the families interleave (First Address Family Count 1), so the
            // first alternate address is the second attempt rather than the one after every
            // preferred address. Listing the preferred family first let two unroutable addresses
            // (IPv6 without an IPv6 route, SYNs dropped) fill MaxConcurrentAttempts before the first
            // IPv4 address was due, and the connect ran into its deadline (#362).
            // Due times: the first attempt at 0, the first alternate at FamilyStaggerDelay, each
            // later attempt AttemptSpacingDelay after the previous one (RFC 8305 section 5).
            int count = Math.Max(preferred.Count, alternate.Count);
            for (int i = 0; i < count; i++)
            {
                if (i < preferred.Count)
                    schedule.Add(new ScheduledAttempt(preferred[i], NextDue(schedule, options, firstAlternate: false)));
                if (i < alternate.Count)
                    schedule.Add(new ScheduledAttempt(alternate[i], NextDue(schedule, options, firstAlternate: i == 0)));
            }

            return schedule;
        }

        private static TimeSpan NextDue(List<ScheduledAttempt> schedule, HappyEyeballsOptions options, bool firstAlternate)
        {
            if (schedule.Count == 0)
                return TimeSpan.Zero;

            // The first alternate always follows the first preferred attempt (Partition keeps the
            // preferred list non-empty whenever an alternate exists).
            if (firstAlternate)
                return options.FamilyStaggerDelay;

            return schedule[schedule.Count - 1].Due + options.AttemptSpacingDelay;
        }

        private static async Task<Socket> ConnectSequentialAsync(
            IReadOnlyList<IPAddress> addresses,
            int port,
            CancellationToken cancellationToken,
            Func<IPAddress, int, CancellationToken, Task<Socket>> connector)
        {
            Exception last = null;
            for (int i = 0; i < addresses.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await connector(addresses[i], port, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw last ?? new SocketException((int)SocketError.HostUnreachable);
        }

        private static async Task<AttemptResult> AttemptAsync(
            IPAddress address,
            int port,
            CancellationToken cancellationToken,
            Func<IPAddress, int, CancellationToken, Task<Socket>> connector)
        {
            try
            {
                var socket = await connector(address, port, cancellationToken).ConfigureAwait(false);
                return new AttemptResult
                {
                    Address = address,
                    Socket = socket
                };
            }
            catch (Exception ex)
            {
                return new AttemptResult
                {
                    Address = address,
                    Exception = ex
                };
            }
        }

        private static List<IPAddress> Deduplicate(IEnumerable<IPAddress> addresses)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<IPAddress>();

            foreach (var address in addresses)
            {
                if (address == null)
                    continue;

                var key = address.AddressFamily + ":" + address;
                if (seen.Add(key))
                    result.Add(address);
            }

            return result;
        }

        private static (List<IPAddress> preferred, List<IPAddress> alternate) Partition(
            IReadOnlyList<IPAddress> addresses,
            bool preferIpv6)
        {
            var primaryFamily = preferIpv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
            var secondaryFamily = preferIpv6 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;

            var preferred = new List<IPAddress>();
            var alternate = new List<IPAddress>();

            for (int i = 0; i < addresses.Count; i++)
            {
                var address = addresses[i];
                if (address.AddressFamily == primaryFamily)
                    preferred.Add(address);
                else if (address.AddressFamily == secondaryFamily)
                    alternate.Add(address);
                else
                    preferred.Add(address);
            }

            if (preferred.Count == 0)
            {
                preferred.AddRange(alternate);
                alternate.Clear();
            }

            return (preferred, alternate);
        }

        /// <summary>
        /// Enables SO_KEEPALIVE (RFC 1122 section 4.2.3.6) so the OS probes idle pooled
        /// connections. The probe timing keeps the OS defaults: Unity's .NET Standard 2.1 profile
        /// has no TcpKeepAliveTime/TcpKeepAliveInterval options (configurable socket tuning is
        /// planned with Phase 23). A target that rejects the option keeps working without it
        /// (#47; device behavior is needs-device-validation under #99).
        /// </summary>
        internal static void ConfigureKeepAlive(Socket socket)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }
            catch (SocketException) { }
            catch (PlatformNotSupportedException) { }
            catch (NotSupportedException) { }
        }

        private static async Task<Socket> ConnectEndpointAsync(
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            Socket socket = null;
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                ConfigureKeepAlive(socket);

                // Socket.ConnectAsync(EndPoint) takes no token. On cancellation the wait is
                // abandoned with the connect task observed, and the catch below disposes the
                // socket, which ends the connect; its fault is never unobserved (#172).
                var connectTask = socket.ConnectAsync(new IPEndPoint(address, port));
                await AbandonableTask.WaitAsync(connectTask, cancellationToken).ConfigureAwait(false);

                return socket;
            }
            catch
            {
                if (socket != null)
                {
                    try { socket.Dispose(); } catch { }
                }

                throw;
            }
        }
    }
}
