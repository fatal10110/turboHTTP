// Copyright (c) 2026 fatal10110 and contributors. Licensed under the MIT License.

using System.Collections.Generic;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.Pkcs;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.X509;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.X9;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Turbo
{
    /// <summary>
    /// Display names for certificate signature algorithms (strip-v2 edge cut E8). Replaces
    /// <c>X509SignatureUtilities.GetSignatureName</c>, which pulls the SignerUtilities registry into the closure.
    /// </summary>
    /// <remarks>
    /// Keyed by OID only: algorithm parameters (RSASSA-PSS, ecdsa-with-SHA2) are not parsed. Names follow
    /// upstream's <c>SignerUtilities</c> spelling for the same OIDs. Any other OID yields its dotted string.
    /// </remarks>
    internal static class TurboSignatureNames
    {
        private static readonly Dictionary<DerObjectIdentifier, string> Names =
            new Dictionary<DerObjectIdentifier, string>
            {
                { PkcsObjectIdentifiers.Sha1WithRsaEncryption, "SHA-1withRSA" },
                { PkcsObjectIdentifiers.Sha224WithRsaEncryption, "SHA-224withRSA" },
                { PkcsObjectIdentifiers.Sha256WithRsaEncryption, "SHA-256withRSA" },
                { PkcsObjectIdentifiers.Sha384WithRsaEncryption, "SHA-384withRSA" },
                { PkcsObjectIdentifiers.Sha512WithRsaEncryption, "SHA-512withRSA" },
                { PkcsObjectIdentifiers.IdRsassaPss, "PSSwithRSA" },
                { X9ObjectIdentifiers.ECDsaWithSha1, "SHA-1withECDSA" },
                { X9ObjectIdentifiers.ECDsaWithSha224, "SHA-224withECDSA" },
                { X9ObjectIdentifiers.ECDsaWithSha256, "SHA-256withECDSA" },
                { X9ObjectIdentifiers.ECDsaWithSha384, "SHA-384withECDSA" },
                { X9ObjectIdentifiers.ECDsaWithSha512, "SHA-512withECDSA" },
            };

        internal static string GetName(AlgorithmIdentifier sigAlgID)
        {
            DerObjectIdentifier oid = sigAlgID.Algorithm;
            return Names.TryGetValue(oid, out string name) ? name : oid.GetID();
        }
    }
}
