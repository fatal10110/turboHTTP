using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Security;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Parameters
{
    public sealed class SlhDsaKeyGenerationParameters
        : KeyGenerationParameters
    {
        private readonly SlhDsaParameters m_parameters;

        public SlhDsaKeyGenerationParameters(SecureRandom random, SlhDsaParameters parameters)
            : base(random, 0)
        {
            m_parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        }

        public SlhDsaKeyGenerationParameters(SecureRandom random, DerObjectIdentifier parametersOid)
            : base(random, 0)
        {
            if (parametersOid == null)
                throw new ArgumentNullException(nameof(parametersOid));
            if (!SlhDsaParameters.ByOid.TryGetValue(parametersOid, out m_parameters))
                throw new ArgumentException("unrecognised SLH-DSA parameters OID", nameof(parametersOid));
        }

        public SlhDsaParameters Parameters => m_parameters;
    }
}
