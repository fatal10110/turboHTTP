#!/usr/bin/env python3
"""Generates the committed DER test certificates for the BouncyCastle chain-validation tests (#33).

Unity's Mono cannot create certificates at runtime (#151), so the material is committed as
base64 in Tests/Runtime/Transport/Tls/ChainValidationTestCertificates.cs. Test material only:
the private keys are discarded. Requires the `cryptography` package.

    python3 'Tools~/test-certs/gen_chain_validation_certs.py' \
        > Tests/Runtime/Transport/Tls/ChainValidationTestCertificates.cs
"""
import base64
import datetime as dt
import ipaddress

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

D = dt.datetime
ROOT_FROM, ROOT_TO = D(2000, 1, 1), D(2126, 1, 1)
LEAF_FROM, LEAF_TO = D(2001, 1, 1), D(2125, 1, 1)


def name(cn):
    return x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, cn)])


def build(subject_cn, key, issuer_cn, issuer_key, *, ca=False, path_len=None, san=None,
          eku=True, not_before=LEAF_FROM, not_after=LEAF_TO, hash_alg=None,
          name_constraints=None):
    b = (x509.CertificateBuilder()
         .subject_name(name(subject_cn))
         .issuer_name(name(issuer_cn))
         .public_key(key.public_key())
         .serial_number(x509.random_serial_number())
         .not_valid_before(not_before)
         .not_valid_after(not_after)
         .add_extension(x509.BasicConstraints(ca=ca, path_length=path_len), critical=True)
         .add_extension(x509.SubjectKeyIdentifier.from_public_key(key.public_key()), critical=False))
    if ca:
        b = b.add_extension(x509.KeyUsage(False, False, False, False, False, True, True, False, False),
                            critical=True)
    else:
        b = b.add_extension(x509.KeyUsage(True, False, False, False, False, False, False, False, False),
                            critical=True)
        if eku:
            b = b.add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
    if issuer_key is not key:
        b = b.add_extension(x509.AuthorityKeyIdentifier.from_issuer_public_key(issuer_key.public_key()),
                            critical=False)
    if san is not None:
        b = b.add_extension(x509.SubjectAlternativeName(san), critical=False)
    if name_constraints is not None:
        b = b.add_extension(x509.NameConstraints(permitted_subtrees=name_constraints,
                                                 excluded_subtrees=None), critical=True)
    return b.sign(issuer_key, hash_alg or hashes.SHA256())


def dns(*names):
    return [x509.DNSName(n) for n in names]


LOCAL = dns("localhost") + [x509.IPAddress(ipaddress.ip_address("127.0.0.1")),
                            x509.IPAddress(ipaddress.ip_address("::1"))]

p256 = lambda: ec.generate_private_key(ec.SECP256R1())
p384 = lambda: ec.generate_private_key(ec.SECP384R1())

root_a_key, root_b_key, rogue_key, inter_key = p256(), p384(), p256(), p256()
RA, RB, RR, NC = "TurboHTTP Test Root A", "TurboHTTP Test Root B P-384", "TurboHTTP Rogue Root", \
    "TurboHTTP Test Constrained CA"

