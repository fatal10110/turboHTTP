using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

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

            var familyOrder = Partition(deduped, options.PreferIpv6);
            var preferred = familyOrder.preferred;
            var alternate = familyOrder.alternate;

            if (!options.Enable || preferred.Count == 0 || alternate.Count == 0)
            {
                return await ConnectSequentialAsync(deduped, port, cancellationToken, connector).ConfigureAwait(false);
            }

            var schedule = BuildSchedule(preferred, alternate, options);
            CancellationTokenSource timeoutCts = connectTimeout > TimeSpan.Zero && connectTimeout != Timeout.InfiniteTimeSpan
                ? new CancellationTokenSource(connectTimeout)
                : null;
            CancellationTokenSource linkedCts = null;

            try
            {
                linkedCts = timeoutCts != null
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
                    : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var ct = linkedCts.Token;

                var activeTasks = new List<Task<AttemptResult>>();
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
                        var attempt = schedule[scheduleIndex++];
                        activeTasks.Add(AttemptAsync(attempt.Address, port, ct, connector));
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

                    activeTasks.Remove(completedTask);

                    var result = await completedTask.ConfigureAwait(false);
                    if (result.Success)
                    {
                        linkedCts.Cancel();
                        await DrainLosersAsync(activeTasks, result.Socket).ConfigureAwait(false);
                        return result.Socket;
                    }

                    if (result.Exception != null)
                        failures.Add(result.Exception);
                }

                if (timeoutCts != null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    throw new TimeoutException("Happy Eyeballs connection timed out.");

                cancellationToken.ThrowIfCancellationRequested();

                if (failures.Count == 0)
                    throw new SocketException((int)SocketError.HostUnreachable);

                throw new AggregateException("All Happy Eyeballs connection attempts failed.", failures);
            }
            finally
            {
                try { linkedCts?.Dispose(); } catch { }
                try { timeoutCts?.Dispose(); } catch { }
            }
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

            for (int i = 0; i < preferred.Count; i++)
            {
                schedule.Add(new ScheduledAttempt(
                    preferred[i],
                    TimeSpan.FromTicks(options.AttemptSpacingDelay.Ticks * i)));
            }

            for (int i = 0; i < alternate.Count; i++)
            {
                schedule.Add(new ScheduledAttempt(
                    alternate[i],
                    options.FamilyStaggerDelay + TimeSpan.FromTicks(options.AttemptSpacingDelay.Ticks * i)));
            }

            schedule.Sort((a, b) => a.Due.CompareTo(b.Due));
            return schedule;
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

                var connectTask = socket.ConnectAsync(new IPEndPoint(address, port));
                if (!cancellationToken.CanBeCanceled)
                {
                    await connectTask.ConfigureAwait(false);
                }
                else
                {
                    var cancelSignal = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    using var registration = cancellationToken.Register(
                        () => cancelSignal.TrySetResult(true));
                    var completed = await Task.WhenAny(connectTask, cancelSignal.Task).ConfigureAwait(false);
                    if (!ReferenceEquals(completed, connectTask))
                    {
                        try { socket.Dispose(); } catch { }
                        throw new OperationCanceledException(cancellationToken);
                    }

                    await connectTask.ConfigureAwait(false);
                }

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
