using System;
using System.IO;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls
{
    public interface TlsCloseable
    {
        /// <exception cref="IOException"/>
        void Close();
    }
}
