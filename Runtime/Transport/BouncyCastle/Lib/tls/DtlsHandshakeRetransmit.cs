using System;
using System.IO;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls
{
    internal interface DtlsHandshakeRetransmit
    {
        /// <exception cref="IOException"/>
        void ReceivedHandshakeRecord(int epoch, byte[] buf, int off, int len);
    }
}
