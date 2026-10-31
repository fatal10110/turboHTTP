using NUnit.Framework;
using TurboHTTP.Editor;
using UnityEditor;
using UnityEditor.Build;

namespace TurboHTTP.Tests.Editor
{
    /// <summary>
    /// WebGL is unsupported in v1.0, so a WebGL build must fail at build time with an actionable
    /// message instead of compiling and failing every request at runtime (#9).
    /// </summary>
    [TestFixture]
    public class WebGlBuildGuardTests
    {
        [Test]
        public void Guard_IsAPreprocessBuildCallback()
        {
            Assert.That(new TurboHttpWebGlBuildGuard(), Is.InstanceOf<IPreprocessBuildWithReport>());
        }

        [Test]
        public void WebGl_FailsTheBuildWithAnActionableMessage()
        {
            var ex = Assert.Throws<BuildFailedException>(
                () => TurboHttpWebGlBuildGuard.ThrowIfUnsupported(BuildTarget.WebGL));

            StringAssert.Contains("does not support WebGL in v1.0", ex.Message);
            StringAssert.Contains("PlatformNotes", ex.Message);
        }

        [TestCase(BuildTarget.iOS)]
        [TestCase(BuildTarget.Android)]
        [TestCase(BuildTarget.StandaloneWindows64)]
        [TestCase(BuildTarget.StandaloneOSX)]
        [TestCase(BuildTarget.StandaloneLinux64)]
        public void SupportedTargets_BuildNormally(BuildTarget target)
        {
            Assert.DoesNotThrow(() => TurboHttpWebGlBuildGuard.ThrowIfUnsupported(target));
        }
    }
}
