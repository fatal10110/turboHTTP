using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls
{
    public class DefaultTlsKeyExchangeFactory
        : AbstractTlsKeyExchangeFactory
    {
        public override TlsKeyExchange CreateDHKeyExchange(int keyExchange)
        {
            return new TlsDHKeyExchange(keyExchange);
        }

        public override TlsKeyExchange CreateDHanonKeyExchangeClient(int keyExchange,
            TlsDHGroupVerifier dhGroupVerifier)
        {
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateDHanonKeyExchangeServer(int keyExchange, TlsDHConfig dhConfig)
        {
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateDheKeyExchangeClient(int keyExchange, TlsDHGroupVerifier dhGroupVerifier)
        {
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateDheKeyExchangeServer(int keyExchange, TlsDHConfig dhConfig)
        {
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateECDHKeyExchange(int keyExchange)
        {
            return new TlsECDHKeyExchange(keyExchange);
        }

        public override TlsKeyExchange CreateECDHanonKeyExchangeClient(int keyExchange)
        {
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateECDHanonKeyExchangeServer(int keyExchange, TlsECConfig ecConfig)
        {
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateECDheKeyExchangeClient(int keyExchange)
        {
            return new TlsECDheKeyExchange(keyExchange);
        }

        public override TlsKeyExchange CreateECDheKeyExchangeServer(int keyExchange, TlsECConfig ecConfig)
        {
            return new TlsECDheKeyExchange(keyExchange, ecConfig);
        }

        public override TlsKeyExchange CreatePskKeyExchangeClient(int keyExchange, TlsPskIdentity pskIdentity,
            TlsDHGroupVerifier dhGroupVerifier)
        {
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreatePskKeyExchangeServer(int keyExchange,
            TlsPskIdentityManager pskIdentityManager, TlsDHConfig dhConfig, TlsECConfig ecConfig)
        {
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateRsaKeyExchange(int keyExchange)
        {
            return new TlsRsaKeyExchange(keyExchange);
        }

        public override TlsKeyExchange CreateSrpKeyExchangeClient(int keyExchange, TlsSrpIdentity srpIdentity,
            TlsSrpConfigVerifier srpConfigVerifier)
        {
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateSrpKeyExchangeServer(int keyExchange, TlsSrpLoginParameters loginParameters)
        {
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }
    }
}
