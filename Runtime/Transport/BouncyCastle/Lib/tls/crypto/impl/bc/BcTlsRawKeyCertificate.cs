using System;
using System.IO;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.X509;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Engines;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Parameters;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Signers;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Math;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Turbo; // turbo-strip: E5 TurboKeyFactory; Asn1.Cmp, Security and Utilities usings dropped

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto.Impl.BC
{
    /// <summary>Implementation class for a single X.509 certificate based on the BC light-weight API.</summary>
    public class BcTlsRawKeyCertificate
        : TlsCertificate
    {
        protected readonly BcTlsCrypto m_crypto;
        protected readonly SubjectPublicKeyInfo m_keyInfo;

        protected DHPublicKeyParameters m_pubKeyDH = null;
        protected ECPublicKeyParameters m_pubKeyEC = null;
        // turbo-strip: E5 Ed25519/Ed448 key fields cut (decision E)
        protected RsaKeyParameters m_pubKeyRsa = null;

        /// <exception cref="IOException"/>
        public BcTlsRawKeyCertificate(BcTlsCrypto crypto, byte[] encoding)
            : this(crypto, SubjectPublicKeyInfo.GetInstance(encoding))
        {
        }

        public BcTlsRawKeyCertificate(BcTlsCrypto crypto, SubjectPublicKeyInfo keyInfo)
        {
            m_crypto = crypto;
            m_keyInfo = keyInfo;
        }

        public virtual SubjectPublicKeyInfo SubjectPublicKeyInfo => m_keyInfo;

        /// <exception cref="IOException"/>
        public virtual TlsEncryptor CreateEncryptor(int tlsCertificateRole)
        {
            ValidateKeyUsage(KeyUsage.KeyEncipherment);

            switch (tlsCertificateRole)
            {
            case TlsCertificateRole.RsaEncryption:
            {
                this.m_pubKeyRsa = GetPubKeyRsa();
                return new BcTlsRsaEncryptor(m_crypto, m_pubKeyRsa);
            }
            // TODO[gmssl]
            //case TlsCertificateRole.Sm2Encryption:
            //{
            //    this.m_pubKeyEC = GetPubKeyEC();
            //    return new BcTlsSM2Encryptor(m_crypto, m_pubKeyEC);
            //}
            }

            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        /// <exception cref="IOException"/>
        public virtual TlsVerifier CreateVerifier(short signatureAlgorithm)
        {
            // turbo-strip: E5 Ed25519/Ed448 legacy verifier cut (decision E)

            ValidateKeyUsage(KeyUsage.DigitalSignature);

            switch (signatureAlgorithm)
            {
            // turbo-strip: E5 DSA verifier cut

            case SignatureAlgorithm.ecdsa:
                return new BcTlsECDsaVerifier(m_crypto, GetPubKeyEC());

            case SignatureAlgorithm.rsa:
            {
                ValidateRsa_Pkcs1();
                return new BcTlsRsaVerifier(m_crypto, GetPubKeyRsa());
            }

            case SignatureAlgorithm.rsa_pss_pss_sha256:
            case SignatureAlgorithm.rsa_pss_pss_sha384:
            case SignatureAlgorithm.rsa_pss_pss_sha512:
            {
                ValidateRsa_Pss_Pss(signatureAlgorithm);
                int signatureScheme = SignatureScheme.From(HashAlgorithm.Intrinsic, signatureAlgorithm);
                return new BcTlsRsaPssVerifier(m_crypto, GetPubKeyRsa(), signatureScheme);
            }

            case SignatureAlgorithm.rsa_pss_rsae_sha256:
            case SignatureAlgorithm.rsa_pss_rsae_sha384:
            case SignatureAlgorithm.rsa_pss_rsae_sha512:
            {
                ValidateRsa_Pss_Rsae();
                int signatureScheme = SignatureScheme.From(HashAlgorithm.Intrinsic, signatureAlgorithm);
                return new BcTlsRsaPssVerifier(m_crypto, GetPubKeyRsa(), signatureScheme);
            }

            // TODO[RFC 9189]
            case SignatureAlgorithm.gostr34102012_256:
            case SignatureAlgorithm.gostr34102012_512:

            default:
                throw new TlsFatalAlert(AlertDescription.internal_error);
            }
        }

        /// <exception cref="IOException"/>
        public virtual Tls13Verifier CreateVerifier(int signatureScheme)
        {
            ValidateKeyUsage(KeyUsage.DigitalSignature);

            switch (signatureScheme)
            {
            case SignatureScheme.ecdsa_brainpoolP256r1tls13_sha256:
            case SignatureScheme.ecdsa_brainpoolP384r1tls13_sha384:
            case SignatureScheme.ecdsa_brainpoolP512r1tls13_sha512:
            case SignatureScheme.ecdsa_secp256r1_sha256:
            case SignatureScheme.ecdsa_secp384r1_sha384:
            case SignatureScheme.ecdsa_secp521r1_sha512:
            case SignatureScheme.ecdsa_sha1:
            {
                int cryptoHashAlgorithm = SignatureScheme.GetCryptoHashAlgorithm(signatureScheme);
                IDigest digest = m_crypto.CreateDigest(cryptoHashAlgorithm);

                // turbo-strip: E5 RFC 8446 4.2.3: an ecdsa_secp* scheme binds the key's curve
                ECPublicKeyParameters publicKey = GetPubKeyEC();
                int namedGroup = SignatureScheme.GetNamedGroup(signatureScheme);
                if (namedGroup >= 0 && !publicKey.Parameters.Equals(BcTlsECDomain.GetDomainParameters(namedGroup)))
                    throw new TlsFatalAlert(AlertDescription.illegal_parameter, "Scheme does not match key's curve");

                ISigner verifier = new DsaDigestSigner(new ECDsaSigner(), digest);
                verifier.Init(false, publicKey);

                return new BcTls13Verifier(verifier);
            }

            // turbo-strip: E5 ed25519/ed448 verifiers cut (decision E)

            case SignatureScheme.rsa_pkcs1_sha1:
            case SignatureScheme.rsa_pkcs1_sha256:
            case SignatureScheme.rsa_pkcs1_sha384:
            case SignatureScheme.rsa_pkcs1_sha512:
            {
                ValidateRsa_Pkcs1();

                int cryptoHashAlgorithm = SignatureScheme.GetCryptoHashAlgorithm(signatureScheme);
                IDigest digest = m_crypto.CreateDigest(cryptoHashAlgorithm);

                RsaDigestSigner verifier = new RsaDigestSigner(digest,
                    TlsCryptoUtilities.GetOidForHash(cryptoHashAlgorithm));
                verifier.Init(false, GetPubKeyRsa());

                return new BcTls13Verifier(verifier);
            }

            case SignatureScheme.rsa_pss_pss_sha256:
            case SignatureScheme.rsa_pss_pss_sha384:
            case SignatureScheme.rsa_pss_pss_sha512:
            {
                ValidateRsa_Pss_Pss(SignatureScheme.GetSignatureAlgorithm(signatureScheme));

                int cryptoHashAlgorithm = SignatureScheme.GetCryptoHashAlgorithm(signatureScheme);
                IDigest digest = m_crypto.CreateDigest(cryptoHashAlgorithm);

                PssSigner verifier = new PssSigner(new RsaEngine(), digest, digest.GetDigestSize());
                verifier.Init(false, GetPubKeyRsa());

                return new BcTls13Verifier(verifier);
            }

            case SignatureScheme.rsa_pss_rsae_sha256:
            case SignatureScheme.rsa_pss_rsae_sha384:
            case SignatureScheme.rsa_pss_rsae_sha512:
            {
                ValidateRsa_Pss_Rsae();

                int cryptoHashAlgorithm = SignatureScheme.GetCryptoHashAlgorithm(signatureScheme);
                IDigest digest = m_crypto.CreateDigest(cryptoHashAlgorithm);

                PssSigner verifier = new PssSigner(new RsaEngine(), digest, digest.GetDigestSize());
                verifier.Init(false, GetPubKeyRsa());

                return new BcTls13Verifier(verifier);
            }

            // turbo-strip: E5 sm2sig_sm3, ML-DSA and SLH-DSA verifiers cut

            default:
                throw new TlsFatalAlert(AlertDescription.internal_error);
            }
        }

        /// <exception cref="IOException"/>
        public virtual byte[] GetEncoded()
        {
            return m_keyInfo.GetEncoded(Asn1Encodable.Der);
        }

        /// <exception cref="IOException"/>
        public virtual byte[] GetExtension(DerObjectIdentifier extensionOid)
        {
            return null;
        }

        public virtual BigInteger SerialNumber => null;

        public virtual string SigAlgOid => null;

        public virtual Asn1Encodable GetSigAlgParams() => null;

        /// <exception cref="IOException"/>
        public virtual short GetLegacySignatureAlgorithm()
        {
            AsymmetricKeyParameter publicKey = GetPublicKey();
            if (publicKey.IsPrivate)
                throw new TlsFatalAlert(AlertDescription.internal_error);

            if (!SupportsKeyUsage(KeyUsage.DigitalSignature))
                return -1;

            /*
             * RFC 5246 7.4.6. Client Certificate
             */

            /*
             * RSA public key; the certificate MUST allow the key to be used for signing with the
             * signature scheme and hash algorithm that will be employed in the certificate verify
             * message.
             */
            if (publicKey is RsaKeyParameters)
                return SignatureAlgorithm.rsa;

            // turbo-strip: E5 DSA public key mapping cut

            /*
             * ECDSA-capable public key; the certificate MUST allow the key to be used for signing
             * with the hash algorithm that will be employed in the certificate verify message; the
             * public key MUST use a curve and point format supported by the server.
             */
            if (publicKey is ECPublicKeyParameters)
            {
                // TODO Check the curve and point format
                return SignatureAlgorithm.ecdsa;
            }

            return -1;
        }

        /// <exception cref="IOException"/>
        public virtual DHPublicKeyParameters GetPubKeyDH()
        {
            try
            {
                return (DHPublicKeyParameters)GetPublicKey();
            }
            catch (InvalidCastException e)
            {
                throw new TlsFatalAlert(AlertDescription.certificate_unknown, "Public key not DH", e);
            }
        }

        // turbo-strip: E5 GetPubKeyDss cut

        /// <exception cref="IOException"/>
        public virtual ECPublicKeyParameters GetPubKeyEC()
        {
            try
            {
                return (ECPublicKeyParameters)GetPublicKey();
            }
            catch (InvalidCastException e)
            {
                throw new TlsFatalAlert(AlertDescription.certificate_unknown, "Public key not EC", e);
            }
        }

        // turbo-strip: E5 GetPubKeyEd25519/GetPubKeyEd448/GetPubKeyMLDsa cut

        /// <exception cref="IOException"/>
        public virtual RsaKeyParameters GetPubKeyRsa()
        {
            try
            {
                return (RsaKeyParameters)GetPublicKey();
            }
            catch (InvalidCastException e)
            {
                throw new TlsFatalAlert(AlertDescription.certificate_unknown, "Public key not RSA", e);
            }
        }

        // turbo-strip: E5 GetPubKeySlhDsa cut

        /// <exception cref="IOException"/>
        public virtual bool SupportsSignatureAlgorithm(short signatureAlgorithm)
        {
            return SupportsSignatureAlgorithm(signatureAlgorithm, KeyUsage.DigitalSignature);
        }

        /// <exception cref="IOException"/>
        public virtual bool SupportsSignatureAlgorithmCA(short signatureAlgorithm)
        {
            return SupportsSignatureAlgorithm(signatureAlgorithm, KeyUsage.KeyCertSign);
        }

        /// <exception cref="IOException"/>
        public virtual TlsCertificate CheckUsageInRole(int tlsCertificateRole)
        {
            switch (tlsCertificateRole)
            {
            case TlsCertificateRole.DH:
            {
                ValidateKeyUsage(KeyUsage.KeyAgreement);
                this.m_pubKeyDH = GetPubKeyDH();
                return this;
            }
            case TlsCertificateRole.ECDH:
            {
                ValidateKeyUsage(KeyUsage.KeyAgreement);
                this.m_pubKeyEC = GetPubKeyEC();
                return this;
            }
            }

            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        /// <exception cref="IOException"/>
        protected virtual AsymmetricKeyParameter GetPublicKey()
        {
            try
            {
                return TurboKeyFactory.CreateKey(m_keyInfo); // turbo-strip: E5 whitelisting key factory
            }
            catch (Exception e)
            {
                throw new TlsFatalAlert(AlertDescription.unsupported_certificate, e);
            }
        }

        // TODO[api] Rename parameter to 'keyUsageBit'
        protected virtual bool SupportsKeyUsage(int keyUsageBits)
        {
            return true;
        }

        // turbo-strip: E5 SupportsMLDsa cut

        protected virtual bool SupportsRsa_Pkcs1()
        {
            AlgorithmIdentifier pubKeyAlgID = m_keyInfo.Algorithm;
            return RsaUtilities.SupportsPkcs1(pubKeyAlgID);
        }

        protected virtual bool SupportsRsa_Pss_Pss(short signatureAlgorithm)
        {
            AlgorithmIdentifier pubKeyAlgID = m_keyInfo.Algorithm;
            return RsaUtilities.SupportsPss_Pss(signatureAlgorithm, pubKeyAlgID);
        }

        protected virtual bool SupportsRsa_Pss_Rsae()
        {
            AlgorithmIdentifier pubKeyAlgID = m_keyInfo.Algorithm;
            return RsaUtilities.SupportsPss_Rsae(pubKeyAlgID);
        }

        // turbo-strip: E5 SupportsSlhDsa cut

        /// <exception cref="IOException"/>
        protected virtual bool SupportsSignatureAlgorithm(short signatureAlgorithm, int keyUsage)
        {
            if (!SupportsKeyUsage(keyUsage))
                return false;

            AsymmetricKeyParameter publicKey = GetPublicKey();

            switch (signatureAlgorithm)
            {
            case SignatureAlgorithm.rsa:
                return SupportsRsa_Pkcs1()
                    && publicKey is RsaKeyParameters;

            // turbo-strip: E5 dsa cut

            case SignatureAlgorithm.ecdsa:
            case SignatureAlgorithm.ecdsa_brainpoolP256r1tls13_sha256:
            case SignatureAlgorithm.ecdsa_brainpoolP384r1tls13_sha384:
            case SignatureAlgorithm.ecdsa_brainpoolP512r1tls13_sha512:
                return publicKey is ECPublicKeyParameters;

            // turbo-strip: E5 ed25519/ed448 cut (decision E)

            case SignatureAlgorithm.rsa_pss_rsae_sha256:
            case SignatureAlgorithm.rsa_pss_rsae_sha384:
            case SignatureAlgorithm.rsa_pss_rsae_sha512:
                return SupportsRsa_Pss_Rsae()
                    && publicKey is RsaKeyParameters;

            case SignatureAlgorithm.rsa_pss_pss_sha256:
            case SignatureAlgorithm.rsa_pss_pss_sha384:
            case SignatureAlgorithm.rsa_pss_pss_sha512:
                return SupportsRsa_Pss_Pss(signatureAlgorithm)
                    && publicKey is RsaKeyParameters;

            // TODO[RFC 9189]
            case SignatureAlgorithm.gostr34102012_256:
            case SignatureAlgorithm.gostr34102012_512:

            default:
                return false;
            }
        }

        /// <exception cref="IOException"/>
        // TODO[api] Rename parameter to 'keyUsageBit'
        public virtual void ValidateKeyUsage(int keyUsageBits)
        {
            if (!SupportsKeyUsage(keyUsageBits))
            {
                switch (keyUsageBits)
                {
                case KeyUsage.DigitalSignature:
                    throw new TlsFatalAlert(AlertDescription.certificate_unknown,
                        "KeyUsage does not allow digital signatures");
                case KeyUsage.KeyAgreement:
                    throw new TlsFatalAlert(AlertDescription.certificate_unknown,
                        "KeyUsage does not allow key agreement");
                case KeyUsage.KeyEncipherment:
                    throw new TlsFatalAlert(AlertDescription.certificate_unknown,
                        "KeyUsage does not allow key encipherment");
                default:
                    throw new TlsFatalAlert(AlertDescription.internal_error);
                }
            }
        }

        // turbo-strip: E5 ValidateMLDsa cut

        /// <exception cref="IOException"/>
        protected virtual void ValidateRsa_Pkcs1()
        {
            if (!SupportsRsa_Pkcs1())
                throw new TlsFatalAlert(AlertDescription.certificate_unknown,
                    "No support for rsa_pkcs1 signature schemes");
        }

        /// <exception cref="IOException"/>
        protected virtual void ValidateRsa_Pss_Pss(short signatureAlgorithm)
        {
            if (!SupportsRsa_Pss_Pss(signatureAlgorithm))
                throw new TlsFatalAlert(AlertDescription.certificate_unknown,
                    "No support for rsa_pss_pss signature schemes");
        }

        /// <exception cref="IOException"/>
        protected virtual void ValidateRsa_Pss_Rsae()
        {
            if (!SupportsRsa_Pss_Rsae())
                throw new TlsFatalAlert(AlertDescription.certificate_unknown,
                    "No support for rsa_pss_rsae signature schemes");
        }

        // turbo-strip: E5 ValidateSlhDsa cut
    }
}
