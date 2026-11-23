using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Tests
{
    /// <summary>
    /// A no-progress bound for long transfers (#1272): <see cref="Token"/> is cancelled only when
    /// <see cref="Progress"/> has not been called for <see cref="StallTimeout"/>, so a slow device
    /// that keeps moving bytes is never cut by a wall-clock total, while a stalled transfer still ends.
    /// </summary>
    internal sealed class ProgressWatchdog : IDisposable
    {
        private readonly CancellationTokenSource _cts;

        public ProgressWatchdog(TimeSpan stallTimeout)
        {
            StallTimeout = stallTimeout;
            _cts = new CancellationTokenSource(stallTimeout);
        }

        /// <summary>The longest time without progress before <see cref="Token"/> is cancelled.</summary>
        public TimeSpan StallTimeout { get; }

        /// <summary>Cancelled when no progress was reported for <see cref="StallTimeout"/>.</summary>
        public CancellationToken Token => _cts.Token;

        /// <summary>Whether the watchdog fired.</summary>
        public bool Expired => _cts.IsCancellationRequested;

        /// <summary>Re-arms the watchdog for another <see cref="StallTimeout"/>; no effect once it fired.</summary>
        public void Progress() => _cts.CancelAfter(StallTimeout);

        /// <summary>
        /// Reads <paramref name="source"/> to its end with <see cref="Token"/>, reporting progress after
        /// every read, and returns the byte count. A stall fails with a <see cref="TimeoutException"/>
        /// that names how many bytes arrived; comparing the count with the expected total is the caller's.
        /// </summary>
        public async Task<long> ReadToEndAsync(Stream source, byte[] buffer)
        {
            long received = 0;
            try
            {
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(), Token).ConfigureAwait(false);
                    if (read == 0)
                        return received;
                    received += read;
                    Progress();
                }
            }
            catch (OperationCanceledException ex) when (Expired)
            {
                throw new TimeoutException(
                    $"No bytes arrived for {StallTimeout.TotalSeconds} s; the transfer stalled after {received} bytes.", ex);
            }
        }

        public void Dispose() => _cts.Dispose();
    }
}
