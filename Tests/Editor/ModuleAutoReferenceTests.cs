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
    /// (#7), and the test/opt-in assemblies stay explicit. The UniTask adapters are define-gated
    /// rather than opt-in by reference: once <c>TURBOHTTP_UNITASK</c> is defined (automatically for
    /// the com.cysharp.unitask package, by hand for a UniTask copied into Assets/), Assembly-CSharp
    /// must see them too (#944).
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
            public string[] references;
            public VersionDefine[] versionDefines;
        }

        [Serializable]
        internal sealed class VersionDefine
        {
            public string name;
            public string expression;
            public string define;
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
        [TestCase("UniTask/TurboHTTP.UniTask.asmdef")]
        [TestCase("UniTask.WebSocket/TurboHTTP.UniTask.WebSocket.asmdef")]
        public void UserFacingModule_IsVisibleToAssemblyCSharp(string relativePath)
        {
            Assert.That(Read(relativePath).autoReferenced, Is.True, relativePath);
        }

        [TestCase("Testing/TurboHTTP.Testing.asmdef")]
        [TestCase("Transport/BouncyCastle/TurboHTTP.Transport.BouncyCastle.asmdef")]
        [TestCase("Transport/BouncyCastle/Lib/TurboHTTP.SecureProtocol.asmdef")]
        public void OptInModule_StaysExplicit(string relativePath)
        {
            Assert.That(Read(relativePath).autoReferenced, Is.False, relativePath);
        }

        [TestCase("Transport/BouncyCastle/TurboHTTP.Transport.BouncyCastle.asmdef")]
        [TestCase("Transport/BouncyCastle/Lib/TurboHTTP.SecureProtocol.asmdef")]
        public void BouncyCastleAssemblies_CompileOnlyWhenTheProjectOptsIn(string relativePath)
        {
            // Without TURBOHTTP_BOUNCYCASTLE neither the glue (#196) nor the vendored library (#870) is
            // compiled, so neither can reach a player. Not compiling the library matters on Mono: at
            // Managed Stripping Level Disabled UnityLinker does not run and every compiled assembly is
            // copied to the player, referenced or not.
            Assert.That(Read(relativePath).defineConstraints, Is.EquivalentTo(new[] { "TURBOHTTP_BOUNCYCASTLE" }),
                relativePath);
        }

        [TestCase("UniTask/TurboHTTP.UniTask.asmdef", new[] { "TurboHTTP.Core", "UniTask" })]
        [TestCase("UniTask.WebSocket/TurboHTTP.UniTask.WebSocket.asmdef",
            new[] { "TurboHTTP.Core", "TurboHTTP.WebSocket", "UniTask" })]
        public void UniTaskAdapters_SplitSoHttpOnlyUsersSkipWebSocket(string relativePath, string[] expectedReferences)
        {
            // HTTP-only UniTask users reference TurboHTTP.UniTask without compiling TurboHTTP.WebSocket;
            // the WebSocket adapters live in TurboHTTP.UniTask.WebSocket (#116). Both compile only with UniTask.
            var fields = Read(relativePath);
            Assert.That(fields.references, Is.EquivalentTo(expectedReferences), relativePath);
            Assert.That(fields.defineConstraints, Is.EquivalentTo(new[] { "TURBOHTTP_UNITASK" }), relativePath);
            Assert.That(fields.versionDefines, Has.Length.EqualTo(1), relativePath);
            Assert.That(fields.versionDefines[0].name, Is.EqualTo("com.cysharp.unitask"), relativePath);
            Assert.That(fields.versionDefines[0].expression, Is.EqualTo("2.0.0"), relativePath);
            Assert.That(fields.versionDefines[0].define, Is.EqualTo("TURBOHTTP_UNITASK"), relativePath);
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
