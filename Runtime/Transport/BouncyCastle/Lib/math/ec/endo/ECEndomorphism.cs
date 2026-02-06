using System;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Math.EC.Endo
{
    public interface ECEndomorphism
    {
        ECPointMap PointMap { get; }

        bool HasEfficientPointMap { get; }
    }
}
