using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls
{
    public interface TlsPsk
    {
        byte[] Identity { get; }

        TlsSecret Key { get; }

        int PrfAlgorithm { get; }
    }
}
