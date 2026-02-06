namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto
{
    public interface IMacDerivationFunction
        : IDerivationFunction
    {
        IMac Mac { get; }
    }
}
