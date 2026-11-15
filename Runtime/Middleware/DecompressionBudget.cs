using System.IO;
using System.Threading;

namespace TurboHTTP.Middleware
{
    /// <summary>
    /// Work bounds and cancellation shared by every <see cref="InflateLayerStream"/> of one decode chain (#774).
    /// The size limit on the decoded output only sees the outermost layer, so a body of stacked codings could
    /// make the inner layers inflate any amount of data, or read any amount of input, while producing nothing.
    /// Every layer charges the bytes it reads from its source here before it decodes them:
    /// <list type="bullet">
    /// <item>the layer on the wire charges the compressed input (a body of unknown length included) against
    /// the compressed-size limit;</item>
    /// <item>every other layer reads the output of the layer below it, which is charged against one total
    /// for all intermediate output.</item>
    /// </list>
    /// Exceeding either fails the body with an <see cref="IOException"/>, which the handler surfaces as a
    /// <c>UHttpException</c>.
    /// </summary>
    /// <remarks>
    /// Threading: one reader at a time (the response body source contract). The counters are written only by
    /// the read in progress; <see cref="CancellationToken"/> is set by the owner before each read and read by
    /// that read's synchronous refill steps, which run on the same logical flow (an offloaded read is published
    /// by <c>Task.Run</c>), so no field needs synchronization.
    /// </remarks>
    internal sealed class DecompressionBudget
    {
        private readonly long _maxWireBytes;
        private readonly long _maxIntermediateBytes;
        private long _wireBytes;
        private long _intermediateBytes;

        /// <param name="maxWireBytes">Compressed input the wire layer may read.</param>
        /// <param name="maxIntermediateBytes">Output of all inner layers together.</param>
        /// <param name="cancellationToken">Initial token observed by synchronous source reads.</param>
        internal DecompressionBudget(long maxWireBytes, long maxIntermediateBytes, CancellationToken cancellationToken)
        {
            _maxWireBytes = maxWireBytes;
            _maxIntermediateBytes = maxIntermediateBytes;
            CancellationToken = cancellationToken;
        }

        /// <summary>
        /// The token of the read in progress. Synchronous source reads (the platform inflater can pull its input
        /// through <c>Stream.Read</c> even inside <c>ReadAsync</c>) have no token argument and observe this one.
        /// </summary>
        internal CancellationToken CancellationToken { get; set; }

        internal long WireBytes => _wireBytes;

        internal long IntermediateBytes => _intermediateBytes;

        /// <summary>Charges <paramref name="bytes"/> read by a layer from its source.</summary>
        /// <param name="bytes">Bytes the layer read.</param>
        /// <param name="wire">True for the layer that reads the wire bytes.</param>
        /// <exception cref="IOException">The read passed its limit.</exception>
        internal void Charge(int bytes, bool wire)
        {
            if (bytes <= 0)
                return;

            if (wire)
            {
                _wireBytes += bytes;
                if (_wireBytes > _maxWireBytes)
                {
                    throw new IOException(
                        $"Compressed response body exceeded the maximum size ({_maxWireBytes} bytes).");
                }

                return;
            }

            _intermediateBytes += bytes;
            if (_intermediateBytes > _maxIntermediateBytes)
            {
                throw new IOException(
                    "Response decompression exceeded the maximum intermediate size of stacked content codings " +
                    $"({_maxIntermediateBytes} bytes).");
            }
        }

        /// <summary>Throws when the read in progress was cancelled.</summary>
        internal void ThrowIfCancellationRequested()
        {
            CancellationToken.ThrowIfCancellationRequested();
        }
    }
}
