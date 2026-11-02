using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// Server certificate path validation through Security.framework on macOS (#248), the
    /// <see cref="PlatformCertificateVerifier"/> of the macOS Editor and macOS players. The same
    /// evaluation as the iOS bridge (<c>Plugins/iOS/TurboHttpSecTrustVerifier.mm</c>), called
    /// directly from C#: <c>SecPolicyCreateSSL(true, host)</c> against the system trust store,
    /// network fetch disabled, at the caller's verification time, sharing the result codes and
    /// their mapping with <see cref="AppleTrustVerifier"/>.
    /// </summary>
    /// <remarks>
    /// <para>Unity does not compile a loose <c>.mm</c> for the macOS Editor or Mono players, so the
    /// bridge is P/Invoke into the system frameworks (no shipped binary). Test anchors (loopback
    /// fixtures only) become the only anchors (<c>SecTrustSetAnchorCertificates</c> +
    /// <c>SecTrustSetAnchorCertificatesOnly</c>); production registers none.</para>
    /// <para>Every Core Foundation object created here is released exactly once in
    /// <c>finally</c>. Compiled on every platform (imports resolve on first call), installed only on
    /// macOS. Needs validation on a Mac (needs-device-validation).</para>
    /// </remarks>
    internal static unsafe class MacSecTrustVerifier
    {
        private const string SecurityLibrary = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string SystemLibrary = "/usr/lib/libSystem.dylib";

        private const uint CFStringEncodingUtf8 = 0x08000100;
        private const double CFAbsoluteTimeIntervalSince1970 = 978307200.0;
        private const int RtldLazy = 1;
        private const int MaxCertificateBytes = 64 * 1024;

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly byte[] CoreFoundationPath = Encoding.ASCII.GetBytes(CoreFoundationLibrary + "\0");
        private static readonly byte[] ArrayCallBacksSymbol = Encoding.ASCII.GetBytes("kCFTypeArrayCallBacks\0");

        // &kCFTypeArrayCallBacks, resolved once; a race resolves the same address twice.
        private static IntPtr s_arrayCallBacks;

        /// <summary>Evaluates the chain with SecTrust (see the class remarks).</summary>
        internal static CertificateVerificationResult Verify(
            byte[][] chainDer, string host, DateTime verificationTimeUtc)
            => Evaluate(chainDer, host, verificationTimeUtc, ServerCertificateVerifier.SnapshotTestTrustAnchors());

        /// <summary>
        /// <see cref="Verify"/> with explicit test anchors: when there are any they are the only
        /// anchors, otherwise the system trust store is.
        /// </summary>
        internal static CertificateVerificationResult Evaluate(
            byte[][] chainDer, string host, DateTime verificationTimeUtc, byte[][] testAnchors)
        {
            if (chainDer == null || chainDer.Length == 0)
                return CertificateVerificationResult.Rejected(CertificateVerificationStatus.Invalid, "empty certificate chain");
            if (string.IsNullOrEmpty(host))
                return CertificateVerificationResult.Rejected(CertificateVerificationStatus.Invalid,
                    "no target host for the platform verifier");

            var owned = new List<IntPtr>(2 * (chainDer.Length + (testAnchors?.Length ?? 0)) + 8);
            int osStatus = 0;
            byte[][] path = null;
            int code = AppleTrustVerifier.ResultNotSet;
            try
            {
                code = EvaluateCore(chainDer, host, verificationTimeUtc, testAnchors, owned, ref osStatus, out path);
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--)
                    CFRelease(owned[i]);
            }

            if (code == AppleTrustVerifier.ResultTrusted && path != null && path.Length > 0)
                return CertificateVerificationResult.Trusted(path);
            return AppleTrustVerifier.Rejection(
                code == AppleTrustVerifier.ResultTrusted ? AppleTrustVerifier.ResultBridgeError : code, osStatus);
        }

        // Returns an AppleTrustVerifier result code; ResultTrusted only together with the path.
        private static int EvaluateCore(byte[][] chainDer, string host, DateTime verificationTimeUtc,
            byte[][] testAnchors, List<IntPtr> owned, ref int osStatus, out byte[][] path)
        {
            path = null;
            IntPtr callBacks = ArrayCallBacks();
            if (callBacks == IntPtr.Zero)
                return AppleTrustVerifier.ResultBridgeError;

            IntPtr certificates = CreateCertificateArray(chainDer, callBacks, owned);
            if (certificates == IntPtr.Zero)
                return AppleTrustVerifier.ResultInvalid;  // not a DER certificate

            IntPtr hostName;
            var hostBytes = Encoding.UTF8.GetBytes(host + "\0");
            fixed (byte* hostPtr = hostBytes)
                hostName = CFStringCreateWithCString(IntPtr.Zero, hostPtr, CFStringEncodingUtf8);
            if (hostName == IntPtr.Zero)
                return AppleTrustVerifier.ResultInvalid;
            owned.Add(hostName);

            IntPtr policy = SecPolicyCreateSSL(1, hostName);
            if (policy == IntPtr.Zero)
                return AppleTrustVerifier.ResultBridgeError;
            owned.Add(policy);

            osStatus = SecTrustCreateWithCertificates(certificates, policy, out IntPtr trust);
            if (trust != IntPtr.Zero)
                owned.Add(trust);
            if (osStatus != 0 || trust == IntPtr.Zero)
                return AppleTrustVerifier.ResultBridgeError;

            // No network: evaluation runs synchronously inside the TLS handshake (#142).
            osStatus = SecTrustSetNetworkFetchAllowed(trust, 0);
            if (osStatus != 0)
                return AppleTrustVerifier.ResultBridgeError;

            double unixSeconds = (verificationTimeUtc.ToUniversalTime() - UnixEpoch).TotalSeconds;
            IntPtr date = CFDateCreate(IntPtr.Zero, unixSeconds - CFAbsoluteTimeIntervalSince1970);
            if (date == IntPtr.Zero)
                return AppleTrustVerifier.ResultBridgeError;
            owned.Add(date);
            osStatus = SecTrustSetVerifyDate(trust, date);
            if (osStatus != 0)
                return AppleTrustVerifier.ResultBridgeError;

            if (testAnchors != null && testAnchors.Length > 0)
            {
                IntPtr anchors = CreateCertificateArray(testAnchors, callBacks, owned);
                if (anchors == IntPtr.Zero)
                    return AppleTrustVerifier.ResultBridgeError;
                osStatus = SecTrustSetAnchorCertificates(trust, anchors);
                if (osStatus != 0)
                    return AppleTrustVerifier.ResultBridgeError;
                osStatus = SecTrustSetAnchorCertificatesOnly(trust, 1);
                if (osStatus != 0)
                    return AppleTrustVerifier.ResultBridgeError;
            }

            byte trusted = SecTrustEvaluateWithError(trust, out IntPtr error);
            if (error != IntPtr.Zero)
                owned.Add(error);
            if (trusted != 1)
            {
                long errorCode = error != IntPtr.Zero ? (long)CFErrorGetCode(error) : 0;
                osStatus = unchecked((int)errorCode);
                return AppleTrustVerifier.MapEvaluationError(errorCode);
            }

            osStatus = 0;
            path = ReadPath(trust, owned);
            return path != null ? AppleTrustVerifier.ResultTrusted : AppleTrustVerifier.ResultBridgeError;
        }

        private static IntPtr ArrayCallBacks()
        {
            IntPtr cached = Volatile.Read(ref s_arrayCallBacks);
            if (cached != IntPtr.Zero)
                return cached;

            IntPtr handle;
            fixed (byte* pathPtr = CoreFoundationPath)
                handle = dlopen(pathPtr, RtldLazy);
            if (handle == IntPtr.Zero)
                return IntPtr.Zero;

            // CoreFoundation stays loaded for the life of the process; the handle is not closed.
            IntPtr symbol;
            fixed (byte* namePtr = ArrayCallBacksSymbol)
                symbol = dlsym(handle, namePtr);
            if (symbol != IntPtr.Zero)
                Volatile.Write(ref s_arrayCallBacks, symbol);
            return symbol;
        }

        // A +1 CFArray of SecCertificateRef (also added to owned), or zero if any DER is rejected.
        private static IntPtr CreateCertificateArray(byte[][] ders, IntPtr callBacks, List<IntPtr> owned)
        {
            var certificates = new IntPtr[ders.Length];
            for (int i = 0; i < ders.Length; i++)
            {
                var der = ders[i];
                if (der == null || der.Length == 0 || der.Length > MaxCertificateBytes)
                    return IntPtr.Zero;

                IntPtr data;
                fixed (byte* bytes = der)
                    data = CFDataCreate(IntPtr.Zero, bytes, (IntPtr)der.Length);
                if (data == IntPtr.Zero)
                    return IntPtr.Zero;
                owned.Add(data);

                IntPtr certificate = SecCertificateCreateWithData(IntPtr.Zero, data);
                if (certificate == IntPtr.Zero)
                    return IntPtr.Zero;
                owned.Add(certificate);
                certificates[i] = certificate;
            }

            IntPtr array;
            fixed (IntPtr* values = certificates)
                array = CFArrayCreate(IntPtr.Zero, values, (IntPtr)certificates.Length, callBacks);
            if (array != IntPtr.Zero)
                owned.Add(array);
            return array;
        }

        // The built chain, leaf first, or null on any inconsistency.
        private static byte[][] ReadPath(IntPtr trust, List<IntPtr> owned)
        {
            IntPtr chain = IntPtr.Zero;
            try
            {
                chain = SecTrustCopyCertificateChain(trust);  // macOS 12+
            }
            catch (EntryPointNotFoundException)
            {
                chain = IntPtr.Zero;
            }

            if (chain != IntPtr.Zero)
            {
                owned.Add(chain);
                long count = (long)CFArrayGetCount(chain);
                if (count <= 0 || count > PlatformVerifierRunner.MaxPathLength)
                    return null;
                var path = new byte[count][];
                for (int i = 0; i < path.Length; i++)
                {
                    path[i] = CopyData(CFArrayGetValueAtIndex(chain, (IntPtr)i));
                    if (path[i] == null)
                        return null;
                }
                return path;
            }

            long total = (long)SecTrustGetCertificateCount(trust);
            if (total <= 0 || total > PlatformVerifierRunner.MaxPathLength)
                return null;
            var legacy = new byte[total][];
            for (int i = 0; i < legacy.Length; i++)
            {
                legacy[i] = CopyData(SecTrustGetCertificateAtIndex(trust, (IntPtr)i));  // not retained
                if (legacy[i] == null)
                    return null;
            }
            return legacy;
        }

        private static byte[] CopyData(IntPtr certificate)
        {
            if (certificate == IntPtr.Zero)
                return null;
            IntPtr data = SecCertificateCopyData(certificate);
            if (data == IntPtr.Zero)
                return null;
            try
            {
                long length = (long)CFDataGetLength(data);
                IntPtr bytes = CFDataGetBytePtr(data);
                if (length <= 0 || length > MaxCertificateBytes || bytes == IntPtr.Zero)
                    return null;
                var der = new byte[length];
                Marshal.Copy(bytes, der, 0, der.Length);
                return der;
            }
            finally
            {
                CFRelease(data);
            }
        }

        [DllImport(SystemLibrary)]
        private static extern IntPtr dlopen(byte* path, int mode);

        [DllImport(SystemLibrary)]
        private static extern IntPtr dlsym(IntPtr handle, byte* symbol);

        [DllImport(CoreFoundationLibrary)]
        private static extern void CFRelease(IntPtr cf);

        [DllImport(CoreFoundationLibrary)]
        private static extern IntPtr CFDataCreate(IntPtr allocator, byte* bytes, IntPtr length);

        [DllImport(CoreFoundationLibrary)]
        private static extern IntPtr CFDataGetLength(IntPtr data);

        [DllImport(CoreFoundationLibrary)]
        private static extern IntPtr CFDataGetBytePtr(IntPtr data);

        [DllImport(CoreFoundationLibrary)]
        private static extern IntPtr CFArrayCreate(IntPtr allocator, IntPtr* values, IntPtr count, IntPtr callBacks);

        [DllImport(CoreFoundationLibrary)]
        private static extern IntPtr CFArrayGetCount(IntPtr array);

        [DllImport(CoreFoundationLibrary)]
        private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, IntPtr index);

        [DllImport(CoreFoundationLibrary)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, byte* cString, uint encoding);

        [DllImport(CoreFoundationLibrary)]
        private static extern IntPtr CFDateCreate(IntPtr allocator, double absoluteTime);

        [DllImport(CoreFoundationLibrary)]
        private static extern IntPtr CFErrorGetCode(IntPtr error);

        [DllImport(SecurityLibrary)]
        private static extern IntPtr SecCertificateCreateWithData(IntPtr allocator, IntPtr data);

        [DllImport(SecurityLibrary)]
        private static extern IntPtr SecCertificateCopyData(IntPtr certificate);

        [DllImport(SecurityLibrary)]
        private static extern IntPtr SecPolicyCreateSSL(byte server, IntPtr hostname);

        [DllImport(SecurityLibrary)]
        private static extern int SecTrustCreateWithCertificates(IntPtr certificates, IntPtr policies, out IntPtr trust);

        [DllImport(SecurityLibrary)]
        private static extern int SecTrustSetNetworkFetchAllowed(IntPtr trust, byte allowFetch);

        [DllImport(SecurityLibrary)]
        private static extern int SecTrustSetVerifyDate(IntPtr trust, IntPtr verifyDate);

        [DllImport(SecurityLibrary)]
        private static extern int SecTrustSetAnchorCertificates(IntPtr trust, IntPtr anchorCertificates);

        [DllImport(SecurityLibrary)]
        private static extern int SecTrustSetAnchorCertificatesOnly(IntPtr trust, byte anchorCertificatesOnly);

        [DllImport(SecurityLibrary)]
        private static extern byte SecTrustEvaluateWithError(IntPtr trust, out IntPtr error);

        [DllImport(SecurityLibrary)]
        private static extern IntPtr SecTrustCopyCertificateChain(IntPtr trust);

        [DllImport(SecurityLibrary)]
        private static extern IntPtr SecTrustGetCertificateCount(IntPtr trust);

        [DllImport(SecurityLibrary)]
        private static extern IntPtr SecTrustGetCertificateAtIndex(IntPtr trust, IntPtr index);
    }
}
