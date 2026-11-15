using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Unity.Mobile.Android;
using UnityEngine;
using UnityEngine.Scripting;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// The Android cleartext platform policy must be installed from a startup hook that managed
    /// stripping keeps on its own: the always-linked TurboHTTP.Complete bootstrap, not only a hook
    /// inside TurboHTTP.Unity that runs while some other link.xml rule happens to keep that
    /// assembly. Without the policy, <c>AllowCleartext = true</c> permits cleartext to every host
    /// (#875).
    /// </summary>
    [TestFixture]
    public class AndroidCleartextPolicyRootTests
    {
#if !ENABLE_IL2CPP
        private const byte CallOpcode = 0x28;
#endif

        private static readonly MethodInfo Hook = typeof(TurboHttpBootstrap).GetMethod(
            "RegisterPlatformPolicies", BindingFlags.Static | BindingFlags.NonPublic);

        private Func<string, bool> _saved;

        [SetUp]
        public void SetUp()
        {
            _saved = CleartextTrafficPolicy.PlatformPolicy;
            CleartextTrafficPolicy.PlatformPolicy = null;
        }

        [TearDown]
        public void TearDown()
        {
            CleartextTrafficPolicy.PlatformPolicy = _saved;
        }

        [Test]
        public void Bootstrap_InstallsThePolicyAtSubsystemRegistration_AndSurvivesStripping()
        {
            Assert.That(Hook, Is.Not.Null, "TurboHttpBootstrap.RegisterPlatformPolicies");

            var hook = Hook.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>();
            Assert.That(hook, Is.Not.Null, "[RuntimeInitializeOnLoadMethod] on the policy hook");
            Assert.That(hook.loadType, Is.EqualTo(RuntimeInitializeLoadType.SubsystemRegistration),
                "installed before any scene, BeforeSceneLoad or Awake code can send a request");
            // RuntimeInitializeOnLoadMethodAttribute derives from PreserveAttribute, so match the exact
            // type: GetCustomAttribute<PreserveAttribute>() would throw AmbiguousMatchException (#276).
            Assert.That(Hook.GetCustomAttributes<PreserveAttribute>().Any(a => a.GetType() == typeof(PreserveAttribute)),
                Is.True, "explicit [Preserve] on the method");
            Assert.That(typeof(TurboHttpBootstrap).GetCustomAttribute<PreserveAttribute>(), Is.Not.Null,
                "[Preserve] on the type");
            Assert.That(typeof(TurboHttpBootstrap).Assembly.GetCustomAttribute<AlwaysLinkAssemblyAttribute>(),
                Is.Not.Null, "[assembly: AlwaysLinkAssembly] so UnityLinker processes the unreferenced assembly");
        }

        [Test]
        public void Bootstrap_CallsTheAndroidPolicyRegistration()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // On Android players the hook's effect is observable: it alone installs the policy.
            Hook.Invoke(null, null);
            Assert.That(CleartextTrafficPolicy.PlatformPolicy, Is.Not.Null,
                "the preserved Complete bootstrap must call EnsureRegistered");
#elif ENABLE_IL2CPP
            // MethodBase.GetMethodBody is an unsupported icall on IL2CPP (NotSupportedException), and off
            // Android the hook installs nothing: the Editor checks the IL, Android players the effect (#875).
            Assert.Pass("checked by IL in the Editor and by behavior on Android players (#875)");
#else
            var il = Hook?.GetMethodBody()?.GetILAsByteArray();
            Assert.That(il, Is.Not.Null, "IL of TurboHttpBootstrap.RegisterPlatformPolicies");

            var ensure = typeof(AndroidCleartextTrafficPolicy).GetMethod(
                nameof(AndroidCleartextTrafficPolicy.EnsureRegistered), BindingFlags.Static | BindingFlags.Public, null,
                Type.EmptyTypes, null);
            Assert.That(ensure, Is.Not.Null, "public AndroidCleartextTrafficPolicy.EnsureRegistered()");
            Assert.That(Calls(Hook, il, ensure), Is.True,
                "the preserved Complete bootstrap must call EnsureRegistered: that call is what keeps the policy " +
                "(and TurboHTTP.Unity) in a stripped Android player");
#endif
        }

        [Test]
        public void AndroidPlayer_StartsWithThePlatformPolicyInstalled()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // SetUp saved the policy the player started with; every test restores it on TearDown.
            Assert.That(_saved, Is.Not.Null,
                "a stripped Android player must start with the platform policy the bootstrap installed");
#else
            Assert.Pass("Android players only (#875)");
#endif
        }

        [Test]
        public void EnsureRegistered_InstallsOnlyOnAndroidPlayers()
        {
            AndroidCleartextTrafficPolicy.EnsureRegistered();
            Hook.Invoke(null, null);

#if UNITY_ANDROID && !UNITY_EDITOR
            Assert.That(CleartextTrafficPolicy.PlatformPolicy, Is.Not.Null, "Android players get the platform policy");
#else
            Assert.That(CleartextTrafficPolicy.PlatformPolicy, Is.Null, "no platform restriction off Android players");
#endif
        }

        [Test]
        public void EnsureRegistered_NeverReplacesThePolicyTheAppInstalled()
        {
            Func<string, bool> appPolicy = host => host == "allowed.example";
            CleartextTrafficPolicy.PlatformPolicy = appPolicy;

            AndroidCleartextTrafficPolicy.EnsureRegistered();
            Hook.Invoke(null, null);

            Assert.That(CleartextTrafficPolicy.PlatformPolicy, Is.SameAs(appPolicy));
        }

        [Test]
        public void InstallPlatformPolicyIfUnset_InstallsOnlyIntoAnEmptySlot()
        {
            Func<string, bool> first = _ => false;
            Func<string, bool> second = _ => true;

            Assert.That(CleartextTrafficPolicy.InstallPlatformPolicyIfUnset(first), Is.True);
            Assert.That(CleartextTrafficPolicy.InstallPlatformPolicyIfUnset(second), Is.False);
            Assert.That(CleartextTrafficPolicy.PlatformPolicy, Is.SameAs(first));
            Assert.That(CleartextTrafficPolicy.IsPermitted("a.example", allowCleartext: true), Is.False,
                "the installed policy decides once AllowCleartext is true");
            Assert.Throws<ArgumentNullException>(() => CleartextTrafficPolicy.InstallPlatformPolicyIfUnset(null));
        }

#if !ENABLE_IL2CPP
        // Scans for a `call` whose method token resolves to callee; operand bytes that happen to be
        // 0x28 resolve to some other member or to nothing, so they cannot produce a false match.
        private static bool Calls(MethodInfo caller, byte[] il, MethodInfo callee)
        {
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != CallOpcode)
                    continue;

                int token = BitConverter.ToInt32(il, i + 1);
                int table = (token >> 24) & 0xFF;
                if (table != 0x06 && table != 0x0A)
                    continue;

                MethodBase resolved;
                try
                {
                    resolved = caller.Module.ResolveMethod(token);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (resolved != null && resolved.DeclaringType == callee.DeclaringType && resolved.Name == callee.Name)
                    return true;
            }

            return false;
        }
#endif
    }
}
