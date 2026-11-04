using System;
using System.Collections.Generic;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Agreement.Srp;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Digests;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Engines;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Macs;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Modes;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Parameters;
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
            case EncryptionAlgorithm.AES_128_CBC:
            case EncryptionAlgorithm.ARIA_128_CBC:
            case EncryptionAlgorithm.CAMELLIA_128_CBC:
            case EncryptionAlgorithm.SEED_CBC:
            case EncryptionAlgorithm.SM4_CBC:
                return CreateCipher_Cbc(cryptoParams, encryptionAlgorithm, 16, macAlgorithm);

            case EncryptionAlgorithm.cls_3DES_EDE_CBC:
                return CreateCipher_Cbc(cryptoParams, encryptionAlgorithm, 24, macAlgorithm);

            case EncryptionAlgorithm.AES_256_CBC:
            case EncryptionAlgorithm.ARIA_256_CBC:
            case EncryptionAlgorithm.CAMELLIA_256_CBC:
                return CreateCipher_Cbc(cryptoParams, encryptionAlgorithm, 32, macAlgorithm);

            case EncryptionAlgorithm.AES_128_CCM:
                // NOTE: Ignores macAlgorithm
                return CreateCipher_Aes_Ccm(cryptoParams, 16, 16);
            case EncryptionAlgorithm.AES_128_CCM_8:
                // NOTE: Ignores macAlgorithm
                return CreateCipher_Aes_Ccm(cryptoParams, 16, 8);
            case EncryptionAlgorithm.AES_128_GCM:
                // NOTE: Ignores macAlgorithm
                return CreateCipher_Aes_Gcm(cryptoParams, 16, 16);
            case EncryptionAlgorithm.AES_256_CCM:
                // NOTE: Ignores macAlgorithm
                return CreateCipher_Aes_Ccm(cryptoParams, 32, 16);
            case EncryptionAlgorithm.AES_256_CCM_8:
                // NOTE: Ignores macAlgorithm
                return CreateCipher_Aes_Ccm(cryptoParams, 32, 8);
            case EncryptionAlgorithm.AES_256_GCM:
                // NOTE: Ignores macAlgorithm
                return CreateCipher_Aes_Gcm(cryptoParams, 32, 16);
            case EncryptionAlgorithm.ARIA_128_GCM:
                // NOTE: Ignores macAlgorithm
                return CreateCipher_Aria_Gcm(cryptoParams, 16, 16);
            case EncryptionAlgorithm.ARIA_256_GCM:
                // NOTE: Ignores macAlgorithm
                return CreateCipher_Aria_Gcm(cryptoParams, 32, 16);
            case EncryptionAlgorithm.CAMELLIA_128_GCM:
                // NOTE: Ignores macAlgorithm
                return CreateCipher_Camellia_Gcm(cryptoParams, 16, 16);
            case EncryptionAlgorithm.CAMELLIA_256_GCM:
                // NOTE: Ignores macAlgorithm
                return CreateCipher_Camellia_Gcm(cryptoParams, 32, 16);
            case EncryptionAlgorithm.CHACHA20_POLY1305:
                // NOTE: Ignores macAlgorithm
                return CreateChaCha20Poly1305(cryptoParams);
            case EncryptionAlgorithm.NULL:
                return CreateNullCipher(cryptoParams, macAlgorithm);
            case EncryptionAlgorithm.NULL_HMAC_SHA256:
                // NOTE: Ignores macAlgorithm
                return Create13NullCipher(cryptoParams, MacAlgorithm.hmac_sha256);
            case EncryptionAlgorithm.NULL_HMAC_SHA384:
                // NOTE: Ignores macAlgorithm
                return Create13NullCipher(cryptoParams, MacAlgorithm.hmac_sha384);
            case EncryptionAlgorithm.SM4_CCM:
                // NOTE: Ignores macAlgorithm
                return CreateCipher_SM4_Ccm(cryptoParams);
            case EncryptionAlgorithm.SM4_GCM:
                // NOTE: Ignores macAlgorithm
                return CreateCipher_SM4_Gcm(cryptoParams);

            case EncryptionAlgorithm.cls_28147_CNT_IMIT:
            case EncryptionAlgorithm.DES40_CBC:
            case EncryptionAlgorithm.DES_CBC:
            case EncryptionAlgorithm.IDEA_CBC:
            case EncryptionAlgorithm.KUZNYECHIK_CTR_OMAC:
            case EncryptionAlgorithm.MAGMA_CTR_OMAC:
            case EncryptionAlgorithm.RC2_CBC_40:
            case EncryptionAlgorithm.RC4_128:
            case EncryptionAlgorithm.RC4_40:
            default:
                throw new TlsFatalAlert(AlertDescription.internal_error);
            }
        }

        public override TlsDHDomain CreateDHDomain(TlsDHConfig dhConfig)
        {
            // turbo-strip: E1 no finite-field DH (HasDHAgreement is false)
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsECDomain CreateECDomain(TlsECConfig ecConfig)
        {
            switch (ecConfig.NamedGroup)
            {
            case NamedGroup.x25519:
                return new BcX25519Domain(this);
            // turbo-strip: E1 x448 removed (HasNamedGroup is false for it)
            default:
                return new BcTlsECDomain(this, ecConfig);
            }
        }

        public override TlsKemDomain CreateKemDomain(TlsKemConfig kemConfig)
        {
            // turbo-strip: E1 no KEM groups (decision C; HasKemAgreement is false)
            throw new TlsFatalAlert(AlertDescription.internal_error);
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
                case SignatureScheme.ed448:
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
            case CryptoHashAlgorithm.md5:
            case CryptoHashAlgorithm.sha1:
            case CryptoHashAlgorithm.sha224:
            case CryptoHashAlgorithm.sha256:
            case CryptoHashAlgorithm.sha384:
            case CryptoHashAlgorithm.sha512:
            case CryptoHashAlgorithm.sm3:
            // turbo-strip: E1 gostr3411_2012_256 removed
                return true;

            default:
                return false;
            }
        }

        public override bool HasCryptoSignatureAlgorithm(int cryptoSignatureAlgorithm)
        {
            switch (cryptoSignatureAlgorithm)
            {
            // turbo-strip: E1 rsa, ecdsa and rsa_pss_* only (dsa, ed25519, ed448, sm2 removed)
            case CryptoSignatureAlgorithm.rsa:
            case CryptoSignatureAlgorithm.ecdsa:
            case CryptoSignatureAlgorithm.rsa_pss_rsae_sha256:
            case CryptoSignatureAlgorithm.rsa_pss_rsae_sha384:
            case CryptoSignatureAlgorithm.rsa_pss_rsae_sha512:
            case CryptoSignatureAlgorithm.rsa_pss_pss_sha256:
            case CryptoSignatureAlgorithm.rsa_pss_pss_sha384:
            case CryptoSignatureAlgorithm.rsa_pss_pss_sha512:
                return true;

            // TODO[RFC 9189]
            case CryptoSignatureAlgorithm.gostr34102012_256:
            case CryptoSignatureAlgorithm.gostr34102012_512:

            default:
                return false;
            }
        }

        public override bool HasDHAgreement()
        {
            // turbo-strip: E1 no finite-field DH
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
            // turbo-strip: E1 AEADs AES-GCM and ChaCha20-Poly1305 only (decision B: no CBC; no CCM, ARIA,
            // CAMELLIA, 3DES, NULL, NULL_HMAC_*, SEED, SM4)
            case EncryptionAlgorithm.AES_128_GCM:
            case EncryptionAlgorithm.AES_256_GCM:
            case EncryptionAlgorithm.CHACHA20_POLY1305:
                return true;

            case EncryptionAlgorithm.cls_28147_CNT_IMIT:
            case EncryptionAlgorithm.DES_CBC:
            case EncryptionAlgorithm.DES40_CBC:
            case EncryptionAlgorithm.IDEA_CBC:
            case EncryptionAlgorithm.KUZNYECHIK_CTR_OMAC:
            case EncryptionAlgorithm.MAGMA_CTR_OMAC:
            case EncryptionAlgorithm.RC2_CBC_40:
            case EncryptionAlgorithm.RC4_128:
            case EncryptionAlgorithm.RC4_40:
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
            case CryptoHashAlgorithm.sha512:
            case CryptoHashAlgorithm.sm3:
                return true;

            default:
                return false;
            }
        }

        public override bool HasKemAgreement()
        {
            // turbo-strip: E1 no KEM groups (decision C)
            return false;
        }

        public override bool HasMacAlgorithm(int macAlgorithm)
        {
            switch (macAlgorithm)
            {
            case MacAlgorithm.hmac_md5:
            case MacAlgorithm.hmac_sha1:
            case MacAlgorithm.hmac_sha256:
            case MacAlgorithm.hmac_sha384:
            case MacAlgorithm.hmac_sha512:
                return true;

            default:
                return false;
            }
        }

        public override bool HasNamedGroup(int namedGroup)
        {
            // turbo-strip: E1 x25519, secp256r1, secp384r1, secp521r1 only (no ffdhe, no KEM/hybrids: decision C)
            switch (namedGroup)
            {
            case NamedGroup.x25519:
            case NamedGroup.secp256r1:
            case NamedGroup.secp384r1:
            case NamedGroup.secp521r1:
                return true;

            default:
                return false;
            }
        }

        public override bool HasRsaEncryption()
        {
            // turbo-strip: E1 no RSA key exchange
            return false;
        }

        public override bool HasSignatureAlgorithm(short signatureAlgorithm)
        {
            switch (signatureAlgorithm)
            {
            // turbo-strip: E1 rsa, ecdsa and rsa_pss_* only (dsa, ed25519/ed448 (decision E), brainpool tls13 removed)
            case SignatureAlgorithm.rsa:
            case SignatureAlgorithm.ecdsa:
            case SignatureAlgorithm.rsa_pss_rsae_sha256:
            case SignatureAlgorithm.rsa_pss_rsae_sha384:
            case SignatureAlgorithm.rsa_pss_rsae_sha512:
            case SignatureAlgorithm.rsa_pss_pss_sha256:
            case SignatureAlgorithm.rsa_pss_pss_sha384:
            case SignatureAlgorithm.rsa_pss_pss_sha512:
                return true;

            // TODO[RFC 9189]
            case SignatureAlgorithm.gostr34102012_256:
            case SignatureAlgorithm.gostr34102012_512:

            default:
                return false;
            }
        }

        public override bool HasSignatureAndHashAlgorithm(SignatureAndHashAlgorithm sigAndHashAlgorithm)
        {
            // turbo-strip: E1 hash whitelist {Intrinsic, SHA-256/384/512}, before the ML-DSA/SLH-DSA early return
            switch (sigAndHashAlgorithm.Hash)
            {
            case HashAlgorithm.Intrinsic:
            case HashAlgorithm.sha256:
            case HashAlgorithm.sha384:
            case HashAlgorithm.sha512:
                break;

            default:
                return false;
            }

            int signatureScheme = SignatureScheme.From(sigAndHashAlgorithm);
            if (SignatureScheme.IsMLDsaScheme(signatureScheme) ||
                SignatureScheme.IsSlhDsa(signatureScheme))
            {
                return HasSignatureScheme(signatureScheme);
            }

            short signature = sigAndHashAlgorithm.Signature;

            switch (sigAndHashAlgorithm.Hash)
            {
            case HashAlgorithm.md5:
                return SignatureAlgorithm.rsa == signature && HasSignatureAlgorithm(signature);
            default:
                return HasSignatureAlgorithm(signature);
            }
        }

        public override bool HasSignatureScheme(int signatureScheme)
        {
            switch (signatureScheme)
            {
            // turbo-strip: E1 ML-DSA, SLH-DSA and sm2sig_sm3 unsupported
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
            case SignatureScheme.sm2sig_sm3:
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
            // turbo-strip: E1 no SRP (CreateSrp6* throw)
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
            case CryptoHashAlgorithm.md5:
                return new MD5Digest((MD5Digest)digest);
            case CryptoHashAlgorithm.sha1:
                return new Sha1Digest((Sha1Digest)digest);
            case CryptoHashAlgorithm.sha224:
                return new Sha224Digest((Sha224Digest)digest);
            case CryptoHashAlgorithm.sha256:
                return new Sha256Digest((Sha256Digest)digest);
            case CryptoHashAlgorithm.sha384:
                return new Sha384Digest((Sha384Digest)digest);
            case CryptoHashAlgorithm.sha512:
                return new Sha512Digest((Sha512Digest)digest);
            case CryptoHashAlgorithm.sm3:
                return new SM3Digest((SM3Digest)digest);
            // turbo-strip: E1 Gost3411_2012_256Digest removed
            default:
                throw new ArgumentException("invalid CryptoHashAlgorithm: " + cryptoHashAlgorithm);
            }
        }

        public virtual IDigest CreateDigest(int cryptoHashAlgorithm)
        {
            switch (cryptoHashAlgorithm)
            {
            case CryptoHashAlgorithm.md5:
                return new MD5Digest();
            case CryptoHashAlgorithm.sha1:
                return new Sha1Digest();
            case CryptoHashAlgorithm.sha224:
                return new Sha224Digest();
            case CryptoHashAlgorithm.sha256:
                return new Sha256Digest();
            case CryptoHashAlgorithm.sha384:
                return new Sha384Digest();
            case CryptoHashAlgorithm.sha512:
                return new Sha512Digest();
            case CryptoHashAlgorithm.sm3:
                return new SM3Digest();
            // turbo-strip: E1 Gost3411_2012_256Digest removed
            default:
                throw new ArgumentException("invalid CryptoHashAlgorithm: " + cryptoHashAlgorithm);
            }
        }

        public override TlsHash CreateHash(int cryptoHashAlgorithm)
        {
            return new BcTlsHash(this, cryptoHashAlgorithm);
        }

        protected virtual IBlockCipher CreateBlockCipher(int encryptionAlgorithm)
        {
            switch (encryptionAlgorithm)
            {
            case EncryptionAlgorithm.cls_3DES_EDE_CBC:
                return CreateDesEdeEngine();
            case EncryptionAlgorithm.AES_128_CBC:
            case EncryptionAlgorithm.AES_256_CBC:
                return CreateAesEngine();
            case EncryptionAlgorithm.ARIA_128_CBC:
            case EncryptionAlgorithm.ARIA_256_CBC:
                return CreateAriaEngine();
            case EncryptionAlgorithm.CAMELLIA_128_CBC:
            case EncryptionAlgorithm.CAMELLIA_256_CBC:
                return CreateCamelliaEngine();
            case EncryptionAlgorithm.SEED_CBC:
                return CreateSeedEngine();
            case EncryptionAlgorithm.SM4_CBC:
                return CreateSM4Engine();
            default:
                throw new TlsFatalAlert(AlertDescription.internal_error);
            }
        }

        protected virtual IBlockCipher CreateCbcBlockCipher(IBlockCipher blockCipher)
        {
            return new CbcBlockCipher(blockCipher);
        }

        protected virtual IBlockCipher CreateCbcBlockCipher(int encryptionAlgorithm)
        {
            return CreateCbcBlockCipher(CreateBlockCipher(encryptionAlgorithm));
        }

        protected virtual TlsCipher CreateChaCha20Poly1305(TlsCryptoParameters cryptoParams)
        {
            BcChaCha20Poly1305 encrypt = new BcChaCha20Poly1305(true);
            BcChaCha20Poly1305 decrypt = new BcChaCha20Poly1305(false);

            return new TlsAeadCipher(cryptoParams, encrypt, decrypt, 32, 16, TlsAeadCipher.AEAD_CHACHA20_POLY1305);
        }

        protected virtual TlsAeadCipher CreateCipher_Aes_Ccm(TlsCryptoParameters cryptoParams, int cipherKeySize,
            int macSize)
        {
            var encrypt = new BcTlsCcmImpl(CreateAeadCipher_Aes_Ccm(), true);
            var decrypt = new BcTlsCcmImpl(CreateAeadCipher_Aes_Ccm(), false);

            return new TlsAeadCipher(cryptoParams, encrypt, decrypt, cipherKeySize, macSize, TlsAeadCipher.AEAD_CCM);
        }

        protected virtual TlsAeadCipher CreateCipher_Aes_Gcm(TlsCryptoParameters cryptoParams, int cipherKeySize,
            int macSize)
        {
            BcTlsAeadCipherImpl encrypt = new BcTlsAeadCipherImpl(CreateAeadCipher_Aes_Gcm(), true);
            BcTlsAeadCipherImpl decrypt = new BcTlsAeadCipherImpl(CreateAeadCipher_Aes_Gcm(), false);

            return new TlsAeadCipher(cryptoParams, encrypt, decrypt, cipherKeySize, macSize, TlsAeadCipher.AEAD_GCM);
        }

        protected virtual TlsAeadCipher CreateCipher_Aria_Gcm(TlsCryptoParameters cryptoParams, int cipherKeySize,
            int macSize)
        {
            BcTlsAeadCipherImpl encrypt = new BcTlsAeadCipherImpl(CreateAeadCipher_Aria_Gcm(), true);
            BcTlsAeadCipherImpl decrypt = new BcTlsAeadCipherImpl(CreateAeadCipher_Aria_Gcm(), false);

            return new TlsAeadCipher(cryptoParams, encrypt, decrypt, cipherKeySize, macSize, TlsAeadCipher.AEAD_GCM);
        }

        protected virtual TlsAeadCipher CreateCipher_Camellia_Gcm(TlsCryptoParameters cryptoParams, int cipherKeySize,
            int macSize)
        {
            BcTlsAeadCipherImpl encrypt = new BcTlsAeadCipherImpl(CreateAeadCipher_Camellia_Gcm(), true);
            BcTlsAeadCipherImpl decrypt = new BcTlsAeadCipherImpl(CreateAeadCipher_Camellia_Gcm(), false);

            return new TlsAeadCipher(cryptoParams, encrypt, decrypt, cipherKeySize, macSize, TlsAeadCipher.AEAD_GCM);
        }

        protected virtual TlsCipher CreateCipher_Cbc(TlsCryptoParameters cryptoParams, int encryptionAlgorithm,
            int cipherKeySize, int macAlgorithm)
        {
            BcTlsBlockCipherImpl encrypt = new BcTlsBlockCipherImpl(CreateCbcBlockCipher(encryptionAlgorithm), true);
            BcTlsBlockCipherImpl decrypt = new BcTlsBlockCipherImpl(CreateCbcBlockCipher(encryptionAlgorithm), false);

            TlsHmac clientMac = CreateMac(cryptoParams, macAlgorithm);
            TlsHmac serverMac = CreateMac(cryptoParams, macAlgorithm);

            return new TlsBlockCipher(cryptoParams, encrypt, decrypt, clientMac, serverMac, cipherKeySize);
        }

        protected virtual TlsAeadCipher CreateCipher_SM4_Ccm(TlsCryptoParameters cryptoParams)
        {
            var encrypt = new BcTlsCcmImpl(CreateAeadCipher_SM4_Ccm(), true);
            var decrypt = new BcTlsCcmImpl(CreateAeadCipher_SM4_Ccm(), false);

            return new TlsAeadCipher(cryptoParams, encrypt, decrypt, 16, 16, TlsAeadCipher.AEAD_CCM);
        }

        protected virtual TlsAeadCipher CreateCipher_SM4_Gcm(TlsCryptoParameters cryptoParams)
        {
            BcTlsAeadCipherImpl encrypt = new BcTlsAeadCipherImpl(CreateAeadCipher_SM4_Gcm(), true);
            BcTlsAeadCipherImpl decrypt = new BcTlsAeadCipherImpl(CreateAeadCipher_SM4_Gcm(), false);

            return new TlsAeadCipher(cryptoParams, encrypt, decrypt, 16, 16, TlsAeadCipher.AEAD_GCM);
        }

        protected virtual Tls13NullCipher Create13NullCipher(TlsCryptoParameters cryptoParams, int macAlgorithm) =>
            new Tls13NullCipher(cryptoParams, CreateHmac(macAlgorithm), CreateHmac(macAlgorithm));

        protected virtual TlsNullCipher CreateNullCipher(TlsCryptoParameters cryptoParams, int macAlgorithm)
        {
            return new TlsNullCipher(cryptoParams, CreateMac(cryptoParams, macAlgorithm),
                CreateMac(cryptoParams, macAlgorithm));
        }

        protected virtual IBlockCipher CreateAesEngine()
        {
            // turbo-strip: E14 AES-NI engine when available, else the 256-byte S-box AesLightEngine
            return AesUtilities.IsHardwareAccelerated ? AesUtilities.CreateEngine() : new AesLightEngine();
        }

        protected virtual IBlockCipher CreateAriaEngine()
        {
            return new AriaEngine();
        }

        protected virtual IBlockCipher CreateCamelliaEngine()
        {
            return new CamelliaEngine();
        }

        protected virtual IBlockCipher CreateDesEdeEngine()
        {
            return new DesEdeEngine();
        }

        protected virtual IBlockCipher CreateSeedEngine()
        {
            return new SeedEngine();
        }

        protected virtual IBlockCipher CreateSM4Engine()
        {
            return new SM4Engine();
        }

        protected virtual CcmBlockCipher CreateCcmMode(IBlockCipher engine)
        {
            return new CcmBlockCipher(engine);
        }

        protected virtual IAeadCipher CreateGcmMode(IBlockCipher engine)
        {
            return new GcmBlockCipher(engine);
        }

        protected virtual CcmBlockCipher CreateAeadCipher_Aes_Ccm()
        {
            return CreateCcmMode(CreateAesEngine());
        }

        protected virtual IAeadCipher CreateAeadCipher_Aes_Gcm()
        {
            return CreateGcmMode(CreateAesEngine());
        }

        protected virtual IAeadCipher CreateAeadCipher_Aria_Gcm()
        {
            return CreateGcmMode(CreateAriaEngine());
        }

        protected virtual IAeadCipher CreateAeadCipher_Camellia_Gcm()
        {
            return CreateGcmMode(CreateCamelliaEngine());
        }

        protected virtual CcmBlockCipher CreateAeadCipher_SM4_Ccm()
        {
            return CreateCcmMode(CreateSM4Engine());
        }

        protected virtual IAeadCipher CreateAeadCipher_SM4_Gcm()
        {
            return CreateGcmMode(CreateSM4Engine());
        }

        public override TlsHmac CreateHmac(int macAlgorithm)
        {
            switch (macAlgorithm)
            {
            case MacAlgorithm.hmac_md5:
            case MacAlgorithm.hmac_sha1:
            case MacAlgorithm.hmac_sha256:
            case MacAlgorithm.hmac_sha384:
            case MacAlgorithm.hmac_sha512:
                return CreateHmacForHash(TlsCryptoUtilities.GetHashForHmac(macAlgorithm));

            default:
                throw new ArgumentException("invalid MacAlgorithm: " + macAlgorithm);
            }
        }

        public override TlsHmac CreateHmacForHash(int cryptoHashAlgorithm)
        {
            return new BcTlsHmac(new HMac(CreateDigest(cryptoHashAlgorithm)));
        }

        protected virtual TlsHmac CreateHmac_Ssl(int macAlgorithm)
        {
            switch (macAlgorithm)
            {
            case MacAlgorithm.hmac_md5:
                return new BcSsl3Hmac(CreateDigest(CryptoHashAlgorithm.md5));
            case MacAlgorithm.hmac_sha1:
                return new BcSsl3Hmac(CreateDigest(CryptoHashAlgorithm.sha1));
            case MacAlgorithm.hmac_sha256:
                return new BcSsl3Hmac(CreateDigest(CryptoHashAlgorithm.sha256));
            case MacAlgorithm.hmac_sha384:
                return new BcSsl3Hmac(CreateDigest(CryptoHashAlgorithm.sha384));
            case MacAlgorithm.hmac_sha512:
                return new BcSsl3Hmac(CreateDigest(CryptoHashAlgorithm.sha512));
            default:
                throw new TlsFatalAlert(AlertDescription.internal_error);
            }
        }

        protected virtual TlsHmac CreateMac(TlsCryptoParameters cryptoParams, int macAlgorithm)
        {
            if (TlsImplUtilities.IsSsl(cryptoParams))
            {
                return CreateHmac_Ssl(macAlgorithm);
            }
            else
            {
                return CreateHmac(macAlgorithm);
            }
        }

        public override TlsSrp6Client CreateSrp6Client(TlsSrpConfig srpConfig)
        {
            // turbo-strip: E1 no SRP (abstract in AbstractTlsCrypto, so it stays and throws)
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsSrp6Server CreateSrp6Server(TlsSrpConfig srpConfig, BigInteger srpVerifier)
        {
            // turbo-strip: E1 no SRP (abstract in AbstractTlsCrypto, so it stays and throws)
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsSrp6VerifierGenerator CreateSrp6VerifierGenerator(TlsSrpConfig srpConfig)
        {
            // turbo-strip: E1 no SRP (abstract in AbstractTlsCrypto, so it stays and throws)
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsSecret HkdfInit(int cryptoHashAlgorithm)
        {
            return AdoptLocalSecret(new byte[TlsCryptoUtilities.GetHashOutputSize(cryptoHashAlgorithm)]);
        }
    }
}
