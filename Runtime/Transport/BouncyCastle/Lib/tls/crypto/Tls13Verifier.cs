using System;
using System.IO;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto
{
    public interface Tls13Verifier
    {
        /// <exception cref="IOException"/>
        Stream Stream { get; }

        /// <exception cref="IOException"/>
        bool VerifySignature(byte[] signature);
    }
}
