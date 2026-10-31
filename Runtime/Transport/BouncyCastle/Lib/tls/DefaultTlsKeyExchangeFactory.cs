using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls
{
    public class DefaultTlsKeyExchangeFactory
        : AbstractTlsKeyExchangeFactory
    {
        public override TlsKeyExchange CreateDHKeyExchange(int keyExchange)
        {
            // turbo-strip: E2 no static DH key exchange
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateDHanonKeyExchangeClient(int keyExchange,
            TlsDHGroupVerifier dhGroupVerifier)
        {
            // turbo-strip: E2 no anonymous key exchange
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateDHanonKeyExchangeServer(int keyExchange, TlsDHConfig dhConfig)
        {
            // turbo-strip: E2 no anonymous key exchange
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateDheKeyExchangeClient(int keyExchange, TlsDHGroupVerifier dhGroupVerifier)
        {
            // turbo-strip: E2 no finite-field DHE key exchange
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateDheKeyExchangeServer(int keyExchange, TlsDHConfig dhConfig)
        {
            // turbo-strip: E2 no finite-field DHE key exchange
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateECDHKeyExchange(int keyExchange)
        {
            // turbo-strip: E2 no static ECDH key exchange
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateECDHanonKeyExchangeClient(int keyExchange)
        {
            // turbo-strip: E2 no anonymous key exchange
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateECDHanonKeyExchangeServer(int keyExchange, TlsECConfig ecConfig)
        {
            // turbo-strip: E2 no anonymous key exchange
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
            // turbo-strip: E2 no PSK key exchange
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreatePskKeyExchangeServer(int keyExchange,
            TlsPskIdentityManager pskIdentityManager, TlsDHConfig dhConfig, TlsECConfig ecConfig)
        {
            // turbo-strip: E2 no PSK key exchange
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateRsaKeyExchange(int keyExchange)
        {
            // turbo-strip: E2 no RSA key exchange
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateSrpKeyExchangeClient(int keyExchange, TlsSrpIdentity srpIdentity,
            TlsSrpConfigVerifier srpConfigVerifier)
        {
            // turbo-strip: E2 no SRP key exchange
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }

        public override TlsKeyExchange CreateSrpKeyExchangeServer(int keyExchange, TlsSrpLoginParameters loginParameters)
        {
            // turbo-strip: E2 no SRP key exchange
            throw new TlsFatalAlert(AlertDescription.internal_error);
        }
    }
}
