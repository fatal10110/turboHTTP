using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Agreement.Kdf;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Math;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Security;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Utilities;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Agreement
{
    // TODO[api] sealed
    public class ECMqvWithKdfBasicAgreement
		: ECMqvBasicAgreement
	{
		private readonly string m_algorithm;
		private readonly IDerivationFunction m_kdf;

		public ECMqvWithKdfBasicAgreement(string algorithm, IDerivationFunction kdf)
		{
            m_algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
            m_kdf = kdf ?? throw new ArgumentNullException(nameof(kdf));
		}

		public override BigInteger CalculateAgreement(ICipherParameters pubKey)
		{
            BigInteger result = base.CalculateAgreement(pubKey);

            return BasicAgreementWithKdf.CalculateAgreementWithKdf(m_algorithm, m_kdf, GetFieldSize(), result);
		}
	}
}
