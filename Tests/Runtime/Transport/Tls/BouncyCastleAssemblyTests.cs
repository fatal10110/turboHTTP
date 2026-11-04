using System;
using System.Linq;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// The vendored BouncyCastle library compiles as its own assembly (#143), and missing
    /// BouncyCastle code fails the TLS suites instead of skipping them (#140).
    /// </summary>
    [TestFixture]
    public class BouncyCastleAssemblyTests
    {
        private const string ProviderTypeName =
            "TurboHTTP.Transport.BouncyCastle.BouncyCastleTlsProvider, " + TestRequirements.BouncyCastleGlueAssembly;

        private const string BcTlsCryptoTypeName =
            "TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto.Impl.BC.BcTlsCrypto, " +
            TestRequirements.BouncyCastleLibAssembly;

        [Test]
        public void BouncyCastleProvider_IsPartOfTheTestBuild()
        {
            var provider = TestRequirements.RequireBouncyCastleProvider();
            Assert.AreEqual("BouncyCastle", provider.ProviderName);
        }

        [Test]
        public void GlueAssembly_RegistersItsProviderAtStartup()
        {
            // No test code registers it: BouncyCastleModuleRegistration runs from its startup hooks
            // when the project defines TURBOHTTP_BOUNCYCASTLE (#196), with no by-name lookup (#186).
            var provider = TestRequirements.RequireBouncyCastleProvider();

            Assert.AreEqual(TestRequirements.BouncyCastleGlueAssembly, provider.GetType().Assembly.GetName().Name);
            Assert.AreSame(provider, TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle));
        }

        [Test]
        public void VendoredLibrary_IsItsOwnAssembly()
        {
            var glueType = TestRequirements.RequireBouncyCastleType(ProviderTypeName);
            var libType = TestRequirements.RequireBouncyCastleType(BcTlsCryptoTypeName);

            Assert.AreEqual(TestRequirements.BouncyCastleLibAssembly, libType.Assembly.GetName().Name);
            Assert.AreEqual(TestRequirements.BouncyCastleGlueAssembly, glueType.Assembly.GetName().Name);
            Assert.AreNotSame(glueType.Assembly, libType.Assembly,
                "BouncyCastle Lib must not compile into the provider glue assembly");
        }

        [Test]
        public void GlueAssembly_ReferencesTheVendoredLibrary()
        {
            var glueType = TestRequirements.RequireBouncyCastleType(ProviderTypeName);

            var referencesLib = glueType.Assembly.GetReferencedAssemblies()
                .Any(r => r.Name == TestRequirements.BouncyCastleLibAssembly);

            Assert.IsTrue(referencesLib,
                TestRequirements.BouncyCastleGlueAssembly + " must reference " + TestRequirements.BouncyCastleLibAssembly);
        }

        [Test]
        public void RenamedBouncyCastleType_FailsInsteadOfIgnoring()
        {
            var ex = Assert.Throws<AssertionException>(() => TestRequirements.RequireBouncyCastleType(
                "TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto.Impl.BC.BcTlsCryptoRenamed, " +
                TestRequirements.BouncyCastleLibAssembly));

            StringAssert.StartsWith(TestRequirements.BouncyCastleMissingPrefix, ex.Message);
        }
    }
}
