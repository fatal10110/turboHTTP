using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Prng.Drbg;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Prng
{
    internal interface IDrbgProvider
    {
        ISP80090Drbg Get(IEntropySource entropySource);
    }
}
