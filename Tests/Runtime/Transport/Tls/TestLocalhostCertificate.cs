using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// A committed, test-only TLS server identity for loopback tests. Unity's Mono cannot create
    /// certificates at runtime (<c>CertificateRequest</c> is unavailable), which made every
    /// real-TLS test skip in the Editor (#151).
    /// </summary>
    /// <remarks>
    /// Test material, not a secret: self-signed <c>CN=localhost</c> (SAN localhost, 127.0.0.1, ::1),
    /// RSA 2048, serverAuth, valid 2026-10-31 to 2126-10-07, PKCS#12 with SHA1/3DES so Mono can read
    /// it. Nothing trusts it: platform <c>SslStream</c> rejects it (the fail-closed tests rely on that).
    /// The BouncyCastle provider validates the chain (#33), so <see cref="TryLoad"/> registers this
    /// certificate as a test-only trust anchor (<c>ServerCertificateVerifier.AddTestTrustAnchor</c>)
    /// for every loopback test that loads it; production code never registers an anchor, and the
    /// chain-validation tests show the same certificate is rejected without it. Regenerate with
    /// <c>openssl req -x509 -newkey rsa:2048 -nodes -days 36500 -subj /CN=localhost ...</c> and
    /// <c>openssl pkcs12 -export -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1</c>.
    /// </remarks>
    internal static class TestLocalhostCertificate
    {
        private const string Password = "turbohttp-test";

        private const string PfxBase64 =
            "MIIJoQIBAzCCCWcGCSqGSIb3DQEHAaCCCVgEgglUMIIJUDCCBAcGCSqGSIb3DQEHBqCCA/gwggP0AgEAMIID7QYJKoZIhvcN" +
            "AQcBMBwGCiqGSIb3DQEMAQMwDgQIQehL+aNTk2kCAggAgIIDwCWLCvxQQG7ZCp7vfkEdMtr3blbWhcZZ/FslBcr6bTHT0I5x" +
            "vXLiZEF2nkzgGsoAIRwA9mq/2RcZTKgYLx4G+ycln/Y06cW3rPcf7qFsdkE4wMsZbsbrioSY0PsBQvcZRgqb55jFECej65Bd" +
            "aB5Y+9qwi9Dh4Dj7dzIHlsGfn6d+zd65RVV0BOxdhpvCsFExcOaCqbDBZ+djSej6VOjlrfTuYZfN3F/d/pgBz9Cu/DIuwAhO" +
            "FrUzNPXKi1h0b2Gth468GCNCaMLfzx3WyTLjYuLwYaw5aOA32vuheh3ySuF78nViiBDWN0fW47GcHyFtxhaZeSwpXy8iOUyp" +
            "QjQ0pigPUq+Fwq5B0zMUlAFWOYpGXkiufeUcxF4S7CdWUUX2gW0nXFLO6+cKmN5a0mhFxQk8t1nJquQDWJbzrpAuHhMiTsxI" +
            "WM4hUZBVPAFEpLOiAPHN4z2cux6oXa2PiTBWx8wY63EuQTayr1mt1w1fnT/Q4eAIgh7yvS1tN9nE2nI5ZVXwWtfKsvwSuvi1" +
            "TO/tHmb7ZiNBuireO6BA1oYPFLjcX578b+PxACbua7a7FoR+PFTi6bFWb+bY3sh28MlkWvEb4E3Z38ZL6dJ5it0lRDmTnqFD" +
            "3hkT9LXQLqNauWsu2bDSAD3pERt5PvBW7iALffiTf5qt0o3aBa0vCE2jyyddt0z/4MbYpIrBXWhiaQ3RtlUzo/f+UULhutW3" +
            "pcEeJlLkEPzpKmG2b718QVB0o4WOGn/KYEBmMuBo3pceggucE4B8AcD1rQARlwDJdKVWmQxj3udIT8Jv/ByVbSucj24d3Fps" +
            "ij2yxF/kr0/2ObjjynJizUC6Wfg2dAdZLJqhEpSpQbNXn2x08xtu8gzat8R/blEmWDxnohG8OCB729bfLU7+7abgWIbIYnk3" +
            "qBEK0P1EUcSo5swZe8U4O8PIHVBsll4ZUO1/UJlID/dYUCWSG8PiEGgdjSK50HnClY1d/s+kuB9q/rR14T59LekNwZECccsx" +
            "rIC72W4tT1AnO6sSmk4mBOc1CcMKa/Zx4E7DOvFurHs/oy2yYwXG0xQbNbB0L2dQukYDI8b18eTBo7mlJ8xmJt/iQfmp7L9z" +
            "gOs4Hp3PdB68WKxEzppQzHUaRkSOv4r93nhSi0IcXQ3071OB7cj2CzWCl3H46yc4hZL0hH1BNUF1g/gmVgA/F4XP379mlcoG" +
            "zkT3QFK2e8NLADbsipUGIEAOP9jkjIoOoEsPCTlcffzKLNw/t+/JoaJS1apNEJ/gZiBxb374tXXTNoATOTCCBUEGCSqGSIb3" +
            "DQEHAaCCBTIEggUuMIIFKjCCBSYGCyqGSIb3DQEMCgECoIIE7jCCBOowHAYKKoZIhvcNAQwBAzAOBAjafmwB+w7giQICCAAE" +
            "ggTIu3Pcax2nVBHuouALv6vLJhsnOcNdYwBsxQJpfceEZK4QX9LWfnv5LWAuPQwYTv+82UdogixFzhAaIkmgnhZoQ0DPX1R7" +
            "lwLOUE5P8Ff+2xPi3JjUX2zSCMaaNUFPz8zfUVhmQiGiDMkAFQbPNL2K+YwPskMC8vTexGzMwYq7uO8nxjIKTDuLCbChpSXW" +
            "VyUrz2GtnCqQNCeWTcdjbLGE0G8kIWRFEaDh8UKeevatloFK5x7ctxjyBVSH/I+q9mLRM+KQj6Yr1I6qY+xjZO8Zj5YUgRjq" +
            "F4mH+cpD2Rbda7ANUu1FPSOdnpKaXF/qr0MPH3XcVntxfaHxw3sKDfCgJskaLhIxprlVLrwfA6H769PmH+E9wkKh/3p8rlsm" +
            "t2n9owbRr6dOIEM4mmFXqDkCDB8dBqvuyFyoCLT8YBik2jB6ydLlOQZ3APLy9mks/fFK8JQJGp+VW0Ik4wranLVrcUgCjM1+" +
            "Bil+PL0U0/8VJWsQKdj/N06Q0Shyr7rBrdJgau+YQxTLW+0m//POVpqgsfiHqpXm9GmlJVNFPYfg+nvHz4OrDT08ZqINB/7u" +
            "yHqUVzfOxWTCYr1wRaJiILX7eNp7KDLRj14pnWgGR1yzvesML3/+3IXIWJX7Uzz6OmxsmBRyTujfbMc+W7AxIIlvsKa6QdC7" +
            "twRVtNhXCn3l9qePYpMogU6dJcWDBRSTjptJRIjdEYNdgK8txrmVQ2mPHht0BE1U0StekUBIEIQ3uU6k9UjBLLs7m8W4wATH" +
            "hHIJ3ZkUyu8EyN5mkiFyEfihJByY98LojkphdpgT2npO0sMt1wYKrpOphz0DKozg6Dpg+39jlt9gP3CSjnMIxiVtJie/hG4y" +
            "Qqqb1HRWmniYIH9r4zx8Fvw7xi8q7SW2NjECWZArLFRkpMKwsoDRxC1PV54FoqXTuNDB+xdBhgl2sftN99gb9CH8W7+Y9yJO" +
            "zIpq5KJW2fSPSK7QAQya331uj5tY1v7bTwDcTRZbf+TxKENr5XU/BONy1x56MH7xLsa543DqxuEL8pEHJVbfamJ+AZKy8e+O" +
            "tV/N+vA+objA9+AGBYnX1dLFzCtulNcGNYtLYK5cbe0Br2FvWnSI6xm1D3CyqMeheCEi/oj1nAWhFdlt8IhHHIiwavIoXaXb" +
            "nR47Mek2XWuqx//FbzowYF07u9rhTjYNcwqj44V174YBJlgIbbkWScEJfHGuX//hOJBFBoxLAi/RSNDK53GCskxZz9LIWYCq" +
            "qCSuC7rOST9NJt2xmYOeft+OcL6C1u6OOSriGUjp6NiGyIeSkJBHKEvoj7w4CB9sgvQJGm1F2/F1wo3tGhKHs75rmw/igIss" +
            "eQDpjxxoTFbt0GfHMqqSZIRJbpcdNZHih1BJmYaRMUGDBgzwh3XMAbqv1Qd4yEg/CP0Z+xCSGXqWzdcHc06HZQs0rgwLUWfv" +
            "XRWOTLd1PT9PyAqFXspp20kfkZrShovMiQQchXIe19Jk01WbJJy9nzUzpcDLsvl+JH4J8RC1vW6KDJBjXHzOEZGqbwqEONs0" +
            "4lDqzD1O5iy+yYKXXqGvR59kUkpJnVOpjD1Z4SH4FzxCYFRzq0rP6/tbDxUq7Z4uReZ0LBGz3YAH1gIxxj/AzmrdjdLOmdwA" +
            "Y7MNMSUwIwYJKoZIhvcNAQkVMRYEFHMrZ+lxNrLq69EkdzUdWA/SUz1qMDEwITAJBgUrDgMCGgUABBQDoIiO96ox+ZtlIanu" +
            "CA3GrpjM5AQI5bzXrdx9jK8CAggA";

        /// <summary>
        /// A new instance (caller disposes), or null if this runtime cannot load PKCS#12. Also trusts
        /// the certificate as a BouncyCastle test anchor (idempotent, process-wide).
        /// </summary>
        internal static X509Certificate2 TryLoad()
        {
            try
            {
                var certificate = new X509Certificate2(
                    Convert.FromBase64String(PfxBase64),
                    Password,
                    X509KeyStorageFlags.Exportable);
                ServerCertificateVerifier.AddTestTrustAnchor(certificate.RawData);
                return certificate;
            }
            catch (CryptographicException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                // Also covers PlatformNotSupportedException.
                return null;
            }
        }
    }
}
