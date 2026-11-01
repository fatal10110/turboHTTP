using System;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// A real public chain (#33, review of PR #240): the www.digicert.com leaf and its issuing
    /// intermediate as the server sent them on 2026-11-01. It chains to DigiCert Global Root G2, which
    /// is in the Windows, macOS, Mozilla/Linux, iOS and Android root programs; the root itself is not
    /// committed, so a passing test proves the platform root store was used. Public data only.
    /// Re-capture with: openssl s_client -connect www.digicert.com:443 -servername www.digicert.com -showcerts
    /// </summary>
    internal static class PublicChainTestCertificates
    {
        /// <summary>Leaf validity starts here (2026-10-11T00:00:00Z).</summary>
        internal static readonly DateTime LeafNotBeforeUtc = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Leaf validity ends here (2026-11-26T23:59:59Z).</summary>
        internal static readonly DateTime LeafNotAfterUtc = new DateTime(2026, 11, 26, 23, 59, 59, DateTimeKind.Utc);

        /// <summary>Subject CN of the platform root the chain must end at.</summary>
        internal const string RootCommonName = "DigiCert Global Root G2";

        /// <summary>RSA EV leaf for www.digicert.com (serverAuth), SHA-256 fingerprint 6D92F386...C2E43C.</summary>
        internal static byte[] Leaf => Convert.FromBase64String(
            "MIIG8DCCBdigAwIBAgIQDwf8t8Ift2Z8G/2UlvKLTDANBgkqhkiG9w0BAQsFADBEMQswCQYDVQQGEwJVUzEVMBMGA1UEChMM" +
            "RGlnaUNlcnQgSW5jMR4wHAYDVQQDExVEaWdpQ2VydCBFViBSU0EgQ0EgRzIwHhcNMjYxMDExMDAwMDAwWhcNMjYxMTI2MjM1" +
            "OTU5WjCBwTETMBEGCysGAQQBgjc8AgEDEwJVUzEVMBMGCysGAQQBgjc8AgECEwRVdGFoMR0wGwYDVQQPDBRQcml2YXRlIE9y" +
            "Z2FuaXphdGlvbjEVMBMGA1UEBRMMNTI5OTUzNy0wMTQyMQswCQYDVQQGEwJVUzENMAsGA1UECBMEVXRhaDENMAsGA1UEBxME" +
            "TGVoaTEXMBUGA1UEChMORGlnaUNlcnQsIEluYy4xGTAXBgNVBAMTEHd3dy5kaWdpY2VydC5jb20wggEiMA0GCSqGSIb3DQEB" +
            "AQUAA4IBDwAwggEKAoIBAQDesPl01saZvIJL1lY8LEEsLE9seV1EunBIeViUPh6L4MU7f9TbNBaQHctERb5XEp9GABNIQ+AK" +
            "ymBQSD7Q2PVM6u+2zoIYnZImo8Yx9rwuzFGSvE02cwoP7Z6TLO0lmna5caYDUpOihYAvvij5C3Zd7hSSOlfCW4vhyCARxKau" +
            "hkVChri/eQWL4fkxoGhouwVJ75rxcSJoQ4aYj8jnM2NII/2v2XM8+t2BjyFe0lNxmJvdWcohoMb+csH7cHOtloJeMPLa7O56" +
            "i3A11A8uQil3PLZJWfyPCL0Fn70DF79weGXsGd2H6wG93Px3aupipmOLqrpZ2nr4q8RpSiizu59HAgMBAAGjggNeMIIDWjAf" +
            "BgNVHSMEGDAWgBRqTlC/mGidW3sgddRZAXlIZpIyBjAdBgNVHQ4EFgQUkymM0C9v5fXSWrV2aS0N7BPhOr4wKQYDVR0RBCIw" +
            "IIIQd3d3LmRpZ2ljZXJ0LmNvbYIMZGlnaWNlcnQuY29tMEoGA1UdIARDMEEwCwYJYIZIAYb9bAIBMDIGBWeBDAEBMCkwJwYI" +
            "KwYBBQUHAgEWG2h0dHA6Ly93d3cuZGlnaWNlcnQuY29tL0NQUzAOBgNVHQ8BAf8EBAMCBaAwEwYDVR0lBAwwCgYIKwYBBQUH" +
            "AwEwdQYDVR0fBG4wbDA0oDKgMIYuaHR0cDovL2NybDMuZGlnaWNlcnQuY29tL0RpZ2lDZXJ0RVZSU0FDQUcyLmNybDA0oDKg" +
            "MIYuaHR0cDovL2NybDQuZGlnaWNlcnQuY29tL0RpZ2lDZXJ0RVZSU0FDQUcyLmNybDBzBggrBgEFBQcBAQRnMGUwJAYIKwYB" +
            "BQUHMAGGGGh0dHA6Ly9vY3NwLmRpZ2ljZXJ0LmNvbTA9BggrBgEFBQcwAoYxaHR0cDovL2NhY2VydHMuZGlnaWNlcnQuY29t" +
            "L0RpZ2lDZXJ0RVZSU0FDQUcyLmNydDAMBgNVHRMBAf8EAjAAMIIBgAYKKwYBBAHWeQIEAgSCAXAEggFsAWoAdgDCMX5XRRmj" +
            "Re5/ON6ykEHrx8IhWiK/f9W1rXaa2Q5SzQAAAaEpmNPYAAAEAwBHMEUCIHyfAdhpFcaA2Q7rYTpyhqBchqtC/nm+v50U4vzr" +
            "MgxJAiEA9wn7WR1yjozW9eumEBW4b2FMCwDT7EGMieUxryMQ4CoAdwDXbX0Q0af1d8LH6V/XAL/5gskzWmXh0LMBcxfAyMVp" +
            "dwAAAaEpmNPHAAAEAwBIMEYCIQCGrJWktboQWyguUWVMLSvhm9qpDWCUd0uXDUWsnnsyUQIhAMWPAmlp7LBOF3S+3vKl2uPn" +
            "xFMCjyfHKWy/tuJBj0W5AHcAlE5Dh/rswe+B8xkkJqgYZQHH0184AgE/cmd9VTcuGdgAAAGhKZjT/gAABAMASDBGAiEAvg+L" +
            "nQqnXug5SEyZj6oos9oKgYJYpebvImxKoDRrD7sCIQDjv0XKuBOE9h+95ubkrMsHfSEnWWXjRynkjSbIdEvi8DANBgkqhkiG" +
            "9w0BAQsFAAOCAQEAAxF6wFqNfoNSJ8BoC530BdS9LW1wPXTEXtBoVKCDGpCgSM0QjFuhpAT6OMgjk6f9z9Z0v7Mu5DmwPEeo" +
            "rj1WUnfUw6Z0yd/y0rohsP7WaXbYvfKMiGPp3Ub1IQMj2K26s4i0/lz3jXGaz26U6lR7kId3qS4PlVhliegHaCcqkRv4tgPr" +
            "syQtZR8IV3/TwoUTWIXDL0/WWq4Vh+/ypW2m6Os3KMlHyDW+hV/F9TmxOvo32DEbyAUHPCtvxNqRVxt8Lv7W/3RI4vNk8YAk" +
            "/L/+mx9Bmnn95TOxjfdCvpnWk9okMnqEVl06YbuPWy9xSJXQMpgDSo3vJy8KRupZMH3pNA==");

        /// <summary>DigiCert EV RSA CA G2 (pathLen 0), issued by DigiCert Global Root G2, valid to 2030-07-02.</summary>
        internal static byte[] Intermediate => Convert.FromBase64String(
            "MIIFPDCCBCSgAwIBAgIQAWePH++IIlXYsKcOa3uyIDANBgkqhkiG9w0BAQsFADBhMQswCQYDVQQGEwJVUzEVMBMGA1UEChMM" +
            "RGlnaUNlcnQgSW5jMRkwFwYDVQQLExB3d3cuZGlnaWNlcnQuY29tMSAwHgYDVQQDExdEaWdpQ2VydCBHbG9iYWwgUm9vdCBH" +
            "MjAeFw0yMDA3MDIxMjQyNTBaFw0zMDA3MDIxMjQyNTBaMEQxCzAJBgNVBAYTAlVTMRUwEwYDVQQKEwxEaWdpQ2VydCBJbmMx" +
            "HjAcBgNVBAMTFURpZ2lDZXJ0IEVWIFJTQSBDQSBHMjCCASIwDQYJKoZIhvcNAQEBBQADggEPADCCAQoCggEBAK0eZsx/neTr" +
            "f4MXJz0R2fJTIDfN8AwUAu7hy4gI0vp7O8LAAHx2h3bbf8wl+pGMSxaJK9ffDDCD63FqqFBqE9eTmo3RkgQhlu55a04LsXRL" +
            "cK6crkBOO0djdonybmhrfGrtBqYvbRatxenkv0Sg4frhRl4wYh4dnW0LOVRGhbt1G5Q19zm9CqMlq7LlUdAE+6d3a5++ppfG" +
            "cnWLmbEVEcLHPAnbl+/iKauQpQlU1Mi+wEBnjE5tK8Q778naXnF+DsedQJ7NEi+bQoonTHEz9ryeEcUHuQTv7nApa/zCqes5" +
            "lXn1pMs4LZJ3SVgbkTLj+RbBov/uiwTXtkBEWawvZH8CAwEAAaOCAgswggIHMB0GA1UdDgQWBBRqTlC/mGidW3sgddRZAXlI" +
            "ZpIyBjAfBgNVHSMEGDAWgBROIlQgGJXm427mD/r6uRLtBhePOTAOBgNVHQ8BAf8EBAMCAYYwHQYDVR0lBBYwFAYIKwYBBQUH" +
            "AwEGCCsGAQUFBwMCMBIGA1UdEwEB/wQIMAYBAf8CAQAwNAYIKwYBBQUHAQEEKDAmMCQGCCsGAQUFBzABhhhodHRwOi8vb2Nz" +
            "cC5kaWdpY2VydC5jb20wewYDVR0fBHQwcjA3oDWgM4YxaHR0cDovL2NybDMuZGlnaWNlcnQuY29tL0RpZ2lDZXJ0R2xvYmFs" +
            "Um9vdEcyLmNybDA3oDWgM4YxaHR0cDovL2NybDQuZGlnaWNlcnQuY29tL0RpZ2lDZXJ0R2xvYmFsUm9vdEcyLmNybDCBzgYD" +
            "VR0gBIHGMIHDMIHABgRVHSAAMIG3MCgGCCsGAQUFBwIBFhxodHRwczovL3d3dy5kaWdpY2VydC5jb20vQ1BTMIGKBggrBgEF" +
            "BQcCAjB+DHxBbnkgdXNlIG9mIHRoaXMgQ2VydGlmaWNhdGUgY29uc3RpdHV0ZXMgYWNjZXB0YW5jZSBvZiB0aGUgUmVseWlu" +
            "ZyBQYXJ0eSBBZ3JlZW1lbnQgbG9jYXRlZCBhdCBodHRwczovL3d3dy5kaWdpY2VydC5jb20vcnBhLXVhMA0GCSqGSIb3DQEB" +
            "CwUAA4IBAQBSMgrCdY2+O9spnYNvwHiG+9lCJbyELR0UsoLwpzGpSdkHD7pVDDFJm3//B8Es+17T1o5Hat+HRDsvRr7d3MEy" +
            "o9iXkkxLhKEgApA2Ft2eZfPrTolc95PwSWnn3FZ8BhdGO4brTA4+zkPSKoMXi/X+WLBNN29Z/nbCS7H/qLGt7gViEvTIdU8x" +
            "+H4l/XigZMUDaVmJ+B5d7cwSK7yOoQdfoIBGmA5Mp4LhMzo52rf//kXPfE3wYIZVHqVuxxlnTkFYmffCX9/Lon7SWaGdg6Rc" +
            "k4RHhHLWtmz2lTZ5CEo2ljDsGzCFGJP7oT4q6Q8oFC38irvdKIJ95cUxYzj4tnOI");
    }
}
