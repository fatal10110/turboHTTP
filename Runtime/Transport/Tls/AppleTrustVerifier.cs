using System;
using System.Runtime.InteropServices;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// Server certificate path validation through Apple's Security framework (#239):
    /// <c>Plugins/iOS/TurboHttpSecTrustVerifier.mm</c> evaluates the chain with
    /// <c>SecPolicyCreateSSL(true, host)</c>, network fetch disabled, against the system trust
    /// store, and returns the built path. A <see cref="PlatformCertificateVerifier"/>.
    /// </summary>
    /// <remarks>
    /// The bridge uses Security.framework and Foundation only, so a macOS player can reuse it
    /// (#248). The result buffer is allocated by the bridge and released here exactly once
    /// (<c>TurboHttp_SecTrustFree</c>), on every path. The buffer layout is native-endian
    /// <c>int32 count</c>, then per certificate <c>int32 length</c> and the DER bytes.
    /// Needs device validation (#99).
    /// </remarks>
    internal static class AppleTrustVerifier
    {
        /// <summary>Bridge result codes; keep in sync with <c>TurboHttpSecTrustVerifier.mm</c>.</summary>
        internal const int ResultTrusted = 0;
        internal const int ResultUntrustedRoot = 1;
        internal const int ResultNotTimeValid = 2;
        internal const int ResultInvalid = 3;
        internal const int ResultBridgeError = 4;

#if UNITY_IOS && !UNITY_EDITOR
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [DllImport("__Internal")]
        private static extern int TurboHttp_SecTrustEvaluate(
            byte[] chainBytes, int[] lengths, int count,
            [MarshalAs(UnmanagedType.LPStr)] string host, double verifyTimeUnixSeconds,
            out IntPtr pathBuffer, out int pathBufferLength, out int osStatus);

        [DllImport("__Internal")]
        private static extern void TurboHttp_SecTrustFree(IntPtr buffer);

        /// <summary>Evaluates the chain with SecTrust (see the class remarks).</summary>
        internal static CertificateVerificationResult Verify(
            byte[][] chainDer, string host, DateTime verificationTimeUtc)
        {
            var lengths = new int[chainDer.Length];
            int total = 0;
            for (int i = 0; i < chainDer.Length; i++)
            {
                lengths[i] = chainDer[i].Length;
                total = checked(total + lengths[i]);
            }

            var flat = new byte[total];
            int offset = 0;
            foreach (var der in chainDer)
            {
                Buffer.BlockCopy(der, 0, flat, offset, der.Length);
                offset += der.Length;
            }

            double verifyTime = (verificationTimeUtc.ToUniversalTime() - UnixEpoch).TotalSeconds;
            IntPtr pathBuffer = IntPtr.Zero;
            try
            {
                int code = TurboHttp_SecTrustEvaluate(flat, lengths, chainDer.Length, host, verifyTime,
                    out pathBuffer, out int pathBufferLength, out int osStatus);
                return FromNative(code, osStatus, pathBuffer, pathBufferLength);
            }
            finally
            {
                if (pathBuffer != IntPtr.Zero)
                    TurboHttp_SecTrustFree(pathBuffer);
            }
        }
#endif

        /// <summary>
        /// Maps a bridge result to a verification result. Reads the path only for
        /// <see cref="ResultTrusted"/>; does not take ownership of <paramref name="pathBuffer"/>.
        /// </summary>
        internal static CertificateVerificationResult FromNative(
            int code, int osStatus, IntPtr pathBuffer, int pathBufferLength)
        {
            switch (code)
            {
                case ResultTrusted:
                    return new CertificateVerificationResult(
                        CertificateVerificationStatus.Trusted, null, ReadPath(pathBuffer, pathBufferLength));
                case ResultUntrustedRoot:
                    return Fail(CertificateVerificationStatus.UntrustedRoot, "SecTrust: not trusted", osStatus);
                case ResultNotTimeValid:
                    return Fail(CertificateVerificationStatus.NotTimeValid, "SecTrust: outside the validity period", osStatus);
                case ResultInvalid:
                    return Fail(CertificateVerificationStatus.Invalid, "SecTrust: evaluation failed", osStatus);
                default:
                    return Fail(CertificateVerificationStatus.Invalid, "SecTrust bridge error " + code, osStatus);
            }
        }

        /// <summary>
        /// Copies the built path out of a bridge buffer. Throws <see cref="FormatException"/> on
        /// any inconsistency; the runner turns that into a rejection.
        /// </summary>
        internal static byte[][] ReadPath(IntPtr buffer, int length)
        {
            if (buffer == IntPtr.Zero || length < 4)
                throw new FormatException("SecTrust bridge returned no path");

            int count = Marshal.ReadInt32(buffer, 0);
            if (count <= 0 || count > PlatformVerifierRunner.MaxPathLength)
                throw new FormatException("SecTrust bridge path count out of range");

            var path = new byte[count][];
            int offset = 4;
            for (int i = 0; i < count; i++)
            {
                if (length - offset < 4)
                    throw new FormatException("SecTrust bridge path truncated");
                int size = Marshal.ReadInt32(buffer, offset);
                offset += 4;
                if (size <= 0 || size > length - offset)
                    throw new FormatException("SecTrust bridge certificate length out of range");

                path[i] = new byte[size];
                Marshal.Copy(IntPtr.Add(buffer, offset), path[i], 0, size);
                offset += size;
            }

            if (offset != length)
                throw new FormatException("SecTrust bridge path has trailing bytes");

            return path;
        }

        private static CertificateVerificationResult Fail(
            CertificateVerificationStatus status, string detail, int osStatus)
            => new CertificateVerificationResult(status, detail + " (OSStatus " + osStatus + ")", null);
    }
}
