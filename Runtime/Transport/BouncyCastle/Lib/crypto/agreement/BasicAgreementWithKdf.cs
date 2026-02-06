using System.Security.Cryptography;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Agreement.Kdf;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Math;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Security;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Utilities;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Agreement
{
    internal static class BasicAgreementWithKdf
    {
        internal static BigInteger CalculateAgreementWithKdf(string algorithm, IDerivationFunction kdf, int fieldSize,
            BigInteger result)
        {
            // Note that the ec.KeyAgreement class in JCE only uses kdf in one
            // of the engineGenerateSecret methods.

            int keySize = GeneratorUtilities.GetDefaultKeySize(algorithm);

            DHKdfParameters dhKdfParams = new DHKdfParameters(
                new DerObjectIdentifier(algorithm),
                keySize,
                BigIntegers.AsUnsignedByteArray(fieldSize, result));

            kdf.Init(dhKdfParams);

            byte[] keyBytes = new byte[keySize / 8];
            kdf.GenerateBytes(keyBytes, 0, keyBytes.Length);

            return new BigInteger(1, keyBytes);
        }
    }
}
