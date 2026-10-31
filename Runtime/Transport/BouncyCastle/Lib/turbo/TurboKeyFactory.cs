// Copyright (c) 2026 fatal10110 and contributors. Licensed under the MIT License.

using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.Pkcs;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.Sec;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.X509;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.X9;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Parameters;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Math;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Math.EC;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Turbo
{
    /// <summary>
    /// Whitelisting replacement for <c>PublicKeyFactory.CreateKey</c> on the TLS client path
    /// (STRIP_PLAN_V2.md §3 E5, E8).
    /// </summary>
    /// <remarks>
    /// Accepts only:
    /// <list type="bullet">
    /// <item>rsaEncryption and id-RSASSA-PSS keys with a 2048–8192 bit modulus and a public exponent of
    /// 3 up to 64 bits;</item>
    /// <item>id-ecPublicKey keys on the named curves P-256, P-384 and P-521, with a compressed or
    /// uncompressed point.</item>
    /// </list>
    /// The size bounds are checked on the parsed integers, and the EC parameters on their raw ASN.1 form,
    /// before any key or curve object is built, so no small-factor test, primality test or modular
    /// exponentiation runs on an out-of-range key. Keys are then built only through public constructors, so
    /// master's validation (even modulus, small factors, point on curve, point at infinity) still runs. Every failure, malformed input included,
    /// surfaces as <see cref="ArgumentException"/> (exactly that type; a null argument gives
    /// <see cref="ArgumentNullException"/>).
    /// </remarks>
    public static class TurboKeyFactory
    {
        /// <summary>Smallest accepted RSA modulus, in bits.</summary>
        public const int RsaMinModulusBits = 2048;

        /// <summary>Largest accepted RSA modulus, in bits (matches E7's <c>RsaMaxSize</c>).</summary>
        public const int RsaMaxModulusBits = 8192;

        /// <summary>Largest accepted RSA public exponent, in bits.</summary>
        public const int RsaMaxExponentBits = 64;

        /// <summary>Create a public key from a DER-encoded SubjectPublicKeyInfo.</summary>
        /// <exception cref="ArgumentException">The encoding or the key is malformed or not supported.</exception>
        public static AsymmetricKeyParameter CreateKey(byte[] encoded)
        {
            if (encoded == null)
                throw new ArgumentNullException(nameof(encoded));

            SubjectPublicKeyInfo keyInfo;
            try
            {
                keyInfo = SubjectPublicKeyInfo.GetInstance(Asn1Object.FromByteArray(encoded));
            }
            catch (Exception e)
            {
                throw new ArgumentException("Malformed SubjectPublicKeyInfo", nameof(encoded), e);
            }

            return CreateKey(keyInfo);
        }

        /// <summary>Create a public key from a SubjectPublicKeyInfo.</summary>
        /// <exception cref="ArgumentException">The key is malformed or not supported.</exception>
        public static AsymmetricKeyParameter CreateKey(SubjectPublicKeyInfo keyInfo)
        {
            if (keyInfo == null)
                throw new ArgumentNullException(nameof(keyInfo));

            try
            {
                return CreateKeyUnchecked(keyInfo);
            }
            catch (Exception e)
            {
                // Exactly ArgumentException passes through; everything else (IOException from ASN.1 parsing,
                // InvalidCastException, ArgumentNullException, ...) is wrapped.
                if (e.GetType() == typeof(ArgumentException))
                    throw;

                throw new ArgumentException("Invalid or unsupported public key", nameof(keyInfo), e);
            }
        }

        private static AsymmetricKeyParameter CreateKeyUnchecked(SubjectPublicKeyInfo keyInfo)
        {
            DerObjectIdentifier algOid = keyInfo.Algorithm.Algorithm;

            if (PkcsObjectIdentifiers.RsaEncryption.Equals(algOid) || PkcsObjectIdentifiers.IdRsassaPss.Equals(algOid))
                return CreateRsaKey(keyInfo);

            if (X9ObjectIdentifiers.IdECPublicKey.Equals(algOid))
                return CreateECKey(keyInfo);

            throw new ArgumentException("Unsupported public key algorithm: " + algOid.Id);
        }

        private static RsaKeyParameters CreateRsaKey(SubjectPublicKeyInfo keyInfo)
        {
            // RFC 8017 A.1.1 / RFC 4055 §1.2: RSAPublicKey ::= SEQUENCE { modulus, publicExponent }.
            RsaPublicKeyStructure pubKey = RsaPublicKeyStructure.GetInstance(keyInfo.ParsePublicKey());
            BigInteger n = pubKey.Modulus;
            BigInteger e = pubKey.PublicExponent;

            // Bounds first: nothing below may run a small-factor test, Miller-Rabin or modexp on an
            // oversized key (STRIP_PLAN_V2.md §3 E5, E7).
            if (n.SignValue <= 0 || n.BitLength < RsaMinModulusBits || n.BitLength > RsaMaxModulusBits)
                throw new ArgumentException("RSA modulus size out of range");

            // RFC 8017 §3.1: 3 <= e; the 64-bit cap bounds the verification modexp.
            if (e.SignValue <= 0 || e.BitLength < 2 || e.BitLength > RsaMaxExponentBits)
                throw new ArgumentException("RSA public exponent out of range");

            // Public constructor: master's even-modulus and small-factor checks run.
            return new RsaKeyParameters(false, n, e);
        }

        private static ECPublicKeyParameters CreateECKey(SubjectPublicKeyInfo keyInfo)
        {
            // RFC 5480 §2.1.1: only the namedCurve form. The raw parameter is tested before any X9 parsing:
            // X962Parameters/X9ECParameters would build the curve of a specifiedCurve (prime test on an
            // attacker-chosen field) before IsNamedCurve could reject it. Missing, NULL (implicitCurve) and
            // SEQUENCE (specifiedCurve) parameters are all rejected here.
            Asn1Encodable algParams = keyInfo.Algorithm.Parameters;
            if (algParams == null)
                throw new ArgumentException("EC parameters missing");

            if (!(algParams.ToAsn1Object() is DerObjectIdentifier curveOid))
                throw new ArgumentException("EC parameters must be a named curve");

            if (!IsSupportedCurve(curveOid))
                throw new ArgumentException("Unsupported EC curve: " + curveOid.Id);

            // RFC 5480 §2.2: compressed (0x02/0x03) or uncompressed (0x04) only; no infinity, no hybrid.
            byte[] encoding = keyInfo.PublicKey.GetOctets();
            if (encoding.Length < 1 || encoding[0] < 0x02 || encoding[0] > 0x04)
                throw new ArgumentException("Unsupported EC point encoding");

            ECNamedDomainParameters domainParameters = ECNamedDomainParameters.LookupOid(curveOid);
            ECPoint q = domainParameters.Curve.DecodePoint(encoding);

            // Public constructor: master's ValidatePublicPoint (infinity, on-curve) runs.
            return new ECPublicKeyParameters("EC", q, domainParameters);
        }

        private static bool IsSupportedCurve(DerObjectIdentifier curveOid)
        {
            // SecP256r1 is the same OID as X9ObjectIdentifiers.Prime256v1.
            return SecObjectIdentifiers.SecP256r1.Equals(curveOid)
                || SecObjectIdentifiers.SecP384r1.Equals(curveOid)
                || SecObjectIdentifiers.SecP521r1.Equals(curveOid);
        }
    }
}
