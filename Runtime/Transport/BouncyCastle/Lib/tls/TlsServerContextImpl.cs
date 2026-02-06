using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls
{
    internal class TlsServerContextImpl
        : AbstractTlsContext, TlsServerContext
    {
        internal TlsServerContextImpl(TlsCrypto crypto)
            : base(crypto, ConnectionEnd.server)
        {
        }

        public override bool IsServer
        {
            get { return true; }
        }
    }
}
