using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// Phase 15.6 reliability suite (#105): dispatcher invariants under a multi-thread flood and across
    /// the cancellation and domain-reload transitions. Work is enqueued and the transition driven in
    /// the same main-thread frame, before any drain, so every outcome is deterministic. The texture
    /// and audio bursts live in <see cref="UnityStressSuiteTests"/>; the leak-on-cancel and coroutine
    /// lifecycle invariants in <c>UnityObjectLeakOnCancelTests</c>, <c>StreamedAudioTempFileTests</c>
    /// and <c>CoroutineWrapperLifecycleTests</c>.
    /// </summary>
    public class UnityReliabilitySuiteTests
    {
        [UnityTest]
        public IEnumerator DispatcherFlood_CompletesWithoutDeadlock()
        {
            Assert.IsNotNull(MainThreadDispatcher.Instance);

            var ran = 0;
            var tasks = new Task[128];
            for (var i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() => MainThreadDispatcher.ExecuteAsync(() => { Interlocked.Increment(ref ran); }));
            }

            var all = Task.WhenAll(tasks);
            yield return new WaitUntil(() => all.IsCompleted);

            Assert.AreEqual(TaskStatus.RanToCompletion, all.Status, all.Exception?.ToString());
            Assert.AreEqual(tasks.Length, Volatile.Read(ref ran), "every flooded item ran exactly once");
        }

        [UnityTest]
        public IEnumerator CancelBeforeDrain_QueuedWorkNeverRuns_TheRestCompletes()
        {
            Assert.IsNotNull(MainThreadDispatcher.Instance);
            var defaults = MainThreadDispatcher.GetSettings();
            var settings = defaults.Clone();
            settings.AllowInlineExecutionOnMainThread = false;
            MainThreadDispatcher.Configure(settings);

            const int count = 32;
            var ran = new int[count];
            var tasks = new Task[count];
            var sources = new CancellationTokenSource[count];
            try
            {
                // Enqueued from the main thread: no drain runs before this frame ends.
                for (var i = 0; i < count; i++)
                {
                    var index = i;
                    sources[i] = new CancellationTokenSource();
                    tasks[i] = MainThreadDispatcher.ExecuteAsync(() => { ran[index]++; }, sources[i].Token);
                }

                for (var i = 0; i < count; i += 2)
                    sources[i].Cancel();

                var all = Task.WhenAll(tasks).ContinueWith(_ => { }, TaskScheduler.Default);
                yield return new WaitUntil(() => all.IsCompleted);

                for (var i = 0; i < count; i++)
                {
                    if (i % 2 == 0)
                    {
                        Assert.IsTrue(tasks[i].IsCanceled, "item " + i + " was cancelled before the drain");
                        Assert.AreEqual(0, ran[i], "a cancelled item never runs");
                    }
                    else
                    {
                        Assert.AreEqual(TaskStatus.RanToCompletion, tasks[i].Status, tasks[i].Exception?.ToString());
                        Assert.AreEqual(1, ran[i], "an item that was not cancelled runs once");
                    }
                }
            }
            finally
            {
                MainThreadDispatcher.Configure(defaults);
                for (var i = 0; i < count; i++)
                    sources[i]?.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ReloadTransition_FailsQueuedWork_RejectsNewWork_AndRecoversAfterReset()
        {
            var oldHost = MainThreadDispatcher.Instance.gameObject;
            var defaults = MainThreadDispatcher.GetSettings();
            var settings = defaults.Clone();
            settings.AllowInlineExecutionOnMainThread = false;
            MainThreadDispatcher.Configure(settings);

            var ran = 0;
            var queued = new Task[16];
            try
            {
                for (var i = 0; i < queued.Length; i++)
                    queued[i] = MainThreadDispatcher.ExecuteAsync(() => { Interlocked.Increment(ref ran); });

                // What AssemblyReloadEvents.beforeAssemblyReload and ExitingPlayMode do in the editor.
                MainThreadDispatcher.BeginShutdown(
                    "reliability suite: simulated domain reload",
                    MainThreadDispatcherLifecycleState.Reloading);

                Assert.AreEqual(MainThreadDispatcherLifecycleState.Reloading, MainThreadDispatcher.LifecycleState);
                Assert.Throws<InvalidOperationException>(
                    () => MainThreadDispatcher.ExecuteAsync(() => { Interlocked.Increment(ref ran); }),
                    "new work is rejected while reloading");

                var all = Task.WhenAll(queued).ContinueWith(_ => { }, TaskScheduler.Default);
                yield return new WaitUntil(() => all.IsCompleted);
                for (var i = 0; i < queued.Length; i++)
                    Assert.IsTrue(queued[i].IsCanceled, "queued item " + i + " is cancelled by the reload");
                Assert.AreEqual(0, Volatile.Read(ref ran), "no queued item ran after the reload began");

                // The SubsystemRegistration reset a real reload runs, then a fresh dispatcher.
                MainThreadDispatcher.ResetStaticState();
                Assert.AreEqual(MainThreadDispatcherLifecycleState.Uninitialized, MainThreadDispatcher.LifecycleState);
                UnityObjects.Destroy(oldHost);
                oldHost = null;

                Assert.IsNotNull(MainThreadDispatcher.Instance);
                Assert.AreEqual(MainThreadDispatcherLifecycleState.Ready, MainThreadDispatcher.LifecycleState);

                var after = Task.Run(() => MainThreadDispatcher.ExecuteAsync(() => { Interlocked.Increment(ref ran); }));
                yield return new WaitUntil(() => after.IsCompleted);
                Assert.AreEqual(TaskStatus.RanToCompletion, after.Status, after.Exception?.ToString());
                Assert.AreEqual(1, Volatile.Read(ref ran), "work dispatches again after the reset");
            }
            finally
            {
                MainThreadDispatcher.Configure(defaults);
                if (MainThreadDispatcher.LifecycleState != MainThreadDispatcherLifecycleState.Ready)
                {
                    // A failed assertion left the dispatcher terminal: give later tests a live one.
                    MainThreadDispatcher.ResetStaticState();
                    if (oldHost != null)
                        UnityObjects.Destroy(oldHost);
                    _ = MainThreadDispatcher.Instance;
                }
            }
        }
    }
}
