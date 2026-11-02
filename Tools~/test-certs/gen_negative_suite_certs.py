#!/usr/bin/env python3
"""Generates the committed server identities for the negative TLS suite (#113).

Unity's Mono cannot create certificates at runtime (#151), and a loopback server needs the leaf's
private key, so every leaf is committed as a PKCS#12 blob (SHA1/3DES PBE and a SHA1 MAC, which
Mono reads) in Tests/Runtime/Transport/Tls/NegativeSuiteTestCertificates.cs. The CA keys are
discarded; all leaves share one RSA key. Test material only, not a secret. Requires the
`cryptography` package (>= 38).

    python3 'Tools~/test-certs/gen_negative_suite_certs.py' \
        > Tests/Runtime/Transport/Tls/NegativeSuiteTestCertificates.cs
"""
import base64
import datetime as dt
import ipaddress

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.hazmat.primitives.serialization import pkcs12
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

PASSWORD = b"turbohttp-test"
D = dt.datetime
CA_FROM, CA_TO = D(2000, 1, 1), D(2126, 1, 1)
LEAF_FROM, LEAF_TO = D(2001, 1, 1), D(2125, 1, 1)

ROOT_CN = "TurboHTTP Negative Suite Root"
INTER_CN = "TurboHTTP Negative Suite Intermediate"
ROGUE_CN = "TurboHTTP Negative Suite Rogue Root"


def rsa_key():
    return rsa.generate_private_key(public_exponent=65537, key_size=2048)


def name(cn):
    return x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, cn)])


def ca(subject_cn, key, issuer_cn, issuer_key):
    return (x509.CertificateBuilder()
            .subject_name(name(subject_cn)).issuer_name(name(issuer_cn))
            .public_key(key.public_key()).serial_number(x509.random_serial_number())
            .not_valid_before(CA_FROM).not_valid_after(CA_TO)
            .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
            .add_extension(x509.KeyUsage(False, False, False, False, False, True, True, False, False),
                           critical=True)
            .add_extension(x509.SubjectKeyIdentifier.from_public_key(key.public_key()), critical=False)
            .sign(issuer_key, hashes.SHA256()))


def leaf(cn, issuer_cn, issuer_key, san, not_before=LEAF_FROM, not_after=LEAF_TO):
    b = (x509.CertificateBuilder()
         .subject_name(name(cn)).issuer_name(name(issuer_cn))
         .public_key(LEAF_KEY.public_key()).serial_number(x509.random_serial_number())
         .not_valid_before(not_before).not_valid_after(not_after)
         .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
         .add_extension(x509.KeyUsage(True, False, True, False, False, False, False, False, False),
                        critical=True)
         .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False))
    if san is not None:
        b = b.add_extension(x509.SubjectAlternativeName(san), critical=False)
    return b.sign(issuer_key, hashes.SHA256())


def dns(*names):
    return [x509.DNSName(n) for n in names]


root_key, inter_key, rogue_key = rsa_key(), rsa_key(), rsa_key()
LEAF_KEY = rsa_key()
root = ca(ROOT_CN, root_key, ROOT_CN, root_key)
inter = ca(INTER_CN, inter_key, ROOT_CN, root_key)
LOCAL = dns("localhost") + [x509.IPAddress(ipaddress.ip_address("127.0.0.1")),
                            x509.IPAddress(ipaddress.ip_address("::1"))]

leaves = [
    ("Valid", "Root-issued, SAN localhost, 127.0.0.1, ::1, serverAuth, 2001..2125: the positive control.",
     leaf("localhost", ROOT_CN, root_key, LOCAL)),
    ("Expired", "Root-issued localhost leaf valid 2001-01-01..2002-01-01 (expired).",
     leaf("localhost", ROOT_CN, root_key, LOCAL, D(2001, 1, 1), D(2002, 1, 1))),
    ("NotYetValid", "Root-issued localhost leaf valid 2100-01-01..2125-01-01 (not yet valid).",
     leaf("localhost", ROOT_CN, root_key, LOCAL, D(2100, 1, 1), LEAF_TO)),
    ("WrongHost", "Root-issued leaf for wrong.example only (CN and SAN).",
     leaf("wrong.example", ROOT_CN, root_key, dns("wrong.example"))),
    ("TldWildcard", "Root-issued leaf with CN and SAN *.com (RFC 6125 6.4.3 wildcard abuse).",
     leaf("*.com", ROOT_CN, root_key, dns("*.com"))),
    ("CnOnlyWithSan", "Root-issued leaf with CN=localhost but SAN wrong.example (CN must be ignored).",
     leaf("localhost", ROOT_CN, root_key, dns("wrong.example"))),
    ("IpInDnsSan", "Root-issued leaf with CN=127.0.0.1 and dNSName SAN 127.0.0.1, no iPAddress SAN.",
     leaf("127.0.0.1", ROOT_CN, root_key, dns("127.0.0.1"))),
    ("ViaIntermediate", "localhost leaf issued by the intermediate; served without it (missing intermediate).",
     leaf("localhost", INTER_CN, inter_key, LOCAL)),
    ("SelfSigned", "Self-signed localhost leaf (its own untrusted root).",
     leaf("localhost", "localhost", LEAF_KEY, LOCAL)),
    ("UntrustedRoot", "localhost leaf issued by a rogue root that nothing trusts.",
     leaf("localhost", ROGUE_CN, rogue_key, LOCAL)),
]

