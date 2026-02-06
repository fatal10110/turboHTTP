using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Security;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Parameters
{
    public class X448KeyGenerationParameters
        : KeyGenerationParameters
    {
        public X448KeyGenerationParameters(SecureRandom random)
            : base(random, 448)
        {
        }
    }
}
