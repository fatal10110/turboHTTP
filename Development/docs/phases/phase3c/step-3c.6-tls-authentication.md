# Step 3C.6: BouncyCastle TlsAuthentication

**File:** `Runtime/Transport/BouncyCastle/TurboTlsAuthentication.cs`  
**Depends on:** Step 3C.4  
**Spec:** RFC 5280 (X.509 Certificates), RFC 6125 (Certificate Hostname Verification)

## Purpose

Implement BouncyCastle's `TlsAuthentication` interface to validate server certificates during the TLS handshake. Performs basic certificate chain validation and hostname verification.

## Type to Implement

### `TurboTlsAuthentication` (class)

```csharp
using System;
using System.Linq;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.X509;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto;

namespace TurboHTTP.Transport.BouncyCastle
{
    /// <summary>
    /// TLS authentication handler for server certificate validation.
    /// </summary>
    internal sealed class TurboTlsAuthentication : TlsAuthentication
    {
        private readonly string _targetHost;

        public TurboTlsAuthentication(string targetHost)
        {
            _targetHost = targetHost ?? throw new ArgumentNullException(nameof(targetHost));
        }

        public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
        {
            // No client certificate support in this phase
            // Return null = "I don't have a client certificate"
            return null;
        }

        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            if (serverCertificate == null || serverCertificate.Certificate.IsEmpty)
            {
                throw new TlsFatalAlert(AlertDescription.certificate_unknown,
                    "Server did not provide a certificate");
            }

            var chain = serverCertificate.Certificate;
            var leafCert = chain.GetCertificateAt(0);

            // 1. Validate certificate chain
            ValidateCertificateChain(chain);

            // 2. Verify hostname matches certificate
            ValidateHostname(leafCert, _targetHost);

            // 3. Check certificate validity period
            ValidateValidity(leafCert);
        }

        private void ValidateCertificateChain(Certificate chain)
        {
            // Basic chain validation
            if (chain.Length == 0)
            {
                throw new TlsFatalAlert(AlertDescription.certificate_unknown,
                    "Certificate chain is empty");
            }

            // TODO: Full chain validation
            // - Verify each certificate is signed by the next
            // - Verify root is in trusted root store
            // - Check for revocation (CRL/OCSP)
            // For now, accept any non-empty chain (INSECURE for production)
        }

        private void ValidateHostname(TlsCertificate tlsCertificate, string hostname)
        {
            var cert = tlsCertificate.GetX509Certificate();
            
            // Extract Subject Alternative Names (SAN)
            var sans = GetSubjectAlternativeNames(cert);
            
            // Check if hostname matches any SAN entry
            if (sans.Any(san => MatchesHostname(san, hostname)))
            {
                return;  // Valid match found
            }

            // Fallback: Check Common Name (CN) in subject
            var subject = cert.SubjectDN.ToString();
            var cn = ExtractCommonName(subject);
            if (cn != null && MatchesHostname(cn, hostname))
            {
                return;  // Valid match found
            }

            throw new TlsFatalAlert(AlertDescription.bad_certificate,
                $"Certificate hostname mismatch: expected '{hostname}', " +
                $"certificate valid for: {string.Join(", ", sans)}");
        }

        private void ValidateValidity(TlsCertificate tlsCertificate)
        {
            var cert = tlsCertificate.GetX509Certificate();
            var now = DateTime.UtcNow;

            if (now < cert.NotBefore)
            {
                throw new TlsFatalAlert(AlertDescription.certificate_expired,
                    $"Certificate not yet valid (NotBefore: {cert.NotBefore})");
            }

            if (now > cert.NotAfter)
            {
                throw new TlsFatalAlert(AlertDescription.certificate_expired,
                    $"Certificate expired (NotAfter: {cert.NotAfter})");
            }
        }

        private string[] GetSubjectAlternativeNames(Org.BouncyCastle.X509.X509Certificate cert)
        {
            try
            {
                var sans = cert.GetSubjectAlternativeNames();
                if (sans == null)
                    return Array.Empty<string>();

                var result = new System.Collections.Generic.List<string>();
                foreach (System.Collections.IList san in sans)
                {
                    // SAN entry format: [type, value]
                    // type 2 = dNSName
                    if (san.Count >= 2 && san[0] is int type && type == 2)
                    {
                        result.Add(san[1].ToString());
                    }
                }
                return result.ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private string ExtractCommonName(string subject)
        {
            // Parse "CN=example.com, O=Company, ..." → "example.com"
            var parts = subject.Split(',');
            foreach (var part in parts)
            {
                var kv = part.Trim().Split('=');
                if (kv.Length == 2 && kv[0].Trim().Equals("CN", StringComparison.OrdinalIgnoreCase))
                {
                    return kv[1].Trim();
                }
            }
            return null;
        }

        private bool MatchesHostname(string pattern, string hostname)
        {
            // Simple wildcard matching: "*.example.com" matches "www.example.com"
            // Does NOT match "example.com" (wildcard requires at least one subdomain)

            if (string.Equals(pattern, hostname, StringComparison.OrdinalIgnoreCase))
            {
                return true;  // Exact match
            }

            if (pattern.StartsWith("*."))
            {
                var suffix = pattern.Substring(2);  // "example.com"
                var dotIndex = hostname.IndexOf('.');
                
                if (dotIndex > 0)
                {
                    var hostSuffix = hostname.Substring(dotIndex + 1);  // "example.com"
                    return string.Equals(suffix, hostSuffix, StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }
    }
}
```

