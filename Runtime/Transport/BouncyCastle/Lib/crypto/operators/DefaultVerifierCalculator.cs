using System;
using System.IO;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.IO;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Operators
{
    // TODO[api] sealed
    public class DefaultVerifierCalculator
        : IStreamCalculator<IVerifier>
    {
        private readonly SignerSink m_signerSink;

        public DefaultVerifierCalculator(ISigner signer)
        {
            m_signerSink = new SignerSink(signer);
        }

        public Stream Stream => m_signerSink;

        public IVerifier GetResult() => new DefaultVerifierResult(m_signerSink.Signer);
    }
}
