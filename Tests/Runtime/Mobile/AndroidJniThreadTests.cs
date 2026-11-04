using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Unity.Mobile.Android;

namespace TurboHTTP.Tests.Mobile
{
    /// <summary>
    /// TurboHTTP never leaves a pool thread attached to the Java VM (#328): JNI work runs on one
    /// attached thread that detaches when it stops, threads that stay attached for the process (the
    /// Unity main thread) run inline and are never detached. Fake JNI only; the API 23 emulator run
    /// is the device evidence.
    /// </summary>
    [TestFixture]
    public class AndroidJniThreadTests
    {
        private static readonly TimeSpan JoinBound = TimeSpan.FromSeconds(30);

        [Test]
        public void PoolThreadCallers_AreNeverAttached_AndTheJniThreadDetachesWhenItStops()
        {
            var jni = new FakeJni();
            var jniThread = new AndroidJniThread(jni, () => false);
            var workThreads = new ConcurrentBag<int>();
            var callerThreads = new ConcurrentBag<int>();

            RunConcurrently(4, () =>
            {
                callerThreads.Add(Environment.CurrentManagedThreadId);
                for (int i = 0; i < 5; i++)
                {
                    Assert.IsTrue(jniThread.Invoke(() =>
                    {
                        workThreads.Add(Environment.CurrentManagedThreadId);
                        return true;
                    }));
                }
            });

            jniThread.Dispose();

            int worker = workThreads.Distinct().Single();
            CollectionAssert.DoesNotContain(callerThreads, worker);
            foreach (int caller in callerThreads)
            {
                Assert.AreEqual(0, jni.Attaches(caller), "a caller thread was attached");
                Assert.AreEqual(0, jni.Detaches(caller), "a caller thread was detached");
            }

            Assert.AreEqual(1, jni.Attaches(worker));
            Assert.AreEqual(1, jni.Detaches(worker));
            Assert.AreEqual(20, jni.Pushes);
            Assert.AreEqual(20, jni.Pops);
            Assert.IsFalse(jni.AnyThreadStillAttached, "a thread exited attached");
        }

        [Test]
        public void PermanentlyAttachedCaller_RunsInline_AndIsNeverAttachedOrDetached()
        {
            var jni = new FakeJni();
            int mainThread = Environment.CurrentManagedThreadId;
            var jniThread = new AndroidJniThread(jni, () => Environment.CurrentManagedThreadId == mainThread);

            int ranOn = 0;
            Assert.IsTrue(jniThread.Invoke(() =>
            {
                ranOn = Environment.CurrentManagedThreadId;
                return true;
            }));
            Assert.AreEqual(mainThread, ranOn);
            Assert.AreEqual(0, jni.TotalAttaches, "inline work must not start or attach the JNI thread");

            // A pool caller is marshalled, and the main thread is still untouched afterwards.
            int poolRanOn = 0;
            RunConcurrently(1, () => jniThread.Invoke(() =>
            {
                poolRanOn = Environment.CurrentManagedThreadId;
                return true;
            }));
            jniThread.Dispose();

            Assert.AreNotEqual(mainThread, poolRanOn);
            Assert.AreEqual(0, jni.Attaches(mainThread));
            Assert.AreEqual(0, jni.Detaches(mainThread));
            Assert.AreEqual(1, jni.Detaches(poolRanOn));
            Assert.IsFalse(jni.AnyThreadStillAttached);
        }

        [Test]
        public void WorkThatThrows_IsRethrownToTheCaller_AndTheFrameIsStillReleased()
        {
            var jni = new FakeJni();
            var jniThread = new AndroidJniThread(jni, () => false);

            RunConcurrently(1, () =>
            {
                var error = Assert.Throws<InvalidOperationException>(
                    () => jniThread.Invoke(() => throw new InvalidOperationException("boom")));
                Assert.AreEqual("boom", error.Message);

                // The same attached thread keeps serving.
                Assert.IsTrue(jniThread.Invoke(() => true));
            });
            jniThread.Dispose();

            Assert.AreEqual(1, jni.TotalAttaches);
            Assert.AreEqual(1, jni.TotalDetaches);
            Assert.AreEqual(2, jni.Pushes);
            Assert.AreEqual(2, jni.Pops);
            Assert.AreEqual(2, jni.ExceptionClears);
            Assert.IsFalse(jni.AnyThreadStillAttached);
        }

        [Test]
        public void AttachFailure_FailsTheCall_WithoutDetaching_AndTheNextCallRetries()
        {
            var jni = new FakeJni { FailAttach = true };
            var jniThread = new AndroidJniThread(jni, () => false);
            bool ran = false;

            Assert.Throws<InvalidOperationException>(() => jniThread.Invoke(() => ran = true));
            Assert.IsFalse(ran);
            Assert.AreEqual(1, jni.TotalAttaches);
            Assert.AreEqual(0, jni.TotalDetaches, "a thread that failed to attach must not detach");
            Assert.AreEqual(0, jni.Pushes);

            jni.FailAttach = false;
            Assert.IsTrue(jniThread.Invoke(() => ran = true));
            jniThread.Dispose();

            Assert.IsTrue(ran);
            Assert.AreEqual(2, jni.TotalAttaches);
            Assert.AreEqual(1, jni.TotalDetaches);
            Assert.IsFalse(jni.AnyThreadStillAttached);
        }

