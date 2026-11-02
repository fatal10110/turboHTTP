using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>Outcome class of <see cref="ServerCertificateVerifier.Verify"/>.</summary>
    /// <remarks>
    /// Fail-closed convention (#278): the zero value is <see cref="NotVerified"/>, a rejection, so a
    /// default or unset result never reads as trusted. Success has an explicit non-zero value.
    /// </remarks>
    internal enum CertificateVerificationStatus
    {
        /// <summary>No verification happened (the default value); always a rejection.</summary>
        NotVerified = 0,

        /// <summary>The chain builds to a trusted anchor with no error.</summary>
        Trusted = 1,

        /// <summary>The chain does not end at a trusted anchor (unknown CA, self-signed, partial).</summary>
        UntrustedRoot = 2,

        /// <summary>A certificate in the chain is expired or not yet valid.</summary>
        NotTimeValid = 3,

        /// <summary>Any other path error: signature, basic constraints, name constraints, usage, encoding.</summary>
        Invalid = 4,

        /// <summary>No certificate verifier exists on this platform; the chain is rejected.</summary>
        NoVerifier = 5,
    }

    /// <summary>Result of <see cref="ServerCertificateVerifier.Verify"/>.</summary>
    /// <remarks>
    /// <c>default</c> is <see cref="CertificateVerificationStatus.NotVerified"/> without a path, a
    /// rejection. Consumers accept a chain only through <see cref="IsTrusted"/>.
    /// </remarks>
    internal readonly struct CertificateVerificationResult
    {
        internal CertificateVerificationResult(
            CertificateVerificationStatus status, string detail, byte[][] path)
        {
            Status = status;
            Detail = detail;
            Path = path;
        }

        /// <summary>A success with the path the verifier built (leaf first, anchor last).</summary>
        internal static CertificateVerificationResult Trusted(byte[][] path)
            => new CertificateVerificationResult(CertificateVerificationStatus.Trusted, null, path);

        /// <summary>A rejection; carries no path.</summary>
        internal static CertificateVerificationResult Rejected(CertificateVerificationStatus status, string detail)
            => new CertificateVerificationResult(status, detail, null);

        /// <summary>Outcome class.</summary>
        internal CertificateVerificationStatus Status { get; }

        /// <summary>Diagnostic detail (chain status flags); never certificate contents.</summary>
        internal string Detail { get; }

        /// <summary>
        /// The verified path, DER, leaf first and trust anchor last. Set only for
        /// <see cref="CertificateVerificationStatus.Trusted"/>.
        /// </summary>
        internal byte[][] Path { get; }

        /// <summary>
        /// The only success test: <see cref="CertificateVerificationStatus.Trusted"/> with a
        /// non-empty path. Any other status, and <c>Trusted</c> without a path, is a rejection.
        /// </summary>
        internal bool IsTrusted
            => Status == CertificateVerificationStatus.Trusted && Path != null && Path.Length > 0;
    }

    /// <summary>The host OS family the verifier is compiled for (selects the native verifier).</summary>
    internal enum VerifierPlatform
    {
        /// <summary>iOS/Android devices, or a build without a Unity platform define.</summary>
        Other = 0,

        /// <summary>Windows Editor or Windows standalone player: crypt32.</summary>
        Windows = 1,

        /// <summary>macOS Editor or macOS standalone player: Security.framework SecTrust.</summary>
        MacOS = 2,

        /// <summary>Linux Editor or Linux standalone player: no verifier, BouncyCastle unsupported.</summary>
        Linux = 3,
    }

    /// <summary>
    /// Certificate path validation outside the TLS engine (#33, decision A on #141): the
    /// BouncyCastle provider hands over the DER chain the server sent and gets back the path the
    /// platform built (RFC 5280 section 6). Host-name matching is not done here
    /// (<see cref="TlsCertificateNameMatcher"/>).
    /// </summary>
    /// <remarks>
    /// <para>Every platform verifies through the OS verifier, run by
    /// <see cref="PlatformVerifierRunner"/> with the platform trust store and policy, revocation off
    /// (decision A: no OCSP/CRL; the provider does not request staples either) and no network
    /// fetch: certificate evaluation runs synchronously on the handshake thread and the handshake
    /// deadline cannot interrupt it (#142). Windows Editor and players use crypt32
    /// (<see cref="WindowsCertificateVerifier"/>), macOS Editor and players Security.framework
    /// SecTrust (<see cref="MacSecTrustVerifier"/>), iOS SecTrust through the native bridge
    /// (<see cref="AppleTrustVerifier"/>), all installed here; Android
    /// <c>X509TrustManagerExtensions</c> is registered by the BouncyCastle glue through
    /// <see cref="RegisterPlatformVerifier"/>, since it needs UnityEngine's JNI (#239, #248).
    /// Linux has no verifier: BouncyCastle is unsupported there and every chain is rejected with
    /// <see cref="CertificateVerificationStatus.NoVerifier"/>, as on any platform without a
    /// registered verifier. Any bridge failure or timeout is a rejection too. Failing closed is
    /// required; this path never accepts a certificate the platform would reject.</para>
    /// <para>Test trust anchors (<see cref="AddTestTrustAnchor"/>) exist only for the loopback
    /// test fixtures. Production code never adds one; with none registered the verifier trusts
    /// exactly the platform roots. The Windows and macOS verifiers take the anchors as their only
    /// roots; elsewhere (devices, Linux) the anchors are served by <see cref="X509Chain"/>, which
    /// production never reaches.</para>
    /// </remarks>
    internal static class ServerCertificateVerifier
    {
        /// <summary>Longest chain accepted from a server; longer ones are rejected unparsed.</summary>
        internal const int MaxChainLength = 10;

        /// <summary>
        /// Upper bound for any URL retrieval a chain engine is configured with (revocation is off;
        /// the platform verifiers also disable network fetch). Well under
        /// <c>TurboTlsClient.DefaultHandshakeTimeoutMillis</c> (30 s).
        /// </summary>
        internal static readonly TimeSpan UrlRetrievalTimeout = TimeSpan.FromSeconds(5);

        /// <summary>id-kp-serverAuth (RFC 5280 4.2.1.12), the application policy required of the path.</summary>
        internal const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

        private static readonly object TestAnchorLock = new object();

        // Copy-on-write snapshot: written under TestAnchorLock, read lock-free with Volatile.
        private static byte[][] s_testTrustAnchors = Array.Empty<byte[]>();

        // Set once (Interlocked), read with Volatile. iOS, Windows and macOS install their verifier
        // here; Android's is registered by the BouncyCastle glue (engine-referencing, JNI). The
        // Editor defines the build target's UNITY_IOS/UNITY_STANDALONE_* too, so the Editor cases
        // test UNITY_EDITOR_* first.
        private static PlatformCertificateVerifier s_platformVerifier =
#if UNITY_IOS && !UNITY_EDITOR
            AppleTrustVerifier.Verify;
#elif UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
            WindowsCertificateVerifier.Verify;
#elif UNITY_EDITOR_OSX || (UNITY_STANDALONE_OSX && !UNITY_EDITOR)
            MacSecTrustVerifier.Verify;
#else
            null;
#endif

        /// <summary>The OS family this build verifies on.</summary>
        internal static VerifierPlatform CurrentPlatform
        {
            get
            {
#if UNITY_IOS && !UNITY_EDITOR
                return VerifierPlatform.Other;
#elif UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
                return VerifierPlatform.Windows;
#elif UNITY_EDITOR_OSX || (UNITY_STANDALONE_OSX && !UNITY_EDITOR)
                return VerifierPlatform.MacOS;
#elif UNITY_EDITOR_LINUX || (UNITY_STANDALONE_LINUX && !UNITY_EDITOR)
                return VerifierPlatform.Linux;
#else
                return VerifierPlatform.Other;
#endif
            }
        }

        /// <summary>
        /// True where the installed verifier takes test anchors itself (Windows, macOS), so test
        /// fixtures exercise the production verifier there.
        /// </summary>
        internal static bool PlatformVerifierTrustsTestAnchors
            => CurrentPlatform == VerifierPlatform.Windows || CurrentPlatform == VerifierPlatform.MacOS;

        /// <summary>Detail of the <see cref="CertificateVerificationStatus.NoVerifier"/> rejection.</summary>
        internal const string NoVerifierDetail =
            "no certificate verifier on this platform; BouncyCastle rejects every server certificate here";

        /// <summary>
        /// Detail of the <see cref="CertificateVerificationStatus.NoVerifier"/> rejection on Linux
        /// (#248): there is no platform verifier to delegate to, and no bundled roots.
        /// </summary>
        internal const string LinuxUnsupportedDetail =
            "TlsBackend.BouncyCastle is unsupported on Linux: there is no platform certificate verifier, " +
            "so every server certificate is rejected; use TlsBackend.Auto or TlsBackend.SslStream";

        /// <summary>The <see cref="CertificateVerificationStatus.NoVerifier"/> detail for <paramref name="platform"/>.</summary>
        internal static string NoVerifierDetailFor(VerifierPlatform platform)
            => platform == VerifierPlatform.Linux ? LinuxUnsupportedDetail : NoVerifierDetail;

        /// <summary>
        /// Installs the native verifier for this platform once; later calls are ignored. Returns
        /// whether <paramref name="verifier"/> was installed.
        /// </summary>
        internal static bool RegisterPlatformVerifier(PlatformCertificateVerifier verifier)
        {
            if (verifier == null)
                throw new ArgumentNullException(nameof(verifier));
            return Interlocked.CompareExchange(ref s_platformVerifier, verifier, null) == null;
        }

        /// <summary>
        /// Verifies <paramref name="chainDer"/> (leaf first, as sent by the server) for
        /// <paramref name="host"/> (platform form: <see cref="TlsHostName.ToTargetHost"/>) at
        /// <paramref name="verificationTimeUtc"/>. The platform verifiers also apply their own SSL
        /// host-name policy to <paramref name="host"/>; RFC 6125 matching stays with
        /// <see cref="TlsCertificateNameMatcher"/>.
        /// </summary>
        internal static CertificateVerificationResult Verify(
            IReadOnlyList<byte[]> chainDer, string host, DateTime verificationTimeUtc)
            => Verify(chainDer, host, verificationTimeUtc, Volatile.Read(ref s_platformVerifier),
                PlatformVerifierLimits.Default, PlatformVerifierTrustsTestAnchors, CurrentPlatform);

        /// <summary>
        /// <see cref="Verify(IReadOnlyList{byte[]}, string, DateTime)"/> with the platform choice
        /// explicit, so tests drive the native-verifier path in the Editor.
        /// </summary>
        internal static CertificateVerificationResult Verify(
            IReadOnlyList<byte[]> chainDer, string host, DateTime verificationTimeUtc,
            PlatformCertificateVerifier platformVerifier, PlatformVerifierLimits limits,
            bool verifierTrustsTestAnchors = false, VerifierPlatform platform = VerifierPlatform.Other)
        {
            if (chainDer == null || chainDer.Count == 0)
                return Fail(CertificateVerificationStatus.Invalid, "empty certificate chain");

            if (chainDer.Count > MaxChainLength)
                return Fail(CertificateVerificationStatus.Invalid,
                    $"certificate chain longer than {MaxChainLength}");

            var anchors = Volatile.Read(ref s_testTrustAnchors);

            // Test anchors (loopback fixtures) only: where the platform verifier cannot take them
            // (devices, Linux), X509Chain serves them. Production registers none and never gets here.
            if (anchors.Length > 0 && !(verifierTrustsTestAnchors && platformVerifier != null))
                return VerifyWithX509Chain(chainDer, verificationTimeUtc, anchors);

            if (platformVerifier == null)
                return Fail(CertificateVerificationStatus.NoVerifier, NoVerifierDetailFor(platform));

            return PlatformVerifierRunner.Run(platformVerifier, chainDer, host, verificationTimeUtc, limits);
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
            // EKU is chained like SslStream's server validation: every CA on the path must allow
            // serverAuth, so a clientAuth-only intermediate cannot issue a usable server leaf.
            policy.ApplicationPolicy.Add(new Oid(ServerAuthOid));
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
                    return CertificateVerificationResult.Trusted(path);

                const X509ChainStatusFlags untrusted =
                    X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain;
                if ((flags & untrusted) != 0)
                    return Fail(CertificateVerificationStatus.UntrustedRoot, flags.ToString());

                if ((flags & ~(X509ChainStatusFlags.NotTimeValid | X509ChainStatusFlags.NotTimeNested)) == 0)
                    return Fail(CertificateVerificationStatus.NotTimeValid, flags.ToString());

                return Fail(CertificateVerificationStatus.Invalid, flags.ToString());
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // Broad on purpose: platform chain engines do not limit themselves to
                // CryptographicException (Unity Mono's X509ChainImplMono.Build rethrows as
                // ArgumentException, keys it cannot handle raise NotSupportedException). Any failure
                // to evaluate the path is a certificate rejection, never a transient I/O error.
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

        internal static bool SameBytes(byte[] a, byte[] b)
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
            => CertificateVerificationResult.Rejected(status, detail);
    }
}
