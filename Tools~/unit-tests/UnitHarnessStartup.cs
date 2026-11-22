using NUnit.Framework;
using TurboHTTP;
using TurboHTTP.JSON;
using TurboHTTP.Transport.BouncyCastle;

/// <summary>
/// Harness-only startup (#879): runs what the Unity Editor runs at domain load
/// ([InitializeOnLoadMethod] / [RuntimeInitializeOnLoadMethod]) before the first test, so the tests see the
/// same registrations they see in the host editor: the default HTTP and WebSocket transports, the
/// TurboHTTP.JSON bridge and the BouncyCastle TLS provider. Outside any namespace, so it covers the whole
/// assembly. JsonUtilitySerializer.RegisterAsDefaultIfBuiltIn is left out: JsonUtility is native engine code.
/// </summary>
[SetUpFixture]
public sealed class UnitHarnessStartup
{
    [OneTimeSetUp]
    public void RegisterModules()
    {
        TurboHttpBootstrap.RegisterDefaultTransports();
        JsonModuleRegistration.EnsureRegistered();
        BouncyCastleModuleRegistration.EnsureRegistered();
    }
}