        [Test]
        public void Invoke_AfterDispose_Throws()
        {
            var jniThread = new AndroidJniThread(new FakeJni(), () => false);
            jniThread.Dispose();

            Assert.Throws<ObjectDisposedException>(() => jniThread.Invoke(() => true));
        }

        [Test]
        public void AndroidCleartextTrafficPolicy_PoolThreadMisses_QueryJavaOnceOnTheJniThread()
        {
            var jni = new FakeJni();
            var jniThread = new AndroidJniThread(jni, () => false);
            var cache = AndroidCleartextTrafficPolicy.NewCache();
            var queried = new ConcurrentQueue<(string Host, int Thread)>();
            var callerThreads = new ConcurrentBag<int>();
            var answers = new ConcurrentBag<bool>();

            RunConcurrently(4, () =>
            {
                callerThreads.Add(Environment.CurrentManagedThreadId);
                answers.Add(AndroidCleartextTrafficPolicy.Resolve("[::1]", cache, jniThread, host =>
                {
                    queried.Enqueue((host, Environment.CurrentManagedThreadId));
                    return false;
                }));
            });
            jniThread.Dispose();

            Assert.AreEqual(1, queried.Count, "concurrent misses for one host must ask Java once");
            Assert.IsTrue(queried.TryPeek(out var query));
            Assert.AreEqual("::1", query.Host);
            CollectionAssert.DoesNotContain(callerThreads, query.Thread);
            CollectionAssert.AreEqual(new[] { false, false, false, false }, answers.ToArray());
            foreach (int caller in callerThreads)
                Assert.AreEqual(0, jni.Attaches(caller), "a transport thread was attached");

            Assert.AreEqual(1, jni.Attaches(query.Thread));
            Assert.AreEqual(1, jni.Detaches(query.Thread));
            Assert.IsFalse(jni.AnyThreadStillAttached);
        }

        [Test]
        public void AndroidCleartextTrafficPolicy_FullCache_KeepsCachingNewHosts()
        {
            var jniThread = new AndroidJniThread(new FakeJni(), () => false);
            var cache = AndroidCleartextTrafficPolicy.NewCache();
            int queries = 0;
            Func<string, bool> query = _ =>
            {
                queries++;
                return true;
            };

            for (int i = 0; i <= 64; i++)
                AndroidCleartextTrafficPolicy.Resolve("host" + i + ".test", cache, jniThread, query);
            Assert.AreEqual(65, queries);
            Assert.LessOrEqual(cache.Count, 64);

            // The 65th host was cached even though the cache was full when it was asked.
            AndroidCleartextTrafficPolicy.Resolve("HOST64.test", cache, jniThread, query);
            Assert.AreEqual(65, queries);
            jniThread.Dispose();
        }

        [Test]
        public void AndroidCleartextTrafficPolicy_QueryFailure_Propagates_AndIsNotCached()
        {
            var jni = new FakeJni();
            var jniThread = new AndroidJniThread(jni, () => false);
            var cache = AndroidCleartextTrafficPolicy.NewCache();

            Assert.Throws<InvalidOperationException>(() => AndroidCleartextTrafficPolicy.Resolve(
                "example.test", cache, jniThread, _ => throw new InvalidOperationException("jni")));
            jniThread.Dispose();

            Assert.AreEqual(0, cache.Count);
            Assert.AreEqual(jni.Pushes, jni.Pops);
            Assert.IsFalse(jni.AnyThreadStillAttached);
        }

        [Test]
        public void AndroidBackgroundPluginOnJniThread_RunsEveryCallOnTheJniThread()
        {
            var jni = new FakeJni();
            var jniThread = new AndroidJniThread(jni, () => false);
            var inner = new ThreadRecordingPlugin();
            var plugin = new AndroidBackgroundPluginOnJniThread(inner, jniThread);
            var callerThreads = new ConcurrentBag<int>();

            RunConcurrently(2, () =>
            {
                callerThreads.Add(Environment.CurrentManagedThreadId);
                Assert.IsTrue(plugin.EnsureInitialized());
                Assert.IsTrue(plugin.BeginGuard("scope", 1000));
                plugin.EndGuard("scope");
                Assert.IsFalse(plugin.EnqueueDeferredWork("work"));
                plugin.CancelDeferredWork("work");
            });
            jniThread.Dispose();

            var calls = inner.Calls.ToArray();
            Assert.AreEqual(10, calls.Length);
            CollectionAssert.AreEquivalent(
                new[] { "init", "begin:scope:1000", "end:scope", "enqueue:work", "cancel:work" },
                calls.Select(c => c.Call).Distinct().ToArray());
            int worker = calls.Select(c => c.Thread).Distinct().Single();
            CollectionAssert.DoesNotContain(callerThreads, worker);
            foreach (int caller in callerThreads)
                Assert.AreEqual(0, jni.Attaches(caller), "a bridge caller was attached");

            Assert.AreEqual(1, jni.Attaches(worker));
            Assert.AreEqual(1, jni.Detaches(worker));
            Assert.IsFalse(jni.AnyThreadStillAttached);
        }

