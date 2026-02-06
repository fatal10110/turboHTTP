using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.X509;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.X509.Extension
{
    /**
	 * A high level subject key identifier.
	 */
    [Obsolete("Use 'X509ExtensionUtilities' methods instead")]
    public class SubjectKeyIdentifierStructure
        : SubjectKeyIdentifier
    {
        public SubjectKeyIdentifierStructure(Asn1OctetString encodedValue)
            : base(Asn1OctetString.GetInstance(encodedValue.GetOctets()))
        {
        }

        public SubjectKeyIdentifierStructure(AsymmetricKeyParameter pubKey)
            : base(keyID: X509ExtensionUtilities.CalculateKeyIdentifier(pubKey))
        {
        }
    }
}
