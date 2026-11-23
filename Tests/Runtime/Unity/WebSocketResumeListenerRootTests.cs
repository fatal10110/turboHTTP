using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TurboHTTP.Unity.WebSocket;
using UnityEngine;
using UnityEngine.Scripting;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// The WebSocket resume listener lives in an assembly that an app using only TurboHTTP.WebSocket never
    /// references, so UnityLinker keeps it only through its own roots: <c>[assembly: AlwaysLinkAssembly]</c>
    /// and <see cref="PreserveAttribute"/> on the type, its startup hook and its player messages. Without
    /// them a stripped player drops the assembly and the keep-alive never gets the resume signal (#1280).
    /// </summary>
    [TestFixture]
    public class WebSocketResumeListenerRootTests
    {
        private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void Install_IsAnAfterSceneLoadHook_AndSurvivesStripping()
        {
            var install = typeof(WebSocketResumeListener).GetMethod(nameof(WebSocketResumeListener.Install),
                StaticMembers);
            Assert.That(install, Is.Not.Null, "WebSocketResumeListener.Install");

            var hook = install.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>();
            Assert.That(hook, Is.Not.Null, "[RuntimeInitializeOnLoadMethod] on Install");
            Assert.That(hook.loadType, Is.EqualTo(RuntimeInitializeLoadType.AfterSceneLoad),
                "installed once the first scene is loaded, so DontDestroyOnLoad has a scene to leave");
            Assert.That(HasExplicitPreserve(install), Is.True, "explicit [Preserve] on Install");
        }

        [Test]
        public void ListenerTypeAndPlayerMessages_ArePreserved()
        {
            Assert.That(typeof(WebSocketResumeListener).GetCustomAttribute<PreserveAttribute>(), Is.Not.Null,
                "[Preserve] on WebSocketResumeListener");

            var pause = typeof(WebSocketResumeListener).GetMethod(
                nameof(WebSocketResumeListener.OnApplicationPause), InstanceMembers);
            Assert.That(pause, Is.Not.Null, "WebSocketResumeListener.OnApplicationPause(bool)");
            Assert.That(HasExplicitPreserve(pause), Is.True, "[Preserve] on OnApplicationPause");

            var destroy = typeof(WebSocketResumeListener).GetMethod("OnDestroy", InstanceMembers);
            Assert.That(destroy, Is.Not.Null, "WebSocketResumeListener.OnDestroy");
            Assert.That(HasExplicitPreserve(destroy), Is.True, "[Preserve] on OnDestroy");
        }

        [Test]
        public void UnityWebSocketAssembly_IsAlwaysLinked()
        {
            Assert.That(typeof(WebSocketResumeListener).Assembly.GetCustomAttribute<AlwaysLinkAssemblyAttribute>(),
                Is.Not.Null,
                "[assembly: AlwaysLinkAssembly] so UnityLinker processes TurboHTTP.Unity.WebSocket when the app " +
                "references only TurboHTTP.WebSocket");
        }

        // RuntimeInitializeOnLoadMethodAttribute derives from PreserveAttribute, so match the exact type:
        // GetCustomAttribute<PreserveAttribute>() would throw AmbiguousMatchException on Install (#276).
        private static bool HasExplicitPreserve(MemberInfo member)
        {
            return member.GetCustomAttributes<PreserveAttribute>().Any(a => a.GetType() == typeof(PreserveAttribute));
        }
    }
}
