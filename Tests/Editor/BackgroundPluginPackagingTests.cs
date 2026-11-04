using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using TurboHTTP.Editor;
using UnityEditor;
using UnityEditor.Build;

namespace TurboHTTP.Tests.Editor
{
    /// <summary>
    /// Packaging of the experimental background networking plugins: the Android plugin is an
    /// .androidlib that declares its WorkManager dependency, so a clean project builds without Gradle
    /// template edits (#89); both native plugins stay out of builds that do not opt in (#106); the iOS
    /// bridge is not shipped to tvOS, where no managed code calls it (#176); and the bridges never
    /// block on the main-thread dispatcher (#88).
    /// </summary>
    [TestFixture]
    public class BackgroundPluginPackagingTests
    {
        private const string AndroidLib = "Plugins/Android/TurboHttpBackground.androidlib";
        private const string IosBridge = "Plugins/iOS/TurboHttpBackgroundTaskBridge.mm";
        private const string IosTrustVerifier = "Plugins/iOS/TurboHttpSecTrustVerifier.mm";

        private static UnityEditor.PackageManager.PackageInfo Package()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TurboHttpBackgroundPluginGate).Assembly);
            Assert.IsNotNull(info, "TurboHTTP.Editor must load from the com.turbohttp.complete package");
            return info;
        }

        private static string Root => Package().resolvedPath;

        [Test]
        public void AndroidPlugin_IsAnAndroidLibDeclaringWorkManager()
        {
            var lib = Path.Combine(Root, AndroidLib);
            Assert.IsTrue(Directory.Exists(lib), lib + " must exist");
            Assert.IsTrue(File.Exists(Path.Combine(lib, "AndroidManifest.xml")));
            StringAssert.Contains("androidx.work:work-runtime", File.ReadAllText(Path.Combine(lib, "build.gradle")));
        }

        [Test]
        public void AndroidPlugin_HasNoLooseSourcesOrTemplatesOutsideTheLibrary()
        {
            var android = Path.Combine(Root, "Plugins", "Android");
            Assert.IsFalse(Directory.Exists(Path.Combine(android, "src")), "loose src/main/AndroidManifest.xml must be gone");
            Assert.IsFalse(File.Exists(Path.Combine(android, "mainTemplate.gradle")), "package-local Gradle templates are ignored by Unity");

            var lib = Path.GetFullPath(Path.Combine(Root, AndroidLib)) + Path.DirectorySeparatorChar;
            var loose = Directory.GetFiles(android, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".meta", StringComparison.Ordinal))
                .Where(f => !Path.GetFullPath(f).StartsWith(lib, StringComparison.Ordinal))
                .ToArray();
            CollectionAssert.IsEmpty(loose);
        }

        [Test]
        public void NativeImporters_TargetOnlyPlatformsWithAManagedCaller()
        {
            var android = Importer(AndroidLib);
            Assert.IsTrue(android.GetCompatibleWithPlatform(BuildTarget.Android));
            Assert.IsFalse(android.GetCompatibleWithEditor());

            foreach (var path in new[] { IosBridge, IosTrustVerifier })
            {
                var ios = Importer(path);
                Assert.IsTrue(ios.GetCompatibleWithPlatform(BuildTarget.iOS), path);
                Assert.IsFalse(ios.GetCompatibleWithPlatform(BuildTarget.tvOS), path + ": managed callers compile for UNITY_IOS only");
                Assert.IsFalse(ios.GetCompatibleWithEditor(), path);
            }
        }

        [Test]
        public void Gate_CoversBothBackgroundPlugins()
        {
            Assert.IsTrue(TurboHttpBackgroundPluginGate.IsGatedPlugin(Package().assetPath + "/" + AndroidLib));
            Assert.IsTrue(TurboHttpBackgroundPluginGate.IsGatedPlugin(Package().assetPath + "/" + IosBridge));
            Assert.IsFalse(TurboHttpBackgroundPluginGate.IsGatedPlugin(Package().assetPath + "/" + IosTrustVerifier));
            Assert.AreEqual(2, TurboHttpBackgroundPluginGate.Register());
            Assert.IsInstanceOf<IPreprocessBuildWithReport>(new TurboHttpBackgroundPluginGate());
        }

        [TestCase("TURBOHTTP_BACKGROUND_NETWORKING", true)]
        [TestCase("TURBOHTTP_BOUNCYCASTLE; TURBOHTTP_BACKGROUND_NETWORKING", true)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("TURBOHTTP_BACKGROUND_NETWORKING_OFF;TURBOHTTP_BOUNCYCASTLE", false)]
        public void Gate_RequiresTheExactDefine(string symbols, bool expected)
        {
            Assert.AreEqual(expected, TurboHttpBackgroundPluginGate.IsDefined(symbols));
        }

        [Test]
        public void Gate_IncludesTheAndroidLibOnlyWhenTheActiveTargetDefinesIt()
        {
            // The test project does not define it, so this normally checks the "left out" case.
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            var defined = TurboHttpBackgroundPluginGate.IsDefined(PlayerSettings.GetScriptingDefineSymbolsForGroup(group));

            TurboHttpBackgroundPluginGate.Register();
            var included = PluginImporter.GetImporters(BuildTarget.Android).Select(i => i.assetPath).ToArray();
            if (defined)
                CollectionAssert.Contains(included, Package().assetPath + "/" + AndroidLib);
            else
                CollectionAssert.DoesNotContain(included, Package().assetPath + "/" + AndroidLib);
        }

        [TestCase("Runtime/Unity/Mobile/Android/AndroidBackgroundWorkBridge.cs")]
        [TestCase("Runtime/Unity/Mobile/Android/AndroidBackgroundPluginJni.cs")]
        [TestCase("Runtime/Unity/Mobile/iOS/IosBackgroundTaskBridge.cs")]
        [TestCase("Runtime/Unity/Mobile/iOS/IosBackgroundTaskBindings.cs")]
        public void Bridges_NeverRouteThroughOrBlockOnTheMainThreadDispatcher(string file)
        {
            var source = File.ReadAllText(Path.Combine(Root, file));
            StringAssert.DoesNotContain("MainThreadDispatcher.Execute", source);
            StringAssert.DoesNotContain("GetAwaiter().GetResult()", source);
        }

        private static PluginImporter Importer(string relative)
        {
            var importer = AssetImporter.GetAtPath(Package().assetPath + "/" + relative) as PluginImporter;
            Assert.IsNotNull(importer, relative + " must import as a plugin");
            return importer;
        }
    }
}
