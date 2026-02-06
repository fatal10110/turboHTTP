using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.X509;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.Pkcs
{
    // TODO[api] This is not supposed to be a separate type; remove and use AlgorithmIdentifier
    public class KeyDerivationFunc
		: AlgorithmIdentifier
	{
		internal KeyDerivationFunc(Asn1Sequence seq)
			: base(seq)
		{
		}

		public KeyDerivationFunc(
			DerObjectIdentifier	id,
			Asn1Encodable		parameters)
			: base(id, parameters)
		{
		}
	}
}