encryption = (serialization.PrivateFormat.PKCS12.encryption_builder()
              .kdf_rounds(2048)
              .key_cert_algorithm(pkcs12.PBES.PBESv1SHA1And3KeyTripleDESCBC)
              .hmac_hash(hashes.SHA1())
              .build(PASSWORD))


def emit_b64(out, ident, b64, kind, doc=None):
    if doc:
        out.append(f"        /// <summary>{doc.replace('&', '&amp;')}</summary>")
    out.append(f"        {"private" if kind else "internal"} const string {ident}{kind} =")
    chunks = [b64[i:i + 96] for i in range(0, len(b64), 96)]
    for i, c in enumerate(chunks):
        end = ";" if i == len(chunks) - 1 else " +"
        out.append(f"            \"{c}\"{end}")
    out.append("")


out = [
    "// <auto-generated>",
    "// Generated by Tools~/test-certs/gen_negative_suite_certs.py; do not edit by hand.",
    "// </auto-generated>",
    "using System;",
    "using System.Security.Cryptography;",
    "using System.Security.Cryptography.X509Certificates;",
    "",
    "namespace TurboHTTP.Tests.Transport.Tls",
    "{",
    "    /// <summary>",
    "    /// Committed server identities for the negative TLS suite (#113): one test root (DER), an",
    "    /// intermediate (DER) and PKCS#12 leaves (SHA1/3DES, password <see cref=\"Password\"/>) sharing one",
    "    /// RSA 2048 key. Test material only; the CA keys were discarded at generation time.",
    "    /// </summary>",
    "    internal static class NegativeSuiteTestCertificates",
    "    {",
    f"        internal const string Password = \"{PASSWORD.decode()}\";",
    "",
    f"        /// <summary>{ROOT_CN}: RSA 2048 self-signed CA, 2000-01-01..2126-01-01.</summary>",
    "        internal static byte[] Root => Convert.FromBase64String(RootDer);",
    "",
    f"        /// <summary>{INTER_CN}: issued by the root; never sent by the server.</summary>",
    "        internal static byte[] Intermediate => Convert.FromBase64String(IntermediateDer);",
    "",
]
out += [
    "        /// <summary>",
    "        /// Loads one of the PKCS#12 leaves (caller disposes), or null when this runtime cannot read",
    "        /// PKCS#12.",
    "        /// </summary>",
    "        internal static X509Certificate2 TryLoad(string pfxBase64)",
    "        {",
    "            try",
    "            {",
    "                return new X509Certificate2(",
    "                    Convert.FromBase64String(pfxBase64), Password, X509KeyStorageFlags.Exportable);",
    "            }",
    "            catch (CryptographicException)",
    "            {",
    "                return null;",
    "            }",
    "            catch (NotSupportedException)",
    "            {",
    "                return null;",
    "            }",
    "        }",
    "",
]
emit_b64(out, "Root", base64.b64encode(root.public_bytes(serialization.Encoding.DER)).decode(), "Der")
emit_b64(out, "Intermediate", base64.b64encode(inter.public_bytes(serialization.Encoding.DER)).decode(), "Der")
for ident, doc, cert in leaves:
    pfx = pkcs12.serialize_key_and_certificates(ident.encode(), LEAF_KEY, cert, None, encryption)
    emit_b64(out, ident, base64.b64encode(pfx).decode(), "", "PKCS#12: " + doc)
out[-1] = "    }"
out.append("}")
print("\n".join(out))
