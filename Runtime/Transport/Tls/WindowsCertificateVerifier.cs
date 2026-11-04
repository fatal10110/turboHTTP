using System;
using System.Runtime.InteropServices;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// Server certificate path validation through Windows crypt32 (#248), the
    /// <see cref="PlatformCertificateVerifier"/> of the Windows Editor and Windows players:
    /// <c>CertGetCertificateChain</c> with the serverAuth usage required of the whole path (RFC 5280
    /// 4.2.1.12), then <c>CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_SSL)</c> for the host.
    /// </summary>
    /// <remarks>
    /// <para>The chain engine uses the current user's trust store (which includes the machine
    /// roots, enterprise roots and the disallowed store), at the caller's verification time, with
    /// revocation off (decision A) and no network: cached URLs only, no AIA fetch, no automatic root
    /// update (<see cref="PlatformCertificateVerifier"/> contract). A root the machine has not
    /// cached yet is therefore untrusted here even where SChannel would fetch it.</para>
    /// <para>Test anchors (loopback fixtures only) run through a private chain engine whose
    /// exclusive root store holds exactly those anchors; production registers none.</para>
    /// <para>Every handle (stores, certificate context, engine, chain context) is released once in
    /// <c>finally</c>. Compiled on every platform (the import resolves on first call), installed
    /// only on Windows.</para>
    /// </remarks>
    internal static unsafe class WindowsCertificateVerifier
    {
        private const string Crypt32 = "crypt32.dll";

        private const uint X509AsnEncoding = 0x00000001;
        private const uint Pkcs7AsnEncoding = 0x00010000;
        private const uint CertStoreAddAlways = 4;
        private const uint UsageMatchTypeAnd = 0;
        private const uint CertChainCacheOnlyUrlRetrieval = 0x00000004;
        private const uint CertChainDisableAuthRootAutoUpdate = 0x00000100;
        private const uint CertChainDisableAia = 0x00002000;
        private const uint AuthTypeServer = 2;
        private const uint CertChainPolicyIgnoreAllRevUnknownFlags = 0x00000F00;
        private const uint MaxCertificateBytes = 64 * 1024;

        private const uint ChainFlags =
            CertChainCacheOnlyUrlRetrieval | CertChainDisableAuthRootAutoUpdate | CertChainDisableAia;

        private static readonly IntPtr CertStoreProvMemory = (IntPtr)2;
        private static readonly IntPtr CertChainPolicySsl = (IntPtr)4;

        // id-kp-serverAuth as the NUL-terminated ANSI string crypt32 expects.
        private static readonly byte[] ServerAuthOidAnsi =
            System.Text.Encoding.ASCII.GetBytes(ServerCertificateVerifier.ServerAuthOid + "\0");

        // CERT_TRUST_STATUS.dwErrorStatus bits (wincrypt.h).
        internal const uint TrustIsNotTimeValid = 0x00000001;
        internal const uint TrustIsNotTimeNested = 0x00000002;
        internal const uint TrustIsRevoked = 0x00000004;
        internal const uint TrustIsNotSignatureValid = 0x00000008;
        internal const uint TrustIsNotValidForUsage = 0x00000010;
        internal const uint TrustIsUntrustedRoot = 0x00000020;
        internal const uint TrustRevocationStatusUnknown = 0x00000040;
        internal const uint TrustInvalidNameConstraints = 0x00000800;
        internal const uint TrustHasNotSupportedNameConstraint = 0x00001000;
        internal const uint TrustHasNotDefinedNameConstraint = 0x00002000;
        internal const uint TrustHasNotPermittedNameConstraint = 0x00004000;
        internal const uint TrustHasExcludedNameConstraint = 0x00008000;
        internal const uint TrustIsPartialChain = 0x00010000;
        internal const uint TrustIsOfflineRevocation = 0x01000000;

        private const uint NameConstraintErrors = TrustInvalidNameConstraints | TrustHasNotSupportedNameConstraint
            | TrustHasNotDefinedNameConstraint | TrustHasNotPermittedNameConstraint | TrustHasExcludedNameConstraint;

        // CERT_CHAIN_POLICY_STATUS.dwError values (winerror.h).
        internal const uint CertEExpired = 0x800B0101;
        internal const uint CertEUntrustedRoot = 0x800B0109;
        internal const uint CertEChaining = 0x800B010A;
        internal const uint CertECnNoMatch = 0x800B010F;

        /// <summary>Evaluates the chain with crypt32 (see the class remarks).</summary>
        internal static CertificateVerificationResult Verify(
            byte[][] chainDer, string host, DateTime verificationTimeUtc)
            => Evaluate(chainDer, host, verificationTimeUtc, ServerCertificateVerifier.SnapshotTestTrustAnchors());

        /// <summary>
        /// <see cref="Verify"/> with explicit test anchors: when there are any they are the only
        /// trusted roots, otherwise the platform store is.
        /// </summary>
        internal static CertificateVerificationResult Evaluate(
            byte[][] chainDer, string host, DateTime verificationTimeUtc, byte[][] testAnchors)
        {
            if (chainDer == null || chainDer.Length == 0)
                return Fail(CertificateVerificationStatus.Invalid, "empty certificate chain");
            if (string.IsNullOrEmpty(host))
                return Fail(CertificateVerificationStatus.Invalid, "no target host for the platform verifier");

            IntPtr extraStore = IntPtr.Zero;
            IntPtr anchorStore = IntPtr.Zero;
            IntPtr engine = IntPtr.Zero;
            IntPtr leaf = IntPtr.Zero;
            IntPtr chain = IntPtr.Zero;
            try
            {
                extraStore = CertOpenStore(CertStoreProvMemory, 0, IntPtr.Zero, 0, IntPtr.Zero);
                if (extraStore == IntPtr.Zero)
                    return Fail(CertificateVerificationStatus.Invalid, "crypt32: could not open a memory store");

                for (int i = 0; i < chainDer.Length; i++)
                {
                    if (!AddCertificate(extraStore, chainDer[i], i == 0 ? &leaf : null))
                        return Fail(CertificateVerificationStatus.Invalid, "malformed certificate in chain");
                }

                uint timeoutMs = (uint)ServerCertificateVerifier.UrlRetrievalTimeout.TotalMilliseconds;
                if (testAnchors != null && testAnchors.Length > 0)
                {
                    anchorStore = CertOpenStore(CertStoreProvMemory, 0, IntPtr.Zero, 0, IntPtr.Zero);
                    if (anchorStore == IntPtr.Zero)
                        return Fail(CertificateVerificationStatus.Invalid, "crypt32: could not open a memory store");
                    foreach (var anchor in testAnchors)
                    {
                        if (!AddCertificate(anchorStore, anchor, null))
                            return Fail(CertificateVerificationStatus.Invalid, "malformed test anchor");
                    }

                    var config = new CERT_CHAIN_ENGINE_CONFIG
                    {
                        cbSize = (uint)sizeof(CERT_CHAIN_ENGINE_CONFIG),
                        dwFlags = CertChainCacheOnlyUrlRetrieval,
                        dwUrlRetrievalTimeout = timeoutMs,
                        hExclusiveRoot = anchorStore,
                    };
                    if (CertCreateCertificateChainEngine(&config, out engine) == 0 || engine == IntPtr.Zero)
                    {
                        engine = IntPtr.Zero;
                        return Fail(CertificateVerificationStatus.Invalid, "crypt32: could not create the test-anchor chain engine");
                    }
                }

                long fileTime = verificationTimeUtc.ToUniversalTime().ToFileTimeUtc();
                bool built;
                fixed (byte* oid = ServerAuthOidAnsi)
                {
                    IntPtr usage = (IntPtr)oid;
                    var para = new CERT_CHAIN_PARA
                    {
                        cbSize = (uint)sizeof(CERT_CHAIN_PARA),
                        dwUrlRetrievalTimeout = timeoutMs,
                    };
                    para.RequestedUsage.dwType = UsageMatchTypeAnd;
                    para.RequestedUsage.Usage.cUsageIdentifier = 1;
                    para.RequestedUsage.Usage.rgpszUsageIdentifier = (IntPtr)(&usage);

                    built = CertGetCertificateChain(engine, leaf, &fileTime, extraStore, &para, ChainFlags,
                        IntPtr.Zero, out chain) != 0 && chain != IntPtr.Zero;
                }

                if (!built)
                    return Map(false, 0, false, 0, null);

                var context = (CERT_CHAIN_CONTEXT*)chain.ToPointer();
                uint errorStatus = context->TrustStatus.dwErrorStatus;
                if (PathErrors(errorStatus) != 0)
                    return Map(true, errorStatus, false, 0, null);

                bool policyEvaluated;
                uint policyError;
                fixed (char* serverName = host)
                {
                    var ssl = new SSL_EXTRA_CERT_CHAIN_POLICY_PARA
                    {
                        cbSize = (uint)sizeof(SSL_EXTRA_CERT_CHAIN_POLICY_PARA),
                        dwAuthType = AuthTypeServer,
                        fdwChecks = 0,
                        pwszServerName = (IntPtr)serverName,
                    };
                    var policyPara = new CERT_CHAIN_POLICY_PARA
                    {
                        cbSize = (uint)sizeof(CERT_CHAIN_POLICY_PARA),
                        // Revocation is off by decision; nothing else is relaxed.
                        dwFlags = CertChainPolicyIgnoreAllRevUnknownFlags,
                        pvExtraPolicyPara = (IntPtr)(&ssl),
                    };
                    var status = new CERT_CHAIN_POLICY_STATUS { cbSize = (uint)sizeof(CERT_CHAIN_POLICY_STATUS) };
                    policyEvaluated = CertVerifyCertificateChainPolicy(CertChainPolicySsl, chain, &policyPara, &status) != 0;
                    policyError = status.dwError;
                }

                byte[][] path = policyEvaluated && policyError == 0 ? ReadPath(context) : null;
                return Map(true, errorStatus, policyEvaluated, policyError, path);
            }
            finally
            {
                if (chain != IntPtr.Zero)
                    CertFreeCertificateChain(chain);
                if (engine != IntPtr.Zero)
                    CertFreeCertificateChainEngine(engine);
                if (leaf != IntPtr.Zero)
                    CertFreeCertificateContext(leaf);
                if (anchorStore != IntPtr.Zero)
                    CertCloseStore(anchorStore, 0);
                if (extraStore != IntPtr.Zero)
                    CertCloseStore(extraStore, 0);
            }
        }

        /// <summary>
        /// Maps a crypt32 outcome to a result. Trusted only when the chain was built with no path
        /// error, the SSL policy ran and passed, and a path was read (#278: nothing defaults to
        /// success). Path errors map like the other verifiers: untrusted or partial chain first,
        /// then validity period only, anything else invalid.
        /// </summary>
        internal static CertificateVerificationResult Map(
            bool chainBuilt, uint chainErrorStatus, bool policyEvaluated, uint policyError, byte[][] path)
        {
            if (!chainBuilt)
                return Fail(CertificateVerificationStatus.Invalid, "crypt32: certificate chain could not be built");

            uint errors = PathErrors(chainErrorStatus);
            if (errors != 0)
            {
                string detail = $"crypt32 chain status 0x{errors:X8}"
                    + ((errors & NameConstraintErrors) != 0 ? " (name constraints)" : "");
                if ((errors & (TrustIsUntrustedRoot | TrustIsPartialChain)) != 0)
                    return Fail(CertificateVerificationStatus.UntrustedRoot, detail);
                if ((errors & ~(TrustIsNotTimeValid | TrustIsNotTimeNested)) == 0)
                    return Fail(CertificateVerificationStatus.NotTimeValid, detail);
                return Fail(CertificateVerificationStatus.Invalid, detail);
            }

            if (!policyEvaluated)
                return Fail(CertificateVerificationStatus.Invalid, "crypt32: SSL policy could not be evaluated");

            if (policyError != 0)
            {
                string detail = $"crypt32 SSL policy error 0x{policyError:X8}";
                switch (policyError)
                {
                    case CertEUntrustedRoot:
                    case CertEChaining:
                        return Fail(CertificateVerificationStatus.UntrustedRoot, detail);
                    case CertEExpired:
                        return Fail(CertificateVerificationStatus.NotTimeValid, detail);
                    case CertECnNoMatch:
                        return Fail(CertificateVerificationStatus.Invalid, detail + " (host name mismatch)");
                    default:
                        return Fail(CertificateVerificationStatus.Invalid, detail);
                }
            }

            if (path == null || path.Length == 0)
                return Fail(CertificateVerificationStatus.Invalid, "crypt32: no certificate path");

            return CertificateVerificationResult.Trusted(path);
        }

        // Revocation is off by decision (decision A); these bits carry no path error.
        private static uint PathErrors(uint errorStatus)
            => errorStatus & ~(TrustRevocationStatusUnknown | TrustIsOfflineRevocation);

        private static bool AddCertificate(IntPtr store, byte[] der, IntPtr* context)
        {
            if (der == null || der.Length == 0 || der.Length > MaxCertificateBytes)
                return false;
            fixed (byte* encoded = der)
            {
                return CertAddEncodedCertificateToStore(store, X509AsnEncoding | Pkcs7AsnEncoding, encoded,
                    (uint)der.Length, CertStoreAddAlways, context) != 0;
            }
        }

        // The path of the single simple chain, leaf first, root last. Throws FormatException on any
        // inconsistency; the runner turns that into a rejection.
        private static byte[][] ReadPath(CERT_CHAIN_CONTEXT* context)
        {
            if (context->cChain != 1 || context->rgpChain == IntPtr.Zero)
                throw new FormatException("crypt32 returned no single certificate path");

            var simple = ((CERT_SIMPLE_CHAIN**)context->rgpChain.ToPointer())[0];
            if (simple == null || simple->rgpElement == IntPtr.Zero
                || simple->cElement == 0 || simple->cElement > PlatformVerifierRunner.MaxPathLength)
                throw new FormatException("crypt32 path length out of range");

            var elements = (CERT_CHAIN_ELEMENT**)simple->rgpElement.ToPointer();
            var path = new byte[simple->cElement][];
            for (int i = 0; i < path.Length; i++)
            {
                var element = elements[i];
                var cert = element != null ? (CERT_CONTEXT*)element->pCertContext.ToPointer() : null;
                if (cert == null || cert->pbCertEncoded == IntPtr.Zero
                    || cert->cbCertEncoded == 0 || cert->cbCertEncoded > MaxCertificateBytes)
                    throw new FormatException("crypt32 path certificate out of range");

                path[i] = new byte[cert->cbCertEncoded];
                Marshal.Copy(cert->pbCertEncoded, path[i], 0, path[i].Length);
            }

            return path;
        }

        private static CertificateVerificationResult Fail(CertificateVerificationStatus status, string detail)
            => CertificateVerificationResult.Rejected(status, detail);

        // wincrypt.h layouts. Read-only structs declare only the leading fields this file reads.
        [StructLayout(LayoutKind.Sequential)]
        private struct CERT_ENHKEY_USAGE
        {
            public uint cUsageIdentifier;
            public IntPtr rgpszUsageIdentifier;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CERT_USAGE_MATCH
        {
            public uint dwType;
            public CERT_ENHKEY_USAGE Usage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CERT_CHAIN_PARA
        {
            public uint cbSize;
            public CERT_USAGE_MATCH RequestedUsage;
            public CERT_USAGE_MATCH RequestedIssuancePolicy;
            public uint dwUrlRetrievalTimeout;
            public int fCheckRevocationFreshnessTime;
            public uint dwRevocationFreshnessTime;
            public IntPtr pftCacheResync;
            public IntPtr pStrongSignPara;
            public uint dwStrongSignFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CERT_CHAIN_ENGINE_CONFIG
        {
            public uint cbSize;
            public IntPtr hRestrictedRoot;
            public IntPtr hRestrictedTrust;
            public IntPtr hRestrictedOther;
            public uint cAdditionalStore;
            public IntPtr rghAdditionalStore;
            public uint dwFlags;
            public uint dwUrlRetrievalTimeout;
            public uint MaximumCachedCertificates;
            public uint CycleDetectionModulus;
            public IntPtr hExclusiveRoot;
            public IntPtr hExclusiveTrustedPeople;
            public uint dwExclusiveFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CERT_TRUST_STATUS
        {
            public uint dwErrorStatus;
            public uint dwInfoStatus;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CERT_CHAIN_CONTEXT
        {
            public uint cbSize;
            public CERT_TRUST_STATUS TrustStatus;
            public uint cChain;
            public IntPtr rgpChain;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CERT_SIMPLE_CHAIN
        {
            public uint cbSize;
            public CERT_TRUST_STATUS TrustStatus;
            public uint cElement;
            public IntPtr rgpElement;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CERT_CHAIN_ELEMENT
        {
            public uint cbSize;
            public IntPtr pCertContext;
            public CERT_TRUST_STATUS TrustStatus;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CERT_CONTEXT
        {
            public uint dwCertEncodingType;
            public IntPtr pbCertEncoded;
            public uint cbCertEncoded;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CERT_CHAIN_POLICY_PARA
        {
            public uint cbSize;
            public uint dwFlags;
            public IntPtr pvExtraPolicyPara;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SSL_EXTRA_CERT_CHAIN_POLICY_PARA
        {
            public uint cbSize;
            public uint dwAuthType;
            public uint fdwChecks;
            public IntPtr pwszServerName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CERT_CHAIN_POLICY_STATUS
        {
            public uint cbSize;
            public uint dwError;
            public int lChainIndex;
            public int lElementIndex;
            public IntPtr pvExtraPolicyStatus;
        }

        [DllImport(Crypt32, ExactSpelling = true)]
        private static extern IntPtr CertOpenStore(
            IntPtr storeProvider, uint encodingType, IntPtr cryptProv, uint flags, IntPtr para);

        [DllImport(Crypt32, ExactSpelling = true)]
        private static extern int CertCloseStore(IntPtr store, uint flags);

        [DllImport(Crypt32, ExactSpelling = true)]
        private static extern int CertAddEncodedCertificateToStore(
            IntPtr store, uint encodingType, byte* encoded, uint encodedLength, uint addDisposition, IntPtr* context);

        [DllImport(Crypt32, ExactSpelling = true)]
        private static extern int CertFreeCertificateContext(IntPtr context);

        [DllImport(Crypt32, ExactSpelling = true)]
        private static extern int CertCreateCertificateChainEngine(CERT_CHAIN_ENGINE_CONFIG* config, out IntPtr engine);

        [DllImport(Crypt32, ExactSpelling = true)]
        private static extern void CertFreeCertificateChainEngine(IntPtr engine);

        [DllImport(Crypt32, ExactSpelling = true)]
        private static extern int CertGetCertificateChain(
            IntPtr engine, IntPtr certContext, long* time, IntPtr additionalStore, CERT_CHAIN_PARA* para,
            uint flags, IntPtr reserved, out IntPtr chainContext);

        [DllImport(Crypt32, ExactSpelling = true)]
        private static extern int CertVerifyCertificateChainPolicy(
            IntPtr policyOid, IntPtr chainContext, CERT_CHAIN_POLICY_PARA* para, CERT_CHAIN_POLICY_STATUS* status);

        [DllImport(Crypt32, ExactSpelling = true)]
        private static extern void CertFreeCertificateChain(IntPtr chainContext);
    }
}
