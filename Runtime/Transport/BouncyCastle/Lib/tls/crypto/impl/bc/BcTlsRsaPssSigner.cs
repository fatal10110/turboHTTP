using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Engines;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Parameters;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Signers;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto.Impl.BC
{
    /// <summary>Operator supporting the generation of RSASSA-PSS signatures using the BC light-weight API.</summary>
    public class BcTlsRsaPssSigner
        : BcTlsSigner
    {
        private readonly int m_signatureScheme;

        public BcTlsRsaPssSigner(BcTlsCrypto crypto, RsaKeyParameters privateKey, int signatureScheme)
            : base(crypto, privateKey)
        {
            if (!SignatureScheme.IsRsaPss(signatureScheme))
                throw new ArgumentException("signatureScheme");

            this.m_signatureScheme = signatureScheme;
        }

        public override byte[] GenerateRawSignature(SignatureAndHashAlgorithm algorithm, byte[] hash)
        {
            if (algorithm == null || SignatureScheme.From(algorithm) != m_signatureScheme)
                throw new InvalidOperationException("Invalid algorithm: " + algorithm);

            int cryptoHashAlgorithm = SignatureScheme.GetCryptoHashAlgorithm(m_signatureScheme);
            IDigest digest = m_crypto.CreateDigest(cryptoHashAlgorithm);

            PssSigner signer = PssSigner.CreateRawSigner(new RsaBlindedEngine(), digest);
            signer.Init(true, new ParametersWithRandom(m_privateKey, m_crypto.SecureRandom));
            signer.BlockUpdate(hash, 0, hash.Length);
            try
            {
                return signer.GenerateSignature();
            }
            catch (CryptoException e)
            {
                throw new TlsFatalAlert(AlertDescription.internal_error, e);
            }
        }
    }
}
