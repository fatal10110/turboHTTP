using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Security;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Parameters
{
    public sealed class MLDsaKeyGenerationParameters
        : KeyGenerationParameters
    {
        private readonly MLDsaParameters m_parameters;

        public MLDsaKeyGenerationParameters(SecureRandom random, MLDsaParameters parameters)
            : base(random, 0)
        {
            m_parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        }

        public MLDsaKeyGenerationParameters(SecureRandom random, DerObjectIdentifier parametersOid)
            : base(random, 0)
        {
            if (parametersOid == null)
                throw new ArgumentNullException(nameof(parametersOid));
            if (!MLDsaParameters.ByOid.TryGetValue(parametersOid, out m_parameters))
                throw new ArgumentException("unrecognised ML-DSA parameters OID", nameof(parametersOid));
        }

        public MLDsaParameters Parameters => m_parameters;
    }
}
