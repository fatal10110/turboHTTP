using System;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls
{
    public interface TlsHeartbeat
    {
        byte[] GeneratePayload();

        int IdleMillis { get; }

        int TimeoutMillis { get; }
    }
}
