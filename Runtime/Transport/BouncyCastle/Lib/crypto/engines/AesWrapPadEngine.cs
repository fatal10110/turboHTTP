using System;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Engines
{
    public class AesWrapPadEngine
        : Rfc5649WrapEngine
    {
        public AesWrapPadEngine()
            : base(AesUtilities.CreateEngine())
        {
        }
    }
}
