using System;
using System.IO;
using System.Runtime.Serialization;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Utilities;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls
{
    [Serializable]
    public class TlsTimeoutException
        : IOException
    {
        public TlsTimeoutException()
            : base()
        {
        }

        public TlsTimeoutException(string message)
            : base(message)
        {
        }

        public TlsTimeoutException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

#if NET8_0_OR_GREATER
        [Obsolete(Exceptions.SYSLIB0051_Message, DiagnosticId="SYSLIB0051", UrlFormat="https://aka.ms/dotnet-warnings/{0}")]
#endif
        protected TlsTimeoutException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