        /// <summary>Runs <paramref name="body"/> on <paramref name="count"/> new threads released together.</summary>
        private static void RunConcurrently(int count, Action body)
        {
            var errors = new ConcurrentQueue<Exception>();
            using var release = new ManualResetEventSlim(false);
            var threads = new Thread[count];
            for (int i = 0; i < count; i++)
            {
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        release.Wait();
                        body();
                    }
                    catch (Exception e)
                    {
                        errors.Enqueue(e);
                    }
                })
                { IsBackground = true };
                threads[i].Start();
            }

            release.Set();
            foreach (var thread in threads)
                Assert.IsTrue(thread.Join(JoinBound), "a caller thread did not finish");

            if (errors.TryDequeue(out var error))
                throw new AggregateException(error);
        }

        private sealed class FakeJni : IAndroidJniAttachment
        {
            private readonly object _gate = new object();
            private readonly Dictionary<int, int> _attaches = new Dictionary<int, int>();
            private readonly Dictionary<int, int> _detaches = new Dictionary<int, int>();
            private readonly HashSet<int> _attached = new HashSet<int>();
            private int _pushes;
            private int _pops;
            private int _exceptionClears;
            private readonly List<string> _violations = new List<string>();
            private volatile bool _failAttach;

            public bool FailAttach
            {
                get => _failAttach;
                set => _failAttach = value;
            }

            public int TotalAttaches
            {
                get { lock (_gate) return _attaches.Values.Sum(); }
            }

            public int TotalDetaches
            {
                get { lock (_gate) return _detaches.Values.Sum(); }
            }

            public int Pushes
            {
                get { lock (_gate) return _pushes; }
            }

            public int Pops
            {
                get { lock (_gate) return _pops; }
            }

            public int ExceptionClears
            {
                get { lock (_gate) return _exceptionClears; }
            }

            public bool AnyThreadStillAttached
            {
                get
                {
                    lock (_gate)
                    {
                        // Also fails on misuse seen on the JNI thread, where an assertion would be lost.
                        CollectionAssert.IsEmpty(_violations);
                        return _attached.Count > 0;
                    }
                }
            }

            public int Attaches(int thread)
            {
                lock (_gate) return _attaches.TryGetValue(thread, out int n) ? n : 0;
            }

            public int Detaches(int thread)
            {
                lock (_gate) return _detaches.TryGetValue(thread, out int n) ? n : 0;
            }

            public int AttachCurrentThread()
            {
                int thread = Environment.CurrentManagedThreadId;
                lock (_gate)
                {
                    _attaches[thread] = Attaches(thread) + 1;
                    if (_failAttach)
                        return -1;

                    _attached.Add(thread);
                    return 0;
                }
            }

            public int DetachCurrentThread()
            {
                int thread = Environment.CurrentManagedThreadId;
                lock (_gate)
                {
                    _detaches[thread] = Detaches(thread) + 1;
                    if (!_attached.Remove(thread))
                        _violations.Add("detached a thread that was not attached");

                    return 0;
                }
            }

            public int PushLocalFrame(int capacity)
            {
                lock (_gate)
                {
                    if (!_attached.Contains(Environment.CurrentManagedThreadId))
                        _violations.Add("JNI on an unattached thread");

                    _pushes++;
                    return 0;
                }
            }

            public void PopLocalFrame()
            {
                lock (_gate) _pops++;
            }

            public void ExceptionClear()
            {
                lock (_gate) _exceptionClears++;
            }
        }

        private sealed class ThreadRecordingPlugin : IAndroidBackgroundPlugin
        {
            public ConcurrentQueue<(string Call, int Thread)> Calls { get; } = new ConcurrentQueue<(string, int)>();

            public bool EnsureInitialized() => Record("init", true);

            public bool BeginGuard(string scopeId, int graceMilliseconds) =>
                Record("begin:" + scopeId + ":" + graceMilliseconds, true);

            public void EndGuard(string scopeId) => Record("end:" + scopeId, true);

            public bool EnqueueDeferredWork(string dedupeKey) => Record("enqueue:" + dedupeKey, false);

            public void CancelDeferredWork(string dedupeKey) => Record("cancel:" + dedupeKey, true);

            private bool Record(string call, bool result)
            {
                Calls.Enqueue((call, Environment.CurrentManagedThreadId));
                return result;
            }
        }
    }
}
