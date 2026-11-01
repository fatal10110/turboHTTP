using System;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// Test preconditions with outcomes CI can tell apart (#140). Code that ships in the package
    /// (the BouncyCastle provider and its vendored library) is always part of the test build, so a
    /// missing, renamed or stripped piece fails the test with <see cref="BouncyCastleMissingPrefix"/>
    /// instead of skipping it. Only genuinely environment-dependent preconditions (no network, no
    /// self-signed certificate generation, no SslStream ALPN) skip, always with
    /// <see cref="EnvironmentIgnorePrefix"/>, so any other Ignored result is a defect.
    /// </summary>
    internal static class TestRequirements
    {
        /// <summary>Prefix of every environment-dependent <c>Assert.Ignore</c> message.</summary>
        public const string EnvironmentIgnorePrefix = "[env] ";

        /// <summary>Prefix of every failure caused by missing BouncyCastle code.</summary>
        public const string BouncyCastleMissingPrefix = "[bc-missing] ";

        /// <summary>Assembly of the BouncyCastle TLS provider glue.</summary>
        public const string BouncyCastleGlueAssembly = "TurboHTTP.Transport.BouncyCastle";

        /// <summary>Assembly of the vendored BouncyCastle library (<c>Runtime/Transport/BouncyCastle/Lib</c>).</summary>
        public const string BouncyCastleLibAssembly = "TurboHTTP.SecureProtocol";

        /// <summary>Skips the test because the runtime environment lacks a capability the test needs.</summary>
        public static void IgnoreEnvironment(string reason)
        {
            Assert.Ignore(EnvironmentIgnorePrefix + reason);
        }

        /// <summary>Fails the test unless the BouncyCastle TLS provider is available.</summary>
        public static void RequireBouncyCastle()
        {
            if (!TlsProviderSelector.IsBouncyCastleAvailable())
            {
                throw BouncyCastleMissing(
                    "TlsProviderSelector.IsBouncyCastleAvailable() is false; the " + BouncyCastleGlueAssembly +
                    " assembly ships with the package and must be part of the test build (define " +
                    "TURBOHTTP_BOUNCYCASTLE in the test project; Tools~/unity/run-tests.sh does).");
            }
        }

        /// <summary>Fails the test unless BouncyCastle is available; returns its provider.</summary>
        public static ITlsProvider RequireBouncyCastleProvider()
        {
            RequireBouncyCastle();
            return TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
        }

        /// <summary>Resolves a BouncyCastle type by assembly-qualified name or fails the test.</summary>
        public static Type RequireBouncyCastleType(string assemblyQualifiedName)
        {
            var type = Type.GetType(assemblyQualifiedName, throwOnError: false);
            if (type == null)
                throw BouncyCastleMissing("type '" + assemblyQualifiedName + "' does not resolve.");
            return type;
        }

        /// <summary>
        /// The failure for missing BouncyCastle code. Thrown directly (not through
        /// <c>Assert.Fail</c>) so a test can assert on it with <c>Assert.Throws</c>.
        /// </summary>
        public static AssertionException BouncyCastleMissing(string detail, Exception inner = null)
        {
            var message = BouncyCastleMissingPrefix + detail;
            return inner == null ? new AssertionException(message) : new AssertionException(message, inner);
        }
    }
}
