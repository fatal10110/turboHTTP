using System;
using System.Collections.Generic;
using System.Threading;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// A native certificate path verifier (iOS SecTrust, Android
    /// <c>X509TrustManagerExtensions</c>; #239), run by <see cref="PlatformVerifierRunner"/>.
    /// </summary>
    /// <remarks>
    /// <para>Contract for implementations:</para>
    /// <list type="bullet">
    /// <item>It runs on a dedicated worker thread created for this one call, which exits right
    /// after it returns (a JNI bridge may attach and detach it).</item>
    /// <item><paramref name="chainDer"/> is a private copy of the chain the server sent, leaf
    /// first. <paramref name="host"/> is the target host in platform form (A-label, bare IP
    /// address without brackets).</item>
    /// <item>It returns <see cref="CertificateVerificationStatus.Trusted"/> only with the path the
    /// platform built (DER, leaf first, anchor last). <see cref="CertificateVerificationResult.Detail"/>
    /// never carries certificate contents or platform error text that could.</item>
    /// <item>It never fetches from the network. It may throw: any exception is a rejection.</item>
    /// </list>
    /// </remarks>
    internal delegate CertificateVerificationResult PlatformCertificateVerifier(
        byte[][] chainDer, string host, DateTime verificationTimeUtc);

    /// <summary>
    /// Bounds for <see cref="PlatformVerifierRunner"/>: how long a native call may take, and how
    /// many timed-out calls may still be running before every new verification is rejected
    /// without starting another thread.
    /// </summary>
    internal sealed class PlatformVerifierLimits
    {
        /// <summary>
        /// Production bounds: 10 s per call (under <c>TurboTlsClient.DefaultHandshakeTimeoutMillis</c>,
        /// 30 s, which cannot interrupt a synchronous call, #142) and 4 abandoned calls.
        /// </summary>
        internal static readonly PlatformVerifierLimits Default =
            new PlatformVerifierLimits(TimeSpan.FromSeconds(10), 4);

        private int _abandoned;

        internal PlatformVerifierLimits(TimeSpan timeout, int maxAbandoned)
        {
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            if (maxAbandoned < 1)
                throw new ArgumentOutOfRangeException(nameof(maxAbandoned));

            Timeout = timeout;
            MaxAbandoned = maxAbandoned;
        }

        /// <summary>Longest a native call may run before the chain is rejected.</summary>
        internal TimeSpan Timeout { get; }

        /// <summary>Timed-out calls still running at which new verifications fail fast.</summary>
        internal int MaxAbandoned { get; }

        /// <summary>Timed-out calls that have not returned yet.</summary>
        internal int Abandoned => Volatile.Read(ref _abandoned);

        internal void AddAbandoned() => Interlocked.Increment(ref _abandoned);

        internal void ReleaseAbandoned() => Interlocked.Decrement(ref _abandoned);
    }

    /// <summary>
    /// Runs a <see cref="PlatformCertificateVerifier"/> with fail-closed handling: a bridge that
    /// throws, times out, returns an unknown status or a path that is empty, oversized, malformed
    /// or does not start at the server's leaf is a rejection, never <c>Trusted</c>.
    /// </summary>
    /// <remarks>
    /// The handshake thread waits for the worker with <see cref="Thread.Join(TimeSpan)"/>; it is
    /// already synchronous inside BouncyCastle's input processing (#142), so no async work is
    /// blocked on. A worker that outlives the timeout is abandoned (its result is dropped) and
    /// counted in <see cref="PlatformVerifierLimits.Abandoned"/> until it returns.
    /// </remarks>
    internal static class PlatformVerifierRunner
    {
        /// <summary>
        /// Longest built path accepted from a platform: the longest chain a server may send plus
        /// the anchor the platform appends.
        /// </summary>
        internal const int MaxPathLength = ServerCertificateVerifier.MaxChainLength + 1;

        internal static CertificateVerificationResult Run(
            PlatformCertificateVerifier verifier, IReadOnlyList<byte[]> chainDer, string host,
            DateTime verificationTimeUtc, PlatformVerifierLimits limits)
        {
            if (verifier == null)
            {
                return Fail(CertificateVerificationStatus.NoVerifier,
                    "no certificate verifier on this platform; BouncyCastle rejects every server certificate here");
            }

            if (string.IsNullOrEmpty(host))
                return Fail(CertificateVerificationStatus.Invalid, "no target host for the platform verifier");

            if (limits.Abandoned >= limits.MaxAbandoned)
                return Fail(CertificateVerificationStatus.Invalid, "platform verifier unresponsive");

            var copy = new byte[chainDer.Count][];
            for (int i = 0; i < copy.Length; i++)
            {
                var der = chainDer[i];
                if (der == null || der.Length == 0)
                    return Fail(CertificateVerificationStatus.Invalid, "malformed certificate in chain");
                copy[i] = (byte[])der.Clone();
            }

            var call = new Call(verifier, copy, host, verificationTimeUtc, limits);
            var worker = new Thread(call.Execute) { IsBackground = true, Name = "TurboHTTP certificate verifier" };
            try
            {
                worker.Start();
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                return Fail(CertificateVerificationStatus.Invalid,
                    "platform verifier could not start: " + ex.GetType().Name);
            }

            // Join is a full synchronization point, so the worker's Result/Error writes are
            // visible. If the worker finishes between a timed-out Join and TryAbandon, the CAS
            // in TryAbandon fails and its Interlocked write in Execute orders the result.
            if (!worker.Join(limits.Timeout) && call.TryAbandon())
            {
                limits.AddAbandoned();
                return Fail(CertificateVerificationStatus.Invalid, "platform verifier timed out");
            }

            if (call.Error != null)
            {
                return Fail(CertificateVerificationStatus.Invalid,
                    "platform verifier failed: " + call.Error.GetType().Name);
            }

            return CheckResult(call.Result, chainDer[0]);
        }

        /// <summary>
        /// Accepts a bridge's <c>Trusted</c> only with a well-formed path that starts at the leaf
        /// the server sent (the one host-name and leaf checks ran on); passes rejections through
        /// without a path.
        /// </summary>
        internal static CertificateVerificationResult CheckResult(
            CertificateVerificationResult result, byte[] serverLeaf)
        {
            switch (result.Status)
            {
                case CertificateVerificationStatus.Trusted:
                    break;
                case CertificateVerificationStatus.UntrustedRoot:
                case CertificateVerificationStatus.NotTimeValid:
                case CertificateVerificationStatus.Invalid:
                case CertificateVerificationStatus.NoVerifier:
                    return Fail(result.Status, result.Detail ?? "rejected by the platform verifier");
                default:
                    return Fail(CertificateVerificationStatus.Invalid, "platform verifier returned an unknown status");
            }

            var path = result.Path;
            if (path == null || path.Length == 0)
                return Fail(CertificateVerificationStatus.Invalid, "platform verifier returned no path");

            if (path.Length > MaxPathLength)
            {
                return Fail(CertificateVerificationStatus.Invalid,
                    $"platform path longer than {MaxPathLength}");
            }

            foreach (var der in path)
            {
                // A DER certificate is a SEQUENCE; full parsing happens in the provider
                // (CheckPathSignatureAlgorithms), which rejects anything else.
                if (der == null || der.Length < 2 || der[0] != 0x30)
                    return Fail(CertificateVerificationStatus.Invalid, "malformed certificate in the platform path");
            }

            if (!ServerCertificateVerifier.SameBytes(path[0], serverLeaf))
            {
                return Fail(CertificateVerificationStatus.Invalid,
                    "platform path does not start at the server certificate");
            }

            return new CertificateVerificationResult(CertificateVerificationStatus.Trusted, null, path);
        }

        private static CertificateVerificationResult Fail(CertificateVerificationStatus status, string detail)
            => new CertificateVerificationResult(status, detail, null);

        private sealed class Call
        {
            private const int Running = 0;
            private const int Completed = 1;
            private const int Abandoned = 2;

            private readonly PlatformCertificateVerifier _verifier;
            private readonly byte[][] _chainDer;
            private readonly string _host;
            private readonly DateTime _verificationTimeUtc;
            private readonly PlatformVerifierLimits _limits;
            private int _state;

            internal Call(PlatformCertificateVerifier verifier, byte[][] chainDer, string host,
                DateTime verificationTimeUtc, PlatformVerifierLimits limits)
            {
                _verifier = verifier;
                _chainDer = chainDer;
                _host = host;
                _verificationTimeUtc = verificationTimeUtc;
                _limits = limits;
            }

            internal CertificateVerificationResult Result { get; private set; }

            internal Exception Error { get; private set; }

            internal void Execute()
            {
                try
                {
                    Result = _verifier(_chainDer, _host, _verificationTimeUtc);
                }
                catch (Exception ex)
                {
                    // Everything: an exception escaping a worker thread terminates the process.
                    Error = ex;
                }
                finally
                {
                    if (Interlocked.CompareExchange(ref _state, Completed, Running) == Abandoned)
                        _limits.ReleaseAbandoned();
                }
            }

            /// <summary>True when the worker had not finished; its result will be dropped.</summary>
            internal bool TryAbandon() => Interlocked.CompareExchange(ref _state, Abandoned, Running) == Running;
        }
    }
}
