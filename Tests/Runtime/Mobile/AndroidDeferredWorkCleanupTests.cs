using System.Collections.Generic;
using NUnit.Framework;
using TurboHTTP.Unity.Mobile.Android;

namespace TurboHTTP.Tests.Mobile
{
    /// <summary>
    /// Deferred WorkManager jobs must not outlive the process that queued them (#940): the bridge
    /// initializes the plugin when it is created, which cancels every tagged job an earlier process
    /// left behind before this process enqueues anything, and it initializes the plugin before a
    /// cancel instead of skipping the cancel in a process that has not initialized it yet.
    /// </summary>
    [TestFixture]
    public class AndroidDeferredWorkCleanupTests
    {
        [Test]
        public void Constructor_InitializesThePluginBeforeAnyWorkIsEnqueued()
        {
            var plugin = new RecordingPlugin();
            var bridge = new AndroidBackgroundWorkBridge(new AndroidBackgroundWorkConfig(), plugin);

            CollectionAssert.AreEqual(new[] { "init" }, plugin.Calls, "startup must clear orphaned work before any enqueue");

            Assert.IsTrue(bridge.TryEnqueueDeferredWork("key"));
            CollectionAssert.AreEqual(new[] { "init", "init", "enqueue:key" }, plugin.Calls);
        }

        [Test]
        public void Constructor_SwallowsAPluginFailure()
        {
            var plugin = new RecordingPlugin { ThrowOnInit = true };

            Assert.DoesNotThrow(() => new AndroidBackgroundWorkBridge(new AndroidBackgroundWorkConfig(), plugin));
            CollectionAssert.AreEqual(new[] { "init" }, plugin.Calls);
        }

        [Test]
        public void TryMarkReplayComplete_InitializesThePluginBeforeCancelling()
        {
            var plugin = new RecordingPlugin { Initialized = false };
            var bridge = new AndroidBackgroundWorkBridge(new AndroidBackgroundWorkConfig(), plugin);
            plugin.Calls.Clear();
            plugin.Initialized = true;

            Assert.IsFalse(bridge.TryMarkReplayComplete("key"), "the key was never queued in this bridge");

            CollectionAssert.AreEqual(new[] { "init", "cancel:key" }, plugin.Calls);
        }

        [Test]
        public void TryMarkReplayComplete_SkipsTheCancelWhileThePluginIsUnavailable()
        {
            var plugin = new RecordingPlugin();
            var bridge = new AndroidBackgroundWorkBridge(new AndroidBackgroundWorkConfig(), plugin);
            Assert.IsTrue(bridge.TryEnqueueDeferredWork("key"));
            plugin.Calls.Clear();
            plugin.Initialized = false;

            Assert.IsTrue(bridge.TryMarkReplayComplete("key"), "the managed queue still releases the key");

            CollectionAssert.AreEqual(new[] { "init" }, plugin.Calls);
        }

        private sealed class RecordingPlugin : IAndroidBackgroundPlugin
        {
            public bool Initialized { get; set; } = true;
            public bool ThrowOnInit { get; set; }
            public List<string> Calls { get; } = new List<string>();

            public bool EnsureInitialized()
            {
                Calls.Add("init");
                if (ThrowOnInit)
                    throw new System.InvalidOperationException("JNI failure");
                return Initialized;
            }

            public bool BeginGuard(string scopeId, int graceMilliseconds)
            {
                Calls.Add("begin");
                return true;
            }

            public void EndGuard(string scopeId) => Calls.Add("end");

            public bool EnqueueDeferredWork(string dedupeKey)
            {
                Calls.Add("enqueue:" + dedupeKey);
                return true;
            }

            public void CancelDeferredWork(string dedupeKey) => Calls.Add("cancel:" + dedupeKey);
        }
    }
}
