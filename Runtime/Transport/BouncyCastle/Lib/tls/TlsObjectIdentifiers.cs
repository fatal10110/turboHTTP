using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.X509;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls
{
    /// <summary>Object Identifiers associated with TLS extensions.</summary>
    public abstract class TlsObjectIdentifiers
    {
        /// <summary>RFC 7633</summary>
        public static readonly DerObjectIdentifier id_pe_tlsfeature = X509ObjectIdentifiers.IdPE.Branch("24");
    }
}
