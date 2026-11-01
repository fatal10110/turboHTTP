using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>Outcome class of <see cref="ServerCertificateVerifier.Verify"/>.</summary>
    internal enum CertificateVerificationStatus
    {
        /// <summary>The chain builds to a trusted anchor with no error.</summary>
        Trusted,

        /// <summary>The chain does not end at a trusted anchor (unknown CA, self-signed, partial).</summary>
        UntrustedRoot,

        /// <summary>A certificate in the chain is expired or not yet valid.</summary>
        NotTimeValid,

        /// <summary>Any other path error: signature, basic constraints, name constraints, usage, encoding.</summary>
        Invalid,

        /// <summary>No certificate verifier exists on this platform; the chain is rejected.</summary>
        NoVerifier,
    }

    /// <summary>Result of <see cref="ServerCertificateVerifier.Verify"/>.</summary>
    internal readonly struct CertificateVerificationResult
    {
        internal CertificateVerificationResult(
            CertificateVerificationStatus status, string detail, byte[][] path)
        {
            Status = status;
            Detail = detail;
            Path = path;
        }

        /// <summary>Outcome class.</summary>
        internal CertificateVerificationStatus Status { get; }

        /// <summary>Diagnostic detail (chain status flags); never certificate contents.</summary>
        internal string Detail { get; }

        /// <summary>
        /// The verified path, DER, leaf first and trust anchor last. Set only for
        /// <see cref="CertificateVerificationStatus.Trusted"/>.
        /// </summary>
        internal byte[][] Path { get; }
    }

    /// <summary>
    /// Certificate path validation outside the TLS engine (#33, decision A on #141): the
    /// BouncyCastle provider hands over the DER chain the server sent and gets back the path the
    /// platform built (RFC 5280 section 6). Host-name matching is not done here
    /// (<see cref="TlsCertificateNameMatcher"/>).
    /// </summary>
    /// <remarks>
    /// <para>Editor and desktop players use <see cref="X509Chain"/> on the same DER chain against
    /// the platform root store, with revocation checking off (decision A: no OCSP/CRL; the
    /// provider does not request staples either). The only network access <see cref="X509Chain"/>
    /// can make is fetching a missing intermediate, bounded by <see cref="UrlRetrievalTimeout"/>,
    /// which is well under the handshake deadline: certificate evaluation runs synchronously on
    /// the handshake thread and the handshake deadline cannot interrupt it (#142).</para>
    /// <para>iOS and Android devices have no verifier yet (native SecTrust / TrustManager bridges
    /// are tracked separately): every chain is rejected with
    /// <see cref="CertificateVerificationStatus.NoVerifier"/>. Failing closed is required; this
    /// path never accepts a certificate the platform would reject.</para>
    /// <para>Test trust anchors (<see cref="AddTestTrustAnchor"/>) exist only for the loopback
    /// test fixtures. Production code never adds one; with none registered the verifier trusts
    /// exactly the platform roots.</para>
    /// </remarks>
    internal static class ServerCertificateVerifier
    {
        /// <summary>Longest chain accepted from a server; longer ones are rejected unparsed.</summary>
        internal const int MaxChainLength = 10;

        /// <summary>
        /// Upper bound for any intermediate fetch <see cref="X509Chain"/> performs (revocation is
        /// off). Well under <c>TurboTlsClient.DefaultHandshakeTimeoutMillis</c> (30 s).
        /// </summary>
        internal static readonly TimeSpan UrlRetrievalTimeout = TimeSpan.FromSeconds(5);

        private static readonly object TestAnchorLock = new object();

        // Copy-on-write snapshot: written under TestAnchorLock, read lock-free with Volatile.
        private static byte[][] s_testTrustAnchors = Array.Empty<byte[]>();

        /// <summary>True on builds where no chain verifier exists (iOS/Android devices).</summary>
        internal static bool IsUnsupportedPlatform
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Verifies <paramref name="chainDer"/> (leaf first, as sent by the server) at
        /// <paramref name="verificationTimeUtc"/>.
        /// </summary>
        internal static CertificateVerificationResult Verify(
            IReadOnlyList<byte[]> chainDer, DateTime verificationTimeUtc)
        {
            if (chainDer == null || chainDer.Count == 0)
                return Fail(CertificateVerificationStatus.Invalid, "empty certificate chain");

            if (chainDer.Count > MaxChainLength)
                return Fail(CertificateVerificationStatus.Invalid,
                    $"certificate chain longer than {MaxChainLength}");

            var anchors = Volatile.Read(ref s_testTrustAnchors);

            if (IsUnsupportedPlatform && anchors.Length == 0)
            {
                return Fail(CertificateVerificationStatus.NoVerifier,
                    "no certificate verifier on this platform; BouncyCastle rejects every server certificate here");
            }

            return VerifyWithX509Chain(chainDer, verificationTimeUtc, anchors);
        }

        /// <summary>
        /// Test fixtures only: trust <paramref name="der"/> as an additional anchor, process-wide,
        /// until <see cref="RestoreTestTrustAnchors"/> removes it.
        /// </summary>
        internal static void AddTestTrustAnchor(byte[] der)
        {
            if (der == null || der.Length == 0)
                throw new ArgumentException("A DER certificate is required.", nameof(der));

            lock (TestAnchorLock)
            {
                var current = s_testTrustAnchors;
                foreach (var existing in current)
                {
                    if (SameBytes(existing, der))
                        return;
                }

                var next = new byte[current.Length + 1][];
                Array.Copy(current, next, current.Length);
                next[current.Length] = (byte[])der.Clone();
                Volatile.Write(ref s_testTrustAnchors, next);
            }
        }

        /// <summary>Test fixtures only: the current anchors, for a later restore.</summary>
        internal static byte[][] SnapshotTestTrustAnchors() => Volatile.Read(ref s_testTrustAnchors);

        /// <summary>Test fixtures only: replace the anchors (null or empty clears them).</summary>
        internal static void RestoreTestTrustAnchors(byte[][] snapshot)
        {
            lock (TestAnchorLock)
            {
                Volatile.Write(ref s_testTrustAnchors, snapshot ?? Array.Empty<byte[]>());
            }
        }

        /// <summary>
        /// The chain policy: revocation off (decision A), bounded fetch, the given time. Test anchors
        /// make an unknown root acceptable to <see cref="X509Chain.Build"/> only; the result is still
        /// rejected unless that root is one of the anchors.
        /// </summary>
        internal static void ConfigurePolicy(
            X509ChainPolicy policy, DateTime verificationTimeUtc, bool hasTestAnchors)
        {
            policy.RevocationMode = X509RevocationMode.NoCheck;
            policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            policy.UrlRetrievalTimeout = UrlRetrievalTimeout;
            policy.VerificationTime = verificationTimeUtc.ToLocalTime();
            policy.VerificationFlags = hasTestAnchors
                ? X509VerificationFlags.AllowUnknownCertificateAuthority
                : X509VerificationFlags.NoFlag;
        }

        private static CertificateVerificationResult VerifyWithX509Chain(
            IReadOnlyList<byte[]> chainDer, DateTime verificationTimeUtc, byte[][] anchors)
        {
            var owned = new List<X509Certificate2>(chainDer.Count + anchors.Length);
            X509Chain chain = null;
            try
            {
                X509Certificate2 leaf;
                try
                {
                    leaf = new X509Certificate2(chainDer[0]);
                    owned.Add(leaf);
                    chain = new X509Chain();
                    ConfigurePolicy(chain.ChainPolicy, verificationTimeUtc, anchors.Length > 0);

                    for (int i = 1; i < chainDer.Count; i++)
                    {
                        var intermediate = new X509Certificate2(chainDer[i]);
                        owned.Add(intermediate);
                        chain.ChainPolicy.ExtraStore.Add(intermediate);
                    }

                    foreach (var anchorDer in anchors)
                    {
                        var anchor = new X509Certificate2(anchorDer);
                        owned.Add(anchor);
                        chain.ChainPolicy.ExtraStore.Add(anchor);
                    }
                }
                catch (CryptographicException)
                {
                    return Fail(CertificateVerificationStatus.Invalid, "malformed certificate in chain");
                }
                catch (ArgumentException)
                {
                    return Fail(CertificateVerificationStatus.Invalid, "malformed certificate in chain");
                }

                // The return value is ignored on purpose: the status flags decide, so that
                // AllowUnknownCertificateAuthority (test anchors only) never accepts a foreign root.
                chain.Build(leaf);

                var elements = chain.ChainElements;
                if (elements.Count == 0)
                    return Fail(CertificateVerificationStatus.Invalid, "no certificate path");

                var flags = X509ChainStatusFlags.NoError;
                foreach (var status in chain.ChainStatus)
                    flags |= status.Status;

                var path = new byte[elements.Count][];
                for (int i = 0; i < elements.Count; i++)
                {
                    var element = elements[i];
                    foreach (var status in element.ChainElementStatus)
                        flags |= status.Status;
                    path[i] = element.Certificate.RawData;
                    owned.Add(element.Certificate);
                }

                // Revocation is off by decision; these flags carry no path error.
                flags &= ~(X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation);

                if ((flags & X509ChainStatusFlags.UntrustedRoot) != 0 && IsTestAnchor(path[path.Length - 1], anchors))
                    flags &= ~X509ChainStatusFlags.UntrustedRoot;

                if (flags == X509ChainStatusFlags.NoError)
                    return new CertificateVerificationResult(CertificateVerificationStatus.Trusted, null, path);

                const X509ChainStatusFlags untrusted =
                    X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain;
                if ((flags & untrusted) != 0)
                    return Fail(CertificateVerificationStatus.UntrustedRoot, flags.ToString());

                if ((flags & ~(X509ChainStatusFlags.NotTimeValid | X509ChainStatusFlags.NotTimeNested)) == 0)
                    return Fail(CertificateVerificationStatus.NotTimeValid, flags.ToString());

                return Fail(CertificateVerificationStatus.Invalid, flags.ToString());
            }
            catch (CryptographicException ex)
            {
                return Fail(CertificateVerificationStatus.Invalid, "chain evaluation failed: " + ex.GetType().Name);
            }
            finally
            {
                chain?.Reset();
                chain?.Dispose();
                foreach (var cert in owned)
                    cert.Dispose();
            }
        }

        private static bool IsTestAnchor(byte[] root, byte[][] anchors)
        {
            foreach (var anchor in anchors)
            {
                if (SameBytes(anchor, root))
                    return true;
            }
            return false;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        private static CertificateVerificationResult Fail(CertificateVerificationStatus status, string detail)
            => new CertificateVerificationResult(status, detail, null);
    }
}