certs = [
    ("RootA", "Root A: ECDSA P-256 self-signed CA, 2000-01-01..2126-01-01.",
     build(RA, root_a_key, RA, root_a_key, ca=True, not_before=ROOT_FROM, not_after=ROOT_TO)),
    ("LeafLocalhost", "ECDSA P-256 leaf from Root A: SAN localhost, 127.0.0.1, ::1; serverAuth.",
     build("localhost", p256(), RA, root_a_key, san=LOCAL)),
    ("LeafExpired", "Leaf from Root A for localhost, valid 2001-01-01..2002-01-01 (expired).",
     build("localhost", p256(), RA, root_a_key, san=LOCAL, not_after=D(2002, 1, 1))),
    ("LeafNotYetValid", "Leaf from Root A for localhost, valid 2100-01-01..2101-01-01.",
     build("localhost", p256(), RA, root_a_key, san=LOCAL, not_before=D(2100, 1, 1),
           not_after=D(2101, 1, 1))),
    ("LeafClientAuthOnly", "Leaf from Root A for localhost whose EKU is clientAuth only.",
     None),
    ("LeafTldWildcard", "Leaf from Root A with SAN *.com only.",
     build("example.com", p256(), RA, root_a_key, san=dns("*.com"))),
    ("LeafIdn", "Leaf from Root A with SAN xn--bcher-kva.example (bücher.example).",
     build("xn--bcher-kva.example", p256(), RA, root_a_key, san=dns("xn--bcher-kva.example"))),
    ("LeafCnOnly", "Leaf from Root A with CN=example.com and no subjectAltName extension.",
     build("example.com", p256(), RA, root_a_key)),
    ("LeafCnWithSan", "Leaf from Root A with CN=example.com but SAN other.example only.",
     build("example.com", p256(), RA, root_a_key, san=dns("other.example"))),
    ("RootB", "Root B: ECDSA P-384 self-signed CA (ecdsa-with-SHA384).",
     build(RB, root_b_key, RB, root_b_key, ca=True, not_before=ROOT_FROM, not_after=ROOT_TO,
           hash_alg=hashes.SHA384())),
    ("LeafP384", "ECDSA P-384 leaf from Root B for localhost (ecdsa-with-SHA384).",
     build("localhost", p384(), RB, root_b_key, san=LOCAL, hash_alg=hashes.SHA384())),
    ("ConstrainedCa", "Intermediate from Root A, pathLen 0, critical nameConstraints permitted dNSName allowed.test.",
     build(NC, inter_key, RA, root_a_key, ca=True, path_len=0, not_before=ROOT_FROM,
           not_after=ROOT_TO, name_constraints=dns("allowed.test"))),
    ("LeafPermitted", "Leaf from the constrained CA with SAN www.allowed.test.",
     build("www.allowed.test", p256(), NC, inter_key, san=dns("www.allowed.test"))),
    ("LeafExcluded", "Leaf from the constrained CA with SAN www.forbidden.test (violates the constraint).",
     build("www.forbidden.test", p256(), NC, inter_key, san=dns("www.forbidden.test"))),
    ("RogueRoot", "An unrelated self-signed ECDSA P-256 CA nothing trusts.",
     build(RR, rogue_key, RR, rogue_key, ca=True, not_before=ROOT_FROM, not_after=ROOT_TO)),
    ("LeafFromRogue", "Leaf for localhost signed by the rogue root.",
     build("localhost", p256(), RR, rogue_key, san=LOCAL)),
]

# clientAuth-only leaf (EKU present without serverAuth).
_k = p256()
certs[4] = (certs[4][0], certs[4][1],
            x509.CertificateBuilder().subject_name(name("localhost")).issuer_name(name(RA))
            .public_key(_k.public_key()).serial_number(x509.random_serial_number())
            .not_valid_before(LEAF_FROM).not_valid_after(LEAF_TO)
            .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.CLIENT_AUTH]), critical=False)
            .add_extension(x509.SubjectAlternativeName(LOCAL), critical=False)
            .sign(root_a_key, hashes.SHA256()))

out = []
out.append("// <auto-generated>")
out.append("// Generated by Tools~/test-certs/gen_chain_validation_certs.py; do not edit by hand.")
out.append("// </auto-generated>")
out.append("using System;")
out.append("")
out.append("namespace TurboHTTP.Tests.Transport.Tls")
out.append("{")
out.append("    /// <summary>")
out.append("    /// Committed DER test certificates for the chain-validation and host-matching tests (#33).")
out.append("    /// Test material only; the private keys were discarded at generation time.")
out.append("    /// </summary>")
out.append("    internal static class ChainValidationTestCertificates")
out.append("    {")
for ident, doc, cert in certs:
    der = cert.public_bytes(serialization.Encoding.DER)
    b64 = base64.b64encode(der).decode()
    out.append(f"        /// <summary>{doc.replace('&', '&amp;')}</summary>")
    out.append(f"        internal static byte[] {ident} => Convert.FromBase64String(")
    chunks = [b64[i:i + 96] for i in range(0, len(b64), 96)]
    for i, c in enumerate(chunks):
        end = ");" if i == len(chunks) - 1 else " +"
        out.append(f"            \"{c}\"{end}")
    out.append("")
out[-1] = "    }"
out.append("}")
print("\n".join(out))
