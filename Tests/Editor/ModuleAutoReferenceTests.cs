using System;
using System.IO;
using NUnit.Framework;
using TurboHTTP.Editor;
using UnityEngine;

namespace TurboHTTP.Tests.Editor
{
    /// <summary>
    /// Scripts in <c>Assets/</c> without an asmdef compile into Assembly-CSharp, which references
    /// only <c>autoReferenced</c> assemblies; asmdef references are not transitive, so an aggregate
    /// assembly cannot re-export the modules. Every user-facing module is therefore autoReferenced
    /// (#7), and the test/opt-in assemblies stay explicit.
    /// </summary>
    [TestFixture]
    public class ModuleAutoReferenceTests
    {
        [Serializable]
        internal sealed class AsmdefFields
        {
            public string name;
            public bool autoReferenced;
            public string[] defineConstraints;
        }

        private string _packageRoot;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _packageRoot = TurboHttpLinkerProcessor.FindPackageRoot();
            Assert.That(_packageRoot, Is.Not.Null, "package root of " + TurboHttpLinkerProcessor.PackageName);
        }

        [TestCase("Core/TurboHTTP.Core.asmdef")]
        [TestCase("TurboHTTP.Complete.asmdef")]
        [TestCase("Transport/TurboHTTP.Transport.asmdef")]
        [TestCase("JSON/TurboHTTP.JSON.asmdef")]
        [TestCase("Retry/TurboHTTP.Retry.asmdef")]
        [TestCase("Auth/TurboHTTP.Auth.asmdef")]
        [TestCase("Cache/TurboHTTP.Cache.asmdef")]
        [TestCase("Middleware/TurboHTTP.Middleware.asmdef")]
        [TestCase("Observability/TurboHTTP.Observability.asmdef")]
        [TestCase("Files/TurboHTTP.Files.asmdef")]
        [TestCase("RateLimit/TurboHTTP.RateLimit.asmdef")]
        [TestCase("Unity/TurboHTTP.Unity.asmdef")]
        [TestCase("WebSocket/TurboHTTP.WebSocket.asmdef")]
        [TestCase("WebSocket.Transport/TurboHTTP.WebSocket.Transport.asmdef")]
        [TestCase("Unity.WebSocket/TurboHTTP.Unity.WebSocket.asmdef")]
        public void UserFacingModule_IsVisibleToAssemblyCSharp(string relativePath)
        {
            Assert.That(Read(relativePath).autoReferenced, Is.True, relativePath);
        }

        [TestCase("Testing/TurboHTTP.Testing.asmdef")]
        [TestCase("UniTask/TurboHTTP.UniTask.asmdef")]
        [TestCase("Transport/BouncyCastle/TurboHTTP.Transport.BouncyCastle.asmdef")]
        public void OptInModule_StaysExplicit(string relativePath)
        {
            Assert.That(Read(relativePath).autoReferenced, Is.False, relativePath);
        }

        [Test]
        public void BouncyCastleGlue_CompilesOnlyWhenTheProjectOptsIn()
        {
            // Without TURBOHTTP_BOUNCYCASTLE the glue is not compiled, so nothing references the
            // vendored library and neither assembly reaches a player's linker output (#196).
            var fields = Read("Transport/BouncyCastle/TurboHTTP.Transport.BouncyCastle.asmdef");
            Assert.That(fields.defineConstraints, Is.EquivalentTo(new[] { "TURBOHTTP_BOUNCYCASTLE" }));
        }

        private AsmdefFields Read(string relativePath)
        {
            var path = Path.Combine(_packageRoot, "Runtime", relativePath);
            Assert.That(File.Exists(path), Is.True, path);
            var fields = JsonUtility.FromJson<AsmdefFields>(File.ReadAllText(path));
            StringAssert.EndsWith(fields.name + ".asmdef", relativePath);
            return fields;
        }
    }
}
