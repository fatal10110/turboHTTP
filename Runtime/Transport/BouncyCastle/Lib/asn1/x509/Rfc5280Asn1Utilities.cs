using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Utilities.Date;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.X509
{
    internal class Rfc5280Asn1Utilities
    {
        internal static DerGeneralizedTime CreateGeneralizedTime(DateTime dateTime) =>
            new DerGeneralizedTime(DateTimeUtilities.WithPrecisionSecond(dateTime));

        internal static DerUtcTime CreateUtcTime(DateTime dateTime) => new DerUtcTime(dateTime, 2049);
    }
}
