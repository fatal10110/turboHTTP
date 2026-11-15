using System;
using System.Security.Cryptography.X509Certificates;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// A real public chain (#33, review of PR #240): the www.apple.com leaf and its issuing
    /// intermediate as the server sent them on 2026-11-15. It chains to DigiCert Global Root G2, which
    /// is in the Windows, macOS, Mozilla/Linux, iOS and Android root programs; the root itself is not
    /// committed, so a passing test proves the platform root store was used. Public data only.
    /// </summary>
    /// <remarks>
    /// <para>The Android TrustManager verifier evaluates at the device clock, not at the time it is
    /// given (#897), so on Android the chain is only trusted while the leaf is valid. The leaf has a
    /// six-month validity; <c>PublicChainTestCertificatesTests</c> fails
    /// <see cref="RenewalWindowDays"/> days before <see cref="LeafNotAfterUtc"/> so the chain is
    /// re-captured in time. Re-capture with
    /// <c>openssl s_client -connect www.apple.com:443 -servername www.apple.com -showcerts</c>,
    /// paste the first two certificates as base64 DER and update the dates, fingerprints and
    /// <see cref="VerificationTimeUtc"/> (the capture date). Prefer a leaf with a long validity left
    /// (short-lived leaves, such as www.digicert.com's 47-day one, need re-capturing every few weeks)
    /// whose chain ends at a root in every platform root program, and update <see cref="Host"/> and
    /// <see cref="RootCommonName"/> when the host or root changes.</para>
    /// </remarks>
    internal static class PublicChainTestCertificates
    {
        /// <summary>Days before <see cref="LeafNotAfterUtc"/> from which the expiry guard fails.</summary>
        internal const int RenewalWindowDays = 30;

        /// <summary>Host the leaf names (SAN dNSName; the leaf also names www.apple.com.cn and images.apple.com).</summary>
        internal const string Host = "www.apple.com";

        /// <summary>
        /// Verification time for verifiers that take one (Windows, macOS, iOS): the capture date,
        /// inside the leaf validity.
        /// </summary>
        internal static readonly DateTime VerificationTimeUtc = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Leaf validity starts here (2026-11-11T19:08:23Z).</summary>
        internal static readonly DateTime LeafNotBeforeUtc = new DateTime(2026, 11, 11, 19, 8, 23, DateTimeKind.Utc);

        /// <summary>Leaf validity ends here (2027-05-13T17:54:00Z).</summary>
        internal static readonly DateTime LeafNotAfterUtc = new DateTime(2027, 5, 13, 17, 54, 0, DateTimeKind.Utc);

        /// <summary>Subject CN of the platform root the chain must end at.</summary>
        internal const string RootCommonName = "DigiCert Global Root G2";

        /// <summary>
        /// True where the platform verifier ignores the verification time it is given and evaluates
        /// at the device clock: the Android TrustManager bridge (<c>AndroidTrustManagerVerifier</c>).
        /// </summary>
        internal static bool VerifierUsesDeviceClock
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// The verdict the production verifier must reach for this chain when it evaluates at
        /// <paramref name="evaluatedAtUtc"/>: trusted inside the leaf validity (RFC 5280 section
        /// 6.1.3 (a)(2), inclusive bounds), outside the validity period otherwise.
        /// </summary>
        internal static CertificateVerificationStatus ExpectedStatusAt(DateTime evaluatedAtUtc)
            => evaluatedAtUtc >= LeafNotBeforeUtc && evaluatedAtUtc <= LeafNotAfterUtc
                ? CertificateVerificationStatus.Trusted
                : CertificateVerificationStatus.NotTimeValid;

        /// <summary>
        /// Asserts the verdict on this chain for a verifier that evaluated at
        /// <paramref name="evaluatedAtUtc"/> (<see cref="ExpectedStatusAt"/>); a trusted verdict must
        /// carry the path leaf, intermediate, then the self-signed platform root.
        /// </summary>
        internal static void AssertVerdict(CertificateVerificationResult result, DateTime evaluatedAtUtc)
        {
            CertificateVerificationStatus expected = ExpectedStatusAt(evaluatedAtUtc);
            Assert.AreEqual(expected, result.Status,
                "Evaluated at " + evaluatedAtUtc.ToString("o") + ", leaf valid " + LeafNotBeforeUtc.ToString("o") +
                " to " + LeafNotAfterUtc.ToString("o") + ". Detail: " + result.Detail);
            if (expected != CertificateVerificationStatus.Trusted)
            {
                Assert.IsNull(result.Path, "a rejection carries no path");
                return;
            }

            Assert.AreEqual(3, result.Path.Length, "leaf + intermediate + platform root");
            CollectionAssert.AreEqual(Leaf, result.Path[0]);
            CollectionAssert.AreEqual(Intermediate, result.Path[1]);
            using var root = new X509Certificate2(result.Path[2]);
            StringAssert.Contains("CN=" + RootCommonName, root.Subject);
            Assert.AreEqual(root.Subject, root.Issuer, "the path ends at a self-signed root");
        }

        /// <summary>EV RSA leaf for www.apple.com (serverAuth), SHA-256 fingerprint 35664EC6...66FEC2.</summary>
        internal static byte[] Leaf => Convert.FromBase64String(
            "MIIHdjCCBl6gAwIBAgIQTWD8d9+FeqjFN8sDRhH21TANBgkqhkiG9w0BAQsFADBRMQswCQYDVQQGEwJVUzETMBEGA1UEChMKQXBw" +
            "bGUgSW5jLjEtMCsGA1UEAxMkQXBwbGUgUHVibGljIEVWIFNlcnZlciBSU0EgQ0EgMSAtIEcxMB4XDTI2MTExMTE5MDgyM1oXDTI3" +
            "MDUxMzE3NTQwMFowgccxHTAbBgNVBA8MFFByaXZhdGUgT3JnYW5pemF0aW9uMRMwEQYLKwYBBAGCNzwCAQMTAlVTMRswGQYLKwYB" +
            "BAGCNzwCAQIMCkNhbGlmb3JuaWExETAPBgNVBAUTCEMwODA2NTkyMQswCQYDVQQGEwJVUzETMBEGA1UECAwKQ2FsaWZvcm5pYTES" +
            "MBAGA1UEBwwJQ3VwZXJ0aW5vMRMwEQYDVQQKDApBcHBsZSBJbmMuMRYwFAYDVQQDDA13d3cuYXBwbGUuY29tMIIBIjANBgkqhkiG" +
            "9w0BAQEFAAOCAQ8AMIIBCgKCAQEAz5fr3mp5ALecF/+7iQs3e7RJAudfmsLPhr8tVLmP/40HsmwmADOobUKxwn2lNPg+SpmMu2vG" +
            "wc7nME22LlTyvXn4F4KEN9thuTEH9TU9bN4kBbdCbtHHWjl29oojiBJiYZBCI7+XVwxTM4DDAJAcRGw40y1mefnV4jjbwXto05W/" +
            "oYyNMGWOW9cfDTJT2ydYRbnmB5lBXOhamWm/Pg1FlY5h8oxgknUFsRlDQyp5fQmbUbfQfoc+cLwOZOlOeJb7awZjM05tvSEnelOy" +
            "pDQCZbz2LZs16Uik3Pq463SGPASimx3CQrzbHLd6SKjyVzNjlVqwsvHmc4z2q0e8AG5lNQIDAQABo4ID0TCCA80wDAYDVR0TAQH/" +
            "BAIwADAfBgNVHSMEGDAWgBTTvcE8oM81uTTF1NvaEA5M3mr+WDB6BggrBgEFBQcBAQRuMGwwMgYIKwYBBQUHMAKGJmh0dHA6Ly9j" +
            "ZXJ0cy5hcHBsZS5jb20vYXBldnNyc2ExZzEuZGVyMDYGCCsGAQUFBzABhipodHRwOi8vb2NzcC5hcHBsZS5jb20vb2NzcDAzLWFw" +
            "ZXZzcnNhMWcxMDEwPAYDVR0RBDUwM4IQd3d3LmFwcGxlLmNvbS5jboINd3d3LmFwcGxlLmNvbYIQaW1hZ2VzLmFwcGxlLmNvbTBg" +
            "BgNVHSAEWTBXMEgGBWeBDAEBMD8wPQYIKwYBBQUHAgEWMWh0dHBzOi8vd3d3LmFwcGxlLmNvbS9jZXJ0aWZpY2F0ZWF1dGhvcml0" +
            "eS9wdWJsaWMwCwYJYIZIAYb9bAIBMBMGA1UdJQQMMAoGCCsGAQUFBwMBMDUGA1UdHwQuMCwwKqAooCaGJGh0dHA6Ly9jcmwuYXBw" +
            "bGUuY29tL2FwZXZzcnNhMWcxLmNybDAdBgNVHQ4EFgQUoS9zKosFDxhpm6TERWFPdQiUTGkwDgYDVR0PAQH/BAQDAgWgMA8GCSqG" +
            "SIb3Y2QGVgQCBQAwggHyBgorBgEEAdZ5AgQCBIIB4gSCAd4B3AB1AETCvQzpFA5kpclKAZMKWqG7NZcOAO4RFoloKhxE17VmAAAB" +
            "ocwMC5EAAAQDAEYwRAIgWO77V5G62Qu14ZK3WTv7NiDVtPih2Jh7YhJpT2PaFQACIHRxELzmPFe/70sUUv+0edbGwHn5d3oyjVl2" +
            "9/wvA1j5AHUA1tWNqdAXU/NqSqDHV0kCr+vH3CzTjNn3ZMgMiRkenwIAAAGhzAwLWAAABAMARjBEAiACaf+IXlL5Dkq5GUbh7a9F" +
            "0zcSQOhwmvy6jTzoFJiaNAIgB2fuUf4AUgFnzftrw6887Uu9W4lLIy331a4cOlqeia4AdQBMY9yY5Zwdq4j2Hoo93q6Pq0SjN3tf" +
            "m5TD+6Gc/MG+JgAAAaHMDAuOAAAEAwBGMEQCIF8DxkNEamcEx5n2J9nMnxoCwn+S+3J/bXV1w64NvmpAAiBYQeQcnWsGo7cGKPyw" +
            "YeVIoDbPc3Fz8GJGo+IdDg4BeAB1AEaiOWfGDbZGh8ZvPfmZlHaTpqYRIIRX1VXn49Ch2bZGAAABocwMC8oAAAQDAEYwRAIgLKOh" +
            "jNHig4zZOh34ZxtPdWwIc3zxKWASXgPfAGaYCVsCIB6hc2rGIt4wrz0sqoWEtFKsUFkfVMcwOJspy1pWYaFIMA0GCSqGSIb3DQEB" +
            "CwUAA4IBAQDagZcSKqfW1eW/lFjNunjoUOfwCDwNel6k/W3cuZ2LSgzY9x39guWtooaSJzn5Id0rUGxfJxgzKu+tEe0qMEJIfdQq" +
            "zinHN5fhc3AJX2MaEOr6WGS6TN0fSzBxGYq1JFKZrVMMQCI+GcvGN4nS7oUNI5m6rf/RDz0H3PGdd7vWpKG9MvbfQJvVEvrA8hX/" +
            "4FWHEx8BzeKHfpc25/+bwFYpTkTovigtJ4EMQRVLMmwAJchRosa/mqLSk1ZD4vBbwOY9Am62Qj/+Jfn0wqtb7djixrV2ZVI93HIH" +
            "nD0ikH3TYEX/2aCcn4K6YSliJyYzUtK2hVRh3blu18z8rpydz20V");

        /// <summary>Apple Public EV Server RSA CA 1 - G1, issued by DigiCert Global Root G2, valid to 2030-04-10.</summary>
        internal static byte[] Intermediate => Convert.FromBase64String(
            "MIIFHjCCBAagAwIBAgIQBPIuzCH8tDgqwouPLWQfwDANBgkqhkiG9w0BAQsFADBhMQswCQYDVQQGEwJVUzEVMBMGA1UEChMMRGln" +
            "aUNlcnQgSW5jMRkwFwYDVQQLExB3d3cuZGlnaWNlcnQuY29tMSAwHgYDVQQDExdEaWdpQ2VydCBHbG9iYWwgUm9vdCBHMjAeFw0y" +
            "MDA0MjkxMjU1MzRaFw0zMDA0MTAyMzU5NTlaMFExCzAJBgNVBAYTAlVTMRMwEQYDVQQKEwpBcHBsZSBJbmMuMS0wKwYDVQQDEyRB" +
            "cHBsZSBQdWJsaWMgRVYgU2VydmVyIFJTQSBDQSAxIC0gRzEwggEiMA0GCSqGSIb3DQEBAQUAA4IBDwAwggEKAoIBAQDfn5fdV0A4" +
            "cCNFu0EvUgduZVPyPZity4q8Re1TcE9qGyWoVpzHm9OluDlqwcvudbLwBe25PWA2Z9xFpVKK9jGW6Vt79N7if7dfi9w2+2zG+/wJ" +
            "u1Z6TA8RENTVuJTaMwqQXw1dTkZEOgjCEKa75uyzXl+mIa0s4GdxkCBclGI9WDsUFlLj3A6eBIzeTomTl7LhIV+nQUEWTqkwG5Pc" +
            "xckiv7Xn1mu7EVWUukZWY8uL+KhcTJQZYtxNj3PeM73WZ3fraixPA+RBkVnG5NgObRnrk5VHwjntbit9892kp7EISZK4Izfm99Qg" +
            "P4V2IqHFsKxfCnZqfwUHikxftIVkjXIdAgMBAAGjggHgMIIB3DAdBgNVHQ4EFgQU073BPKDPNbk0xdTb2hAOTN5q/lgwHwYDVR0j" +
            "BBgwFoAUTiJUIBiV5uNu5g/6+rkS7QYXjzkwDgYDVR0PAQH/BAQDAgGGMB0GA1UdJQQWMBQGCCsGAQUFBwMBBggrBgEFBQcDAjAS" +
            "BgNVHRMBAf8ECDAGAQH/AgEAMDQGCCsGAQUFBwEBBCgwJjAkBggrBgEFBQcwAYYYaHR0cDovL29jc3AuZGlnaWNlcnQuY29tMEIG" +
            "A1UdHwQ7MDkwN6A1oDOGMWh0dHA6Ly9jcmwzLmRpZ2ljZXJ0LmNvbS9EaWdpQ2VydEdsb2JhbFJvb3RHMi5jcmwwgdwGA1UdIASB" +
            "1DCB0TCBxQYJYIZIAYb9bAIBMIG3MCgGCCsGAQUFBwIBFhxodHRwczovL3d3dy5kaWdpY2VydC5jb20vQ1BTMIGKBggrBgEFBQcC" +
            "AjB+DHxBbnkgdXNlIG9mIHRoaXMgQ2VydGlmaWNhdGUgY29uc3RpdHV0ZXMgYWNjZXB0YW5jZSBvZiB0aGUgUmVseWluZyBQYXJ0" +
            "eSBBZ3JlZW1lbnQgbG9jYXRlZCBhdCBodHRwczovL3d3dy5kaWdpY2VydC5jb20vcnBhLXVhMAcGBWeBDAEBMA0GCSqGSIb3DQEB" +
            "CwUAA4IBAQBD9c6SmtMxGjRwc/A1bPiM+r1qj5xbDzGn6s6m6oggm9UeBLCSUNJthKffNPqqwtULeeUddssewaOZX+uHjG9bY/O9" +
            "J1VQtGtXI2hndyAPiloqNjf5iBW16h3ZIUFQL319hISioItFVJZnVe4gjNEWio1ZRwO5A4e/H69/lPAX294yGtYGllAdv2NexhUM" +
            "fjODhCajoTJmkXbyIpYzTNkgDXvQptTecrvr0rPzEMWfTtGSppbOC+s/5jG3aJ6GJn49Ram1ZLEGHTx9PWUoHth9Lj7vwFBD9667" +
            "x9m9nUhuET9a3XvNep+N7w96ZqH2fAqUBW1kl6u3u67D6mvDsCQr");
    }
}