## Implementation Details

### Certificate Validation Steps

Updated by #33 (2026-11; decision A on #141). In order, each failing closed:

1. **Host identity** (RFC 6125 s6) via `TlsCertificateNameMatcher` (Transport): target is
   `TlsHostName.ToTargetHost` (A-labels, bare IP, no trailing dot); IP literals match only
   iPAddress SANs; dNSName otherwise; CN only when the certificate has no subjectAltName at all;
   wildcard only as the complete left-most label with at least two labels after it (`*.com`,
   `f*.example.com` match nothing). Mismatch: `bad_certificate`.
2. **EKU**: when present it must include serverAuth (or anyExtendedKeyUsage). Else `bad_certificate`.
3. **Validity period** of the leaf. Else `certificate_expired`.
4. **Path validation outside BC** via `ServerCertificateVerifier` (Transport): `X509Chain` on the
   server's DER chain in the Editor/desktop, revocation off, intermediate fetch bounded at 5 s;
   untrusted: `unknown_ca`, time: `certificate_expired`, other: `bad_certificate`. iOS/Android:
   no verifier yet, every chain rejected with `certificate_unknown` (native bridges: #239).
5. **`TlsUtilities.CheckPeerSigAlgs`** on the platform-built path (leaf to anchor); unsupported
   keys: `unsupported_certificate`.

### Client Certificate Support

`GetClientCredentials()` returns `null` because client certificates (mutual TLS) are not implemented in this phase. This is standard for most HTTPS clients.

## Security Status

Implemented (#33): chain validation (Editor/desktop), RFC 6125 host identity, EKU, validity,
signature-algorithm check on the built path. Not implemented: revocation (off by decision A, no
OCSP request), native iOS/Android verifiers (#239; devices reject every certificate meanwhile),
pinning / user validation callback (#117).

## Error Handling

Throws `TlsFatalAlert` on validation failure:
- `AlertDescription.certificate_unknown` - chain empty or cert missing
- `AlertDescription.bad_certificate` - hostname mismatch, EKU, malformed names, invalid path
- `AlertDescription.certificate_expired` - validity period check failed (leaf or path)
- `AlertDescription.unknown_ca` - path does not end at a trusted anchor
- `AlertDescription.unsupported_certificate` - key or signature algorithm outside the restricted set

BouncyCastle will abort the handshake and bubble this exception up to the caller.

## Namespace

`TurboHTTP.Transport.BouncyCastle`

## Validation Criteria

- [ ] Class compiles without errors
- [ ] Hostname validation works for exact matches
- [ ] Wildcard certificates work correctly
- [ ] Expired certificates are rejected
- [ ] Not-yet-valid certificates are rejected
- [ ] Missing certificates trigger fatal alert

## Testing Notes

Test cases:
1. **Valid certificate**: `https://www.google.com`
2. **Wildcard certificate**: Find a site using `*.example.com`
3. **Expired certificate**: `https://expired.badssl.com/`
4. **Hostname mismatch**: Connect to `google.com` but validate against `example.com`

## References

- [RFC 5280 - X.509 Certificates](https://tools.ietf.org/html/rfc5280)
- [RFC 6125 - Certificate Hostname Verification](https://tools.ietf.org/html/rfc6125)
- [BouncyCastle X.509 API](https://github.com/bcgit/bc-csharp/tree/master/crypto/src/x509)
