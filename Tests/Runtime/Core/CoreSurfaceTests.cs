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

        [TestCase("TurboHTTP.Core.Internal.ObjectPool`1")]
        [TestCase("TurboHTTP.Core.Internal.ObjectPoolDiagnostics")]
        [TestCase("TurboHTTP.Core.Internal.ByteArrayPool")]
        [TestCase("TurboHTTP.Core.Internal.ByteArrayPoolDiagnostics")]
        [TestCase("TurboHTTP.Core.Internal.SegmentedBuffer")]
        [TestCase("TurboHTTP.Core.Internal.SegmentedReadStream")]
        [TestCase("TurboHTTP.Core.PlatformConfig")]
        [TestCase("TurboHTTP.Core.PlatformInfo")]
        [TestCase("TurboHTTP.Core.IL2CPPCompatibility")]
        [TestCase("TurboHTTP.Core.ProjectJsonBridge")]
        [TestCase("TurboHTTP.Core.IJsonBridge")]
        public void PlumbingType_IsInternal(string typeName)
        {
            var type = CoreAssembly.GetType(typeName, throwOnError: true);
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
