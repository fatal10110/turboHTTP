using System;
using System.Collections.Generic;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Digests;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Engines;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Macs;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Modes;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Prng;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Math;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Security;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Utilities;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto.Impl.BC
{
    /**
     * Class for providing cryptographic services for TLS based on implementations in the BC light-weight API.
     * <p>
     *     This class provides default implementations for everything. If you need to customise it, extend the class
     *     and override the appropriate methods.
     * </p>
     */
    public class BcTlsCrypto
        : AbstractTlsCrypto
    {
        private readonly SecureRandom m_entropySource;

        public BcTlsCrypto()
            : this(CryptoServicesRegistrar.GetSecureRandom())
        {
        }

        public BcTlsCrypto(SecureRandom entropySource)
        {
            if (entropySource == null)
                throw new ArgumentNullException(nameof(entropySource));

            this.m_entropySource = entropySource;
        }

        internal virtual BcTlsSecret AdoptLocalSecret(byte[] data)
        {
            return new BcTlsSecret(this, data);
        }

        public override SecureRandom SecureRandom
        {
            get { return m_entropySource; }
        }

        public override TlsCertificate CreateCertificate(short type, byte[] encoding)
        {
            switch (type)
            {
            case CertificateType.X509:
                return new BcTlsCertificate(this, encoding);
            case CertificateType.RawPublicKey:
                return new BcTlsRawKeyCertificate(this, encoding);
            default:
                throw new TlsFatalAlert(AlertDescription.internal_error);
            }
        }

        public override TlsCipher CreateCipher(TlsCryptoParameters cryptoParams, int encryptionAlgorithm,
            int macAlgorithm)
        {
            switch (encryptionAlgorithm)
            {
            case EncryptionAlgorithm.AES_128_GCM:
                return CreateCipher_Aes_Gcm(cryptoParams, 16, 16);
            case EncryptionAlgorithm.AES_256_GCM:
                return CreateCipher_Aes_Gcm(cryptoParams, 32, 16);
            case EncryptionAlgorithm.CHACHA20_POLY1305:
                return CreateChaCha20Poly1305(cryptoParams);
            default:
                throw new TlsFatalAlert(AlertDescription.internal_error);
            }
        }

        public override TlsDHDomain CreateDHDomain(TlsDHConfig dhConfig)
        {
            throw new NotSupportedException("Finite-field DH is not supported in this stripped build.");
        }

        public override TlsECDomain CreateECDomain(TlsECConfig ecConfig)
        {
            switch (ecConfig.NamedGroup)
            {
            case NamedGroup.x25519:
                return new BcX25519Domain(this);
            default:
                return new BcTlsECDomain(this, ecConfig);
            }
        }

        public override TlsKemDomain CreateKemDomain(TlsKemConfig kemConfig)
        {
            throw new NotSupportedException("KEM is not supported in this stripped build.");
        }

        public override TlsNonceGenerator CreateNonceGenerator(byte[] additionalSeedMaterial)
        {
            // TODO[api] Require non-null additionalSeedMaterial
            //if (additionalSeedMaterial == null)
            //    throw new ArgumentNullException(nameof(additionalSeedMaterial));

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            var seed = additionalSeedMaterial == null ? Span<byte>.Empty : additionalSeedMaterial.AsSpan();

            return CreateNonceGenerator(seed);
#else
            int cryptoHashAlgorithm = CryptoHashAlgorithm.sha256;
            IDigest digest = CreateDigest(cryptoHashAlgorithm);

            int seedLength = 2 * TlsCryptoUtilities.GetHashOutputSize(cryptoHashAlgorithm);
            byte[] seed = new byte[seedLength];
            SecureRandom.NextBytes(seed);

            DigestRandomGenerator randomGenerator = new DigestRandomGenerator(digest);
            randomGenerator.AddSeedMaterial(additionalSeedMaterial);
            randomGenerator.AddSeedMaterial(seed);

            return new BcTlsNonceGenerator(randomGenerator);
#endif
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        public override TlsNonceGenerator CreateNonceGenerator(ReadOnlySpan<byte> additionalSeedMaterial)
        {
            int cryptoHashAlgorithm = CryptoHashAlgorithm.sha256;
            IDigest digest = CreateDigest(cryptoHashAlgorithm);

            int seedLength = 2 * TlsCryptoUtilities.GetHashOutputSize(cryptoHashAlgorithm);
            Span<byte> seed = seedLength <= 128
                ? stackalloc byte[seedLength]
                : new byte[seedLength];
            SecureRandom.NextBytes(seed);

            DigestRandomGenerator randomGenerator = new DigestRandomGenerator(digest);
            randomGenerator.AddSeedMaterial(additionalSeedMaterial);
            randomGenerator.AddSeedMaterial(seed);

            return new BcTlsNonceGenerator(randomGenerator);
        }
#endif

        public override bool HasAnyStreamVerifiers(IList<SignatureAndHashAlgorithm> signatureAndHashAlgorithms)
        {
            foreach (SignatureAndHashAlgorithm algorithm in signatureAndHashAlgorithms)
            {
                switch (SignatureScheme.From(algorithm))
                {
                case SignatureScheme.ed25519:
                    return true;
                }
            }
            return false;
        }

        public override bool HasAnyStreamVerifiersLegacy(short[] clientCertificateTypes)
        {
            return false;
        }

        public override bool HasCryptoHashAlgorithm(int cryptoHashAlgorithm)
        {
            switch (cryptoHashAlgorithm)
            {
            case CryptoHashAlgorithm.sha256:
            case CryptoHashAlgorithm.sha384:
            case CryptoHashAlgorithm.sha512:
                return true;

            default:
                return false;
            }
        }

        public override bool HasCryptoSignatureAlgorithm(int cryptoSignatureAlgorithm)
        {
            switch (cryptoSignatureAlgorithm)
            {
            case CryptoSignatureAlgorithm.rsa:
            case CryptoSignatureAlgorithm.ecdsa:
            case CryptoSignatureAlgorithm.rsa_pss_rsae_sha256:
            case CryptoSignatureAlgorithm.rsa_pss_rsae_sha384:
            case CryptoSignatureAlgorithm.rsa_pss_rsae_sha512:
            case CryptoSignatureAlgorithm.ed25519:
            case CryptoSignatureAlgorithm.rsa_pss_pss_sha256:
            case CryptoSignatureAlgorithm.rsa_pss_pss_sha384:
            case CryptoSignatureAlgorithm.rsa_pss_pss_sha512:
                return true;

            // TODO[RFC 8998]
            case CryptoSignatureAlgorithm.sm2:

            default:
                return false;
            }
        }

        public override bool HasDHAgreement()
        {
            return false;
        }

        public override bool HasECDHAgreement()
        {
            return true;
        }

        public override bool HasEncryptionAlgorithm(int encryptionAlgorithm)
        {
            switch (encryptionAlgorithm)
            {
            case EncryptionAlgorithm.AES_128_GCM:
            case EncryptionAlgorithm.AES_256_GCM:
            case EncryptionAlgorithm.CHACHA20_POLY1305:
                return true;

            default:
                return false;
            }
        }

        public override bool HasHkdfAlgorithm(int cryptoHashAlgorithm)
        {
            switch (cryptoHashAlgorithm)
            {
            case CryptoHashAlgorithm.sha256:
            case CryptoHashAlgorithm.sha384:
                return true;

            default:
                return false;
            }
        }

        public override bool HasKemAgreement()
        {
            return false;
        }

        public override bool HasMacAlgorithm(int macAlgorithm)
        {
            switch (macAlgorithm)
            {
            case MacAlgorithm.hmac_sha256:
            case MacAlgorithm.hmac_sha384:
                return true;

            default:
                return false;
            }
        }

        public override bool HasNamedGroup(int namedGroup)
        {
            switch (namedGroup)
            {
            case NamedGroup.x25519:
            case NamedGroup.secp256r1:
            case NamedGroup.secp384r1:
                return true;
            default:
                return false;
            }
        }

        public override bool HasRsaEncryption()
        {
            return true;
        }

        public override bool HasSignatureAlgorithm(short signatureAlgorithm)
        {
            switch (signatureAlgorithm)
            {
            case SignatureAlgorithm.rsa:
            case SignatureAlgorithm.ecdsa:
            case SignatureAlgorithm.ed25519:
            case SignatureAlgorithm.rsa_pss_rsae_sha256:
            case SignatureAlgorithm.rsa_pss_rsae_sha384:
            case SignatureAlgorithm.rsa_pss_rsae_sha512:
            case SignatureAlgorithm.rsa_pss_pss_sha256:
            case SignatureAlgorithm.rsa_pss_pss_sha384:
            case SignatureAlgorithm.rsa_pss_pss_sha512:
                return true;

            // TODO[RFC 8998]
            //case SignatureAlgorithm.sm2:

            default:
                return false;
            }
        }

        public override bool HasSignatureAndHashAlgorithm(SignatureAndHashAlgorithm sigAndHashAlgorithm)
        {
            short signature = sigAndHashAlgorithm.Signature;

            switch (sigAndHashAlgorithm.Hash)
            {
            case HashAlgorithm.Intrinsic:
            case HashAlgorithm.sha256:
            case HashAlgorithm.sha384:
            case HashAlgorithm.sha512:
                return HasSignatureAlgorithm(signature);
            default:
                return false;
            }
        }

        public override bool HasSignatureScheme(int signatureScheme)
        {
            switch (signatureScheme)
            {
            case SignatureScheme.sm2sig_sm3:
                return false;
            case SignatureScheme.mldsa44:
            case SignatureScheme.mldsa65:
            case SignatureScheme.mldsa87:
            case SignatureScheme.slhdsa_sha2_128s:
            case SignatureScheme.slhdsa_sha2_128f:
            case SignatureScheme.slhdsa_sha2_192s:
            case SignatureScheme.slhdsa_sha2_192f:
            case SignatureScheme.slhdsa_sha2_256s:
            case SignatureScheme.slhdsa_sha2_256f:
            case SignatureScheme.slhdsa_shake_128s:
            case SignatureScheme.slhdsa_shake_128f:
            case SignatureScheme.slhdsa_shake_192s:
            case SignatureScheme.slhdsa_shake_192f:
            case SignatureScheme.slhdsa_shake_256s:
            case SignatureScheme.slhdsa_shake_256f:
                return false;
            default:
            {
                short signature = SignatureScheme.GetSignatureAlgorithm(signatureScheme);

                switch (SignatureScheme.GetCryptoHashAlgorithm(signatureScheme))
                {
                case CryptoHashAlgorithm.md5:
                    return SignatureAlgorithm.rsa == signature && HasSignatureAlgorithm(signature);
                default:
                    return HasSignatureAlgorithm(signature);
                }
            }
            }
        }

        public override bool HasSrpAuthentication()
        {
            return false;
        }

        public override TlsSecret CreateHybridSecret(TlsSecret s1, TlsSecret s2)
        {
            return AdoptLocalSecret(Arrays.Concatenate(s1.Extract(), s2.Extract()));
        }

        public override TlsSecret CreateSecret(byte[] data)
        {
            try
            {
                return AdoptLocalSecret(Arrays.Clone(data));
            }
            finally
            {
                // TODO[tls-ops] Add this after checking all callers
                //if (data != null)
                //{
                //    Array.Clear(data, 0, data.Length);
                //}
            }
        }

        public override TlsSecret GenerateRsaPreMasterSecret(ProtocolVersion version)
        {
            byte[] data = new byte[48];
            SecureRandom.NextBytes(data);
            TlsUtilities.WriteVersion(version, data, 0);
            return AdoptLocalSecret(data);
        }

        public virtual IDigest CloneDigest(int cryptoHashAlgorithm, IDigest digest)
        {
            switch (cryptoHashAlgorithm)
            {
            case CryptoHashAlgorithm.sha256:
                return new Sha256Digest((Sha256Digest)digest);
            case CryptoHashAlgorithm.sha384:
                return new Sha384Digest((Sha384Digest)digest);
            case CryptoHashAlgorithm.sha512:
                return new Sha512Digest((Sha512Digest)digest);
            default:
                throw new ArgumentException("invalid CryptoHashAlgorithm: " + cryptoHashAlgorithm);
            }
        }

        public virtual IDigest CreateDigest(int cryptoHashAlgorithm)
        {
            switch (cryptoHashAlgorithm)
            {
            case CryptoHashAlgorithm.sha256:
                return new Sha256Digest();
            case CryptoHashAlgorithm.sha384:
                return new Sha384Digest();
            case CryptoHashAlgorithm.sha512:
                return new Sha512Digest();
            default:
                throw new ArgumentException("invalid CryptoHashAlgorithm: " + cryptoHashAlgorithm);
            }
        }

        public override TlsHash CreateHash(int cryptoHashAlgorithm)
        {
            return new BcTlsHash(this, cryptoHashAlgorithm);
        }

        protected virtual TlsCipher CreateChaCha20Poly1305(TlsCryptoParameters cryptoParams)
        {
            BcChaCha20Poly1305 encrypt = new BcChaCha20Poly1305(true);
            BcChaCha20Poly1305 decrypt = new BcChaCha20Poly1305(false);

            return new TlsAeadCipher(cryptoParams, encrypt, decrypt, 32, 16, TlsAeadCipher.AEAD_CHACHA20_POLY1305);
        }

        protected virtual TlsAeadCipher CreateCipher_Aes_Gcm(TlsCryptoParameters cryptoParams, int cipherKeySize,
            int macSize)
        {
            BcTlsAeadCipherImpl encrypt = new BcTlsAeadCipherImpl(CreateAeadCipher_Aes_Gcm(), true);
            BcTlsAeadCipherImpl decrypt = new BcTlsAeadCipherImpl(CreateAeadCipher_Aes_Gcm(), false);

            return new TlsAeadCipher(cryptoParams, encrypt, decrypt, cipherKeySize, macSize, TlsAeadCipher.AEAD_GCM);
        }

        protected virtual IBlockCipher CreateAesEngine()
        {
            return AesUtilities.CreateEngine();
        }

        protected virtual IAeadCipher CreateGcmMode(IBlockCipher engine)
        {
            return new GcmBlockCipher(engine);
        }

        protected virtual IAeadCipher CreateAeadCipher_Aes_Gcm()
        {
            return CreateGcmMode(CreateAesEngine());
        }

        public override TlsHmac CreateHmac(int macAlgorithm)
        {
            switch (macAlgorithm)
            {
            case MacAlgorithm.hmac_sha256:
            case MacAlgorithm.hmac_sha384:
                return CreateHmacForHash(TlsCryptoUtilities.GetHashForHmac(macAlgorithm));

            default:
                throw new ArgumentException("invalid MacAlgorithm: " + macAlgorithm);
            }
        }

        public override TlsHmac CreateHmacForHash(int cryptoHashAlgorithm)
        {
            return new BcTlsHmac(new HMac(CreateDigest(cryptoHashAlgorithm)));
        }

        public override TlsSrp6Client CreateSrp6Client(TlsSrpConfig srpConfig)
        {
            throw new NotSupportedException("SRP is not supported in this stripped build.");
        }

        public override TlsSrp6Server CreateSrp6Server(TlsSrpConfig srpConfig, BigInteger srpVerifier)
        {
            throw new NotSupportedException("SRP is not supported in this stripped build.");
        }

        public override TlsSrp6VerifierGenerator CreateSrp6VerifierGenerator(TlsSrpConfig srpConfig)
        {
            throw new NotSupportedException("SRP is not supported in this stripped build.");
        }

        public override TlsSecret HkdfInit(int cryptoHashAlgorithm)
        {
            return AdoptLocalSecret(new byte[TlsCryptoUtilities.GetHashOutputSize(cryptoHashAlgorithm)]);
        }
    }
}
