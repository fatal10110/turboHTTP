using System;
using System.IO;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.IO;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto.Impl.BC
{
    internal sealed class BcTls13Verifier
        : Tls13Verifier
    {
        private readonly SignerSink m_output;

        internal BcTls13Verifier(ISigner verifier)
        {
            if (verifier == null)
                throw new ArgumentNullException("verifier");

            this.m_output = new SignerSink(verifier);
        }

        public Stream Stream
        {
            get { return m_output; }
        }

        public bool VerifySignature(byte[] signature)
        {
            return m_output.Signer.VerifySignature(signature);
        }
    }
}
