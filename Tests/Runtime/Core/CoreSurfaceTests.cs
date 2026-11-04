using System;
using System.Reflection;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Unity.Mobile;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// Guards the Core public surface (#77, #156): feature types live outside Core, pooling and
    /// platform plumbing is internal, and no test-only shim ships in a runtime assembly.
    /// Tools~/ci/api-baseline/core-public-types.txt is the CI-side list of the remaining types.
    /// </summary>
    public sealed class CoreSurfaceTests
    {
        private static readonly Assembly CoreAssembly = typeof(UHttpClient).Assembly;

        // typeof, not a name lookup: High stripping removes internal types nothing references, so
        // CoreAssembly.GetType(name) threw TypeLoadException in an IL2CPP player for the types only
        // their own file names (#321). The name still pins the namespace the surface rule is about.
        private static readonly object[] PlumbingTypes =
        {
            new object[] { typeof(global::TurboHTTP.Core.Internal.ObjectPool<>), "TurboHTTP.Core.Internal.ObjectPool`1" },
            new object[] { typeof(global::TurboHTTP.Core.Internal.ObjectPoolDiagnostics), "TurboHTTP.Core.Internal.ObjectPoolDiagnostics" },
            new object[] { typeof(global::TurboHTTP.Core.Internal.ByteArrayPool), "TurboHTTP.Core.Internal.ByteArrayPool" },
            new object[] { typeof(global::TurboHTTP.Core.Internal.ByteArrayPoolDiagnostics), "TurboHTTP.Core.Internal.ByteArrayPoolDiagnostics" },
            new object[] { typeof(global::TurboHTTP.Core.Internal.SegmentedBuffer), "TurboHTTP.Core.Internal.SegmentedBuffer" },
            new object[] { typeof(global::TurboHTTP.Core.Internal.SegmentedReadStream), "TurboHTTP.Core.Internal.SegmentedReadStream" },
            new object[] { typeof(global::TurboHTTP.Core.PlatformConfig), "TurboHTTP.Core.PlatformConfig" },
            new object[] { typeof(global::TurboHTTP.Core.PlatformInfo), "TurboHTTP.Core.PlatformInfo" },
            new object[] { typeof(global::TurboHTTP.Core.IL2CPPCompatibility), "TurboHTTP.Core.IL2CPPCompatibility" },
            new object[] { typeof(global::TurboHTTP.Core.ProjectJsonBridge), "TurboHTTP.Core.ProjectJsonBridge" },
            new object[] { typeof(global::TurboHTTP.Core.IJsonBridge), "TurboHTTP.Core.IJsonBridge" },
        };

        [TestCaseSource(nameof(PlumbingTypes))]
        public void PlumbingType_IsInternal(Type type, string typeName)
        {
            Assert.AreEqual(typeName, type.FullName);
            Assert.AreSame(CoreAssembly, type.Assembly, typeName + " must live in Core");
            Assert.IsFalse(type.IsVisible, typeName + " must not be public");
        }

        [TestCase("TurboHTTP.Core.AdaptiveInterceptor")]
        [TestCase("TurboHTTP.Core.AdaptivePolicy")]
        [TestCase("TurboHTTP.Core.BackgroundNetworkingInterceptor")]
        [TestCase("TurboHTTP.Core.BackgroundNetworkingPolicy")]
        [TestCase("TurboHTTP.Core.BackgroundRequestQueuedException")]
        [TestCase("TurboHTTP.Core.BackgroundRequestBuilderExtensions")]
        [TestCase("TurboHTTP.Core.IBackgroundExecutionBridge")]
        [TestCase("TurboHTTP.Core.IBackgroundExecutionScope")]
        [TestCase("TurboHTTP.Core.IDeferredBackgroundWorkBridge")]
        [TestCase("TurboHTTP.Core.NetworkQuality")]
        [TestCase("TurboHTTP.Core.NetworkQualityDetector")]
        [TestCase("TurboHTTP.Core.NetworkQualitySample")]
        [TestCase("TurboHTTP.Core.NetworkQualitySnapshot")]
        [TestCase("TurboHTTP.Core.NetworkQualityThresholds")]
        [TestCase("TurboHTTP.Core.LegacyHttpHandlerCompatExtensions")]
        public void FeatureOrShimType_IsNotInCore(string typeName)
        {
            Assert.IsNull(CoreAssembly.GetType(typeName, throwOnError: false), typeName);
        }

        [Test]
        public void MobileFeatureTypes_LiveInTheUnityModule()
        {
            Assert.AreEqual("TurboHTTP.Unity", typeof(AdaptiveInterceptor).Assembly.GetName().Name);
            Assert.AreEqual("TurboHTTP.Unity", typeof(BackgroundNetworkingInterceptor).Assembly.GetName().Name);
            Assert.AreEqual("TurboHTTP.Unity", typeof(NetworkQualityDetector).Assembly.GetName().Name);
        }

        [Test]
        public void CorePublicTypeCount_IsAtMostSixty()
        {
            var exported = CoreAssembly.GetExportedTypes();
            Assert.LessOrEqual(exported.Length, 60, "Core exports " + exported.Length + " public types");
        }

        [Test]
        public void MockTransports_HaveNoObsoleteSendAsync()
        {
            Assert.IsNull(typeof(MockTransport).GetMethod("SendAsync"));
            Assert.IsNull(typeof(RecordReplayTransport).GetMethod("SendAsync"));
        }
    }
}
