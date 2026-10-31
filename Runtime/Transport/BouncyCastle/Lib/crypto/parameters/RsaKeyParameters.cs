using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Math;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Utilities;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Parameters
{
    /// <summary>Base class for RSA key parameters.</summary>
    public class RsaKeyParameters
        : AsymmetricKeyParameter
    {
        public static BigInteger ValidateModulus(BigInteger modulus) => Validate(modulus, isInternal: false);

        private readonly BigInteger m_modulus;
        private readonly BigInteger m_exponent;

        /// <summary>Initializes a new instance of <see cref="RsaKeyParameters"/>.</summary>
        /// <param name="isPrivate">Whether the key is private or not.</param>
        /// <param name="modulus">The RSA modulus.</param>
        /// <param name="exponent">The RSA exponent (public exponent for public keys, private exponent for private keys).</param>
        public RsaKeyParameters(bool isPrivate, BigInteger modulus, BigInteger exponent)
            : this(isPrivate, modulus, exponent, isInternal: false)
        {
        }

        internal RsaKeyParameters(bool isPrivate, BigInteger modulus, BigInteger exponent, bool isInternal)
            : base(isPrivate)
        {
            if (modulus == null)
                throw new ArgumentNullException(nameof(modulus));
            if (exponent == null)
                throw new ArgumentNullException(nameof(exponent));
            if (modulus.SignValue <= 0)
                throw new ArgumentException("Not a valid RSA modulus", nameof(modulus));
            if (exponent.SignValue <= 0)
                throw new ArgumentException("Not a valid RSA exponent", nameof(exponent));
            if (!isPrivate && (exponent.IntValue & 1) == 0)
                throw new ArgumentException("RSA publicExponent is even", nameof(exponent));

            m_modulus = Validate(modulus, isInternal);
            m_exponent = exponent;
        }

        /// <summary>Gets the RSA modulus.</summary>
        public BigInteger Modulus => m_modulus;

        /// <summary>Gets the RSA exponent.</summary>
        public BigInteger Exponent => m_exponent;

        public override bool Equals(object obj)
        {
            return obj is RsaKeyParameters that
                && this.IsPrivate == that.IsPrivate
                && this.Modulus.Equals(that.Modulus)
                && this.Exponent.Equals(that.Exponent);
        }

        public override int GetHashCode() => IsPrivate.GetHashCode() ^ Modulus.GetHashCode() ^ Exponent.GetHashCode();

        private static int GetMRIterations(int bits)
        {
            int iterations = bits >= 1536 ? 3
                : bits >= 1024 ? 4
                : bits >= 512 ? 7
                : 50;
            return iterations;
        }

        private static BigInteger Validate(BigInteger modulus, bool isInternal)
        {
            // TODO Add m_validated cache
            //if (m_validated.Contains(modulus))
            //    return modulus;

            if (!isInternal)
            {
                if (!modulus.TestBit(0))
                    throw new ArgumentException("RSA modulus is even", nameof(modulus));

                // If you need to set this you need to have a serious word to whoever is generating your keys.
                if (Properties.GetBoolean(Properties.RsaAllowUnsafeModulus, false))
                    return modulus;

                // turbo-strip: E7 default RSA modulus cap 8192 bits (upstream 16384); the property still overrides
                int maxBitLength = Properties.GetInt32(Properties.RsaMaxSize, 8192);
                if (modulus.BitLength > maxBitLength)
                    throw new ArgumentException("RSA modulus out of range", nameof(modulus));

                if (BigIntegers.HasAnySmallFactors(modulus))
                    throw new ArgumentException("RSA modulus has a small prime factor", nameof(modulus));

                // turbo-strip: E7 no Miller-Rabin on peer moduli by default (upstream 8ee52127 costs ~1 s at 8192
                // bits per key); GetMRIterations is kept unused for rebase surface; the property still overrides
                int iterations = Properties.GetInt32(Properties.RsaMaxMRTests, 0);
                if (iterations > 0)
                {
                    Primes.MROutput mr = Primes.EnhancedMRProbablePrimeTest(modulus,
                        CryptoServicesRegistrar.GetSecureRandom(), iterations);
                    if (!mr.IsProvablyComposite)
                        throw new ArgumentException("RSA modulus is not composite", nameof(modulus));
                }
            }

            //m_validated.Add(modulus);
            return modulus;
        }
    }
}
