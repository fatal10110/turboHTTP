using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Parameters;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Math;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Security;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Utilities;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Generators
{
	/**
	* Generate a random factor suitable for use with RSA blind signatures
	* as outlined in Chaum's blinding and unblinding as outlined in
	* "Handbook of Applied Cryptography", page 475.
	*/
	public class RsaBlindingFactorGenerator
	{
		private RsaKeyParameters key;
		private SecureRandom random;

		/**
		* Initialise the factor generator
		*
		* @param param the necessary RSA key parameters.
		*/
		public void Init(ICipherParameters param)
		{
			key = (RsaKeyParameters)ParameterUtilities.GetRandom(param, out var providedRandom);
			random = CryptoServicesRegistrar.GetSecureRandom(providedRandom);

			if (key.IsPrivate)
				throw new ArgumentException("generator requires RSA public key");
		}

		/**
		* Generate a suitable blind factor for the public key the generator was initialised with.
		*
		* @return a random blind factor
		*/
		public BigInteger GenerateBlindingFactor()
		{
			if (key == null)
				throw new InvalidOperationException("generator not initialised");

			BigInteger m = key.Modulus;
			int length = m.BitLength - 1; // must be less than m.BitLength

			BigInteger factor;
			do
			{
				factor = BigIntegers.CreateRandomBigInteger(length, random);
			}
			while (factor.CompareTo(BigInteger.Two) < 0 || !BigIntegers.ModOddIsCoprime(m, factor));

			return factor;
		}
	}
}
