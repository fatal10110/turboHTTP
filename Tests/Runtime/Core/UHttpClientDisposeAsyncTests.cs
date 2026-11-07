using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// Regression tests for #279: <see cref="UHttpClient.DisposeAsync"/> awaits every plugin
    /// shutdown (bounded by <see cref="UHttpClientOptions.PluginShutdownTimeout"/>) before it
    /// disposes owned interceptors and the owned transport. Ordering is driven by gates, not sleeps.
    /// </summary>
    [TestFixture]
    public class UHttpClientDisposeAsyncTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        [Test]
        public void DisposeAsync_WaitsForGatedPluginShutdown_BeforeDisposingInterceptorsAndTransport()
        {
            AssertAsync.Run(async () =>
            {
                var events = new EventLog();
                var client = CreateClient(events);
                var plugin = new GatedPlugin("gated", events, PluginShutdownMode.Gated);
                await client.RegisterPluginAsync(plugin);

                var dispose = client.DisposeAsync().AsTask();
                await TestHelpers.AssertCompletesWithinAsync(plugin.ShutdownEntered.Task, Timeout, "ShutdownAsync not entered");

                Assert.IsFalse(dispose.IsCompleted, "DisposeAsync must not complete while a plugin is still shutting down");
                CollectionAssert.AreEqual(new[] { "shutdown-start:gated" }, events.Snapshot(),
                    "no interceptor or transport may be disposed before plugin shutdown completed");

                plugin.ReleaseShutdown();
                await TestHelpers.AssertCompletesWithinAsync(dispose, Timeout, "DisposeAsync did not complete");

                CollectionAssert.AreEqual(
                    new[] { "shutdown-start:gated", "shutdown-end:gated", "interceptor-disposed", "transport-disposed" },
                    events.Snapshot());
            });
        }

        [Test]
        public void DisposeAsync_ShutsPluginsDownOneAtATimeInReverseRegistrationOrder()
        {
            AssertAsync.Run(async () =>
            {
                var events = new EventLog();
                var client = CreateClient(events);
                var first = new GatedPlugin("first", events, PluginShutdownMode.Gated);
                var second = new GatedPlugin("second", events, PluginShutdownMode.Gated);
                await client.RegisterPluginAsync(first);
                await client.RegisterPluginAsync(second);

                var dispose = client.DisposeAsync().AsTask();
                await TestHelpers.AssertCompletesWithinAsync(second.ShutdownEntered.Task, Timeout, "second plugin shutdown not entered");
                Assert.AreEqual(0, first.ShutdownCount, "the earlier plugin is shut down only after the later one finished");

                second.ReleaseShutdown();
                await TestHelpers.AssertCompletesWithinAsync(first.ShutdownEntered.Task, Timeout, "first plugin shutdown not entered");
                Assert.IsFalse(dispose.IsCompleted);
                CollectionAssert.DoesNotContain(events.Snapshot(), "transport-disposed");

                first.ReleaseShutdown();
                await TestHelpers.AssertCompletesWithinAsync(dispose, Timeout, "DisposeAsync did not complete");

                CollectionAssert.AreEqual(
                    new[]
                    {
                        "shutdown-start:second", "shutdown-end:second",
                        "shutdown-start:first", "shutdown-end:first",
                        "interceptor-disposed", "transport-disposed"
                    },
                    events.Snapshot());
            });
        }

        [Test]
        public void DisposeAsync_PluginShutdownTimesOut_CompletesAndDisposesComponents()
        {
            AssertAsync.Run(async () =>
            {
                var events = new EventLog();
                var client = CreateClient(events, TimeSpan.FromMilliseconds(50));
                // One plugin honours the shutdown token, the other ignores it: the client must stop
                // waiting for both at the timeout.
                var cooperative = new GatedPlugin("cooperative", events, PluginShutdownMode.UntilCancelled);
                var ignoring = new GatedPlugin("ignoring", events, PluginShutdownMode.Gated);
                await client.RegisterPluginAsync(cooperative);
                await client.RegisterPluginAsync(ignoring);

                await TestHelpers.AssertCompletesWithinAsync(client.DisposeAsync().AsTask(), Timeout,
                    "DisposeAsync must be bounded by PluginShutdownTimeout");

                await TestHelpers.AssertCompletesWithinAsync(cooperative.ShutdownCancelled.Task, Timeout,
                    "the shutdown token must be cancelled at the timeout");
                Assert.AreEqual(1, ignoring.ShutdownCount);
                CollectionAssert.AreEqual(
                    new[] { "shutdown-start:ignoring", "shutdown-start:cooperative", "interceptor-disposed", "transport-disposed" },
                    events.Snapshot());

                ignoring.ReleaseShutdown();
            });
        }

        [Test]
        public void DisposeAsync_PluginShutdownThrows_DoesNotThrowAndStillDisposesComponents()
        {
            AssertAsync.Run(async () =>
            {
                var events = new EventLog();
                var client = CreateClient(events);
                var throwsInline = new GatedPlugin("inline", events, PluginShutdownMode.ThrowInline);
                var throwsLater = new GatedPlugin("later", events, PluginShutdownMode.ThrowAfterYield);
                await client.RegisterPluginAsync(throwsInline);
                await client.RegisterPluginAsync(throwsLater);

                await TestHelpers.AssertCompletesWithinAsync(client.DisposeAsync().AsTask(), Timeout, "DisposeAsync did not complete");

                CollectionAssert.AreEqual(
                    new[] { "shutdown-start:later", "shutdown-start:inline", "interceptor-disposed", "transport-disposed" },
                    events.Snapshot());
            });
        }

        [Test]
        public void DisposeThenDisposeAsync_DisposesOnce()
        {
            AssertAsync.Run(async () =>
            {
                var events = new EventLog();
                var client = CreateClient(events);
                var plugin = new GatedPlugin("plugin", events, PluginShutdownMode.Immediate);
                await client.RegisterPluginAsync(plugin);

                client.Dispose();
                var second = client.DisposeAsync();
                Assert.IsTrue(second.IsCompleted, "DisposeAsync after Dispose is a no-op");
                await second;
                client.Dispose();

                Assert.AreEqual(1, plugin.ShutdownCount);
                Assert.AreEqual(1, events.Count("interceptor-disposed"));
                Assert.AreEqual(1, events.Count("transport-disposed"));
            });
        }

        [Test]
        public void DisposeAsyncThenDispose_DisposesOnceAndRejectsRegistration()
        {
            AssertAsync.Run(async () =>
            {
                var events = new EventLog();
                var client = CreateClient(events);
                var plugin = new GatedPlugin("plugin", events, PluginShutdownMode.Gated);
                await client.RegisterPluginAsync(plugin);

                var dispose = client.DisposeAsync().AsTask();
                await TestHelpers.AssertCompletesWithinAsync(plugin.ShutdownEntered.Task, Timeout, "ShutdownAsync not entered");

                client.Dispose();
                Assert.IsTrue(client.DisposeAsync().IsCompleted, "a second DisposeAsync is a no-op");
                Assert.AreEqual(0, events.Count("transport-disposed"), "Dispose racing DisposeAsync must not dispose early");

                var late = new GatedPlugin("late", events, PluginShutdownMode.Immediate);
                Exception registerError = null;
                try
                {
                    await client.RegisterPluginAsync(late);
                }
                catch (Exception ex)
                {
                    registerError = ex;
                }

                Assert.IsInstanceOf<ObjectDisposedException>(registerError);

                plugin.ReleaseShutdown();
                await TestHelpers.AssertCompletesWithinAsync(dispose, Timeout, "DisposeAsync did not complete");

                Assert.AreEqual(1, plugin.ShutdownCount);
                Assert.AreEqual(1, events.Count("interceptor-disposed"));
                Assert.AreEqual(1, events.Count("transport-disposed"));
            });
        }

        private static UHttpClient CreateClient(EventLog events, TimeSpan? pluginShutdownTimeout = null)
        {
            var options = new UHttpClientOptions
            {
                Transport = new RecordingTransport(events),
                DisposeTransport = true,
                DisposeInterceptors = true,
                FollowRedirects = false
            };
            options.Interceptors.Add(new RecordingInterceptor(events));
            if (pluginShutdownTimeout.HasValue)
                options.PluginShutdownTimeout = pluginShutdownTimeout.Value;
            return new UHttpClient(options);
        }

        private sealed class EventLog
        {
            private readonly object _lock = new object();
            private readonly List<string> _events = new List<string>();

            public void Add(string value)
            {
                lock (_lock)
                    _events.Add(value);
            }

            public string[] Snapshot()
            {
                lock (_lock)
                    return _events.ToArray();
            }

            public int Count(string value)
            {
                lock (_lock)
                    return _events.FindAll(e => e == value).Count;
            }
        }

        private sealed class RecordingTransport : IHttpTransport
        {
            private readonly EventLog _events;
            private readonly MockTransport _inner = new MockTransport();

            public RecordingTransport(EventLog events)
            {
                _events = events;
            }

            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                return _inner.DispatchAsync(request, handler, context, cancellationToken);
            }

            public void Dispose()
            {
                _events.Add("transport-disposed");
                _inner.Dispose();
            }
        }

        private sealed class RecordingInterceptor : IHttpInterceptor, IDisposable
        {
            private readonly EventLog _events;

            public RecordingInterceptor(EventLog events)
            {
                _events = events;
            }

            public DispatchFunc Wrap(DispatchFunc next) => next;

            public void Dispose() => _events.Add("interceptor-disposed");
        }

        private enum PluginShutdownMode
        {
            Immediate,
            Gated,
            UntilCancelled,
            ThrowInline,
            ThrowAfterYield
        }

        private sealed class GatedPlugin : IHttpPlugin
        {
            private readonly EventLog _events;
            private readonly PluginShutdownMode _mode;
            private readonly TaskCompletionSource<bool> _releaseShutdown = NewSource();
            private int _shutdownCount;

            public GatedPlugin(string name, EventLog events, PluginShutdownMode mode)
            {
                Name = name;
                _events = events;
                _mode = mode;
            }

            public string Name { get; }
            public string Version => "1.0.0";
            public PluginCapabilities Capabilities => PluginCapabilities.ObserveRequests;

            public TaskCompletionSource<bool> ShutdownEntered { get; } = NewSource();
            public int ShutdownCount => Volatile.Read(ref _shutdownCount);
            public TaskCompletionSource<bool> ShutdownCancelled { get; } = NewSource();

            public void ReleaseShutdown() => _releaseShutdown.TrySetResult(true);

            public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
            {
                return default;
            }

            public async ValueTask ShutdownAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _shutdownCount);
                _events.Add("shutdown-start:" + Name);
                ShutdownEntered.TrySetResult(true);

                switch (_mode)
                {
                    case PluginShutdownMode.Gated:
                        await _releaseShutdown.Task.ConfigureAwait(false);
                        break;
                    case PluginShutdownMode.UntilCancelled:
                        using (cancellationToken.Register(() => ShutdownCancelled.TrySetResult(true)))
                        {
                            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                        }

                        break;
                    case PluginShutdownMode.ThrowInline:
                        throw new InvalidOperationException("Simulated shutdown failure.");
                    case PluginShutdownMode.ThrowAfterYield:
                        await Task.Yield();
                        throw new InvalidOperationException("Simulated asynchronous shutdown failure.");
                }

                _events.Add("shutdown-end:" + Name);
            }

            private static TaskCompletionSource<bool> NewSource()
            {
                return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
