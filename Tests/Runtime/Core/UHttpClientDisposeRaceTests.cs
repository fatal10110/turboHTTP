using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// Regression tests for #178: Dispose racing plugin register/unregister. Each test drives the
    /// interleaving with gates (no sleeps) and repeats it to shake out ordering assumptions.
    /// </summary>
    [TestFixture]
    public class UHttpClientDisposeRaceTests
    {
        private const int Iterations = 50;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        [Test]
        public void DisposeWhileInitializing_PluginIsShutDownAndNotRegistered()
        {
            AssertAsync.Run(async () =>
            {
                for (int i = 0; i < Iterations; i++)
                {
                    var client = CreateClient();
                    var plugin = new GatedPlugin("gated-" + i);
                    var register = Task.Run(() => client.RegisterPluginAsync(plugin));
                    await TestHelpers.AssertCompletesWithinAsync(plugin.InitializeEntered.Task, Timeout, "InitializeAsync not entered");

                    client.Dispose();
                    plugin.ReleaseInitialize();

                    var error = await CaptureAsync(register);
                    Assert.IsInstanceOf<ObjectDisposedException>(error, "iteration " + i);
                    Assert.AreEqual(1, plugin.ShutdownCount, "a plugin initialized after Dispose must be shut down (iteration " + i + ")");
                    Assert.AreEqual(0, client.GetRegisteredPlugins().Count, "iteration " + i);
                }
            });
        }

        [Test]
        public void DisposeRacingInitializeCompletion_EndsRegisteredAndShutDownOrRejected()
        {
            AssertAsync.Run(async () =>
            {
                for (int i = 0; i < Iterations; i++)
                {
                    var client = CreateClient();
                    var plugin = new GatedPlugin("race-" + i);
                    var register = Task.Run(() => client.RegisterPluginAsync(plugin));
                    await TestHelpers.AssertCompletesWithinAsync(plugin.InitializeEntered.Task, Timeout, "InitializeAsync not entered");

                    using var start = new ManualResetEventSlim(false);
                    var dispose = Task.Run(() =>
                    {
                        start.Wait();
                        client.Dispose();
                    });
                    var release = Task.Run(() =>
                    {
                        start.Wait();
                        plugin.ReleaseInitialize();
                    });
                    start.Set();
                    await Task.WhenAll(dispose, release).ConfigureAwait(false);

                    var error = await CaptureAsync(register);
                    Assert.IsTrue(error == null || error is ObjectDisposedException,
                        "only success or ObjectDisposedException is allowed, got " + error);

                    // Registered before Dispose: Dispose shuts it down in the background. Rejected: shut down inline.
                    await TestHelpers.AssertCompletesWithinAsync(plugin.ShutdownEntered.Task, Timeout, "plugin never shut down");
                    Assert.AreEqual(1, plugin.ShutdownCount, "iteration " + i);
                    Assert.AreEqual(0, client.GetRegisteredPlugins().Count, "iteration " + i);
                }
            });
        }

        [Test]
        public void DisposeWhileInitializing_InitializeFailureIsNotMasked()
        {
            AssertAsync.Run(async () =>
            {
                for (int i = 0; i < Iterations; i++)
                {
                    var client = CreateClient();
                    var plugin = new GatedPlugin("failing-" + i) { FailInitialize = true };
                    var register = Task.Run(() => client.RegisterPluginAsync(plugin));
                    await TestHelpers.AssertCompletesWithinAsync(plugin.InitializeEntered.Task, Timeout, "InitializeAsync not entered");

                    client.Dispose();
                    plugin.ReleaseInitialize();

                    var error = await CaptureAsync(register);
                    Assert.IsInstanceOf<PluginException>(error, "the plugin's own failure must surface (iteration " + i + ")");
                }
            });
        }

        [Test]
        public void DisposeWithRegistrationQueuedOnGate_QueuedCallFailsWithObjectDisposed()
        {
            AssertAsync.Run(async () =>
            {
                for (int i = 0; i < Iterations; i++)
                {
                    var client = CreateClient();
                    var first = new GatedPlugin("first-" + i);
                    var second = new GatedPlugin("second-" + i);
                    var firstRegister = Task.Run(() => client.RegisterPluginAsync(first));
                    await TestHelpers.AssertCompletesWithinAsync(first.InitializeEntered.Task, Timeout, "InitializeAsync not entered");

                    // Queued behind the lifecycle gate the first registration holds.
                    var secondRegister = client.RegisterPluginAsync(second);

                    client.Dispose();
                    first.ReleaseInitialize();
                    second.ReleaseInitialize();

                    var firstError = await CaptureAsync(firstRegister);
                    var secondError = await CaptureAsync(secondRegister);
                    Assert.IsInstanceOf<ObjectDisposedException>(firstError, "iteration " + i);
                    Assert.IsInstanceOf<ObjectDisposedException>(secondError, "iteration " + i);
                    Assert.AreEqual(0, second.InitializeCount, "a call queued before Dispose must not initialize");
                    Assert.AreEqual(0, client.GetRegisteredPlugins().Count, "iteration " + i);
                }
            });
        }

        [Test]
        public void DisposeWhileUnregistering_UnregisterCompletesWithItsOwnResult()
        {
            AssertAsync.Run(async () =>
            {
                for (int i = 0; i < Iterations; i++)
                {
                    var client = CreateClient();
                    var plugin = new GatedPlugin("unregister-" + i, gateShutdown: true);
                    plugin.ReleaseInitialize();
                    await client.RegisterPluginAsync(plugin);

                    var unregister = Task.Run(() => client.UnregisterPluginAsync(plugin.Name));
                    await TestHelpers.AssertCompletesWithinAsync(plugin.ShutdownEntered.Task, Timeout, "ShutdownAsync not entered");

                    client.Dispose();
                    plugin.ReleaseShutdown();

                    var error = await CaptureAsync(unregister);
                    Assert.IsNull(error, "unregister must not fail because Dispose ran meanwhile (iteration " + i + ")");
                    Assert.AreEqual(1, plugin.ShutdownCount, "Dispose must not shut the plugin down a second time");
                }
            });
        }

        private static UHttpClient CreateClient()
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = new MockTransport(),
                DisposeTransport = true,
                FollowRedirects = false
            });
        }

        private static async Task<Exception> CaptureAsync(Task task)
        {
            try
            {
                await TestHelpers.AssertCompletesWithinAsync(task, Timeout, "plugin lifecycle call did not complete");
                return null;
            }
            catch (AssertionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private sealed class GatedPlugin : IHttpPlugin
        {
            private readonly TaskCompletionSource<bool> _releaseInitialize = NewSource();
            private readonly TaskCompletionSource<bool> _releaseShutdown;
            private int _initializeCount;
            private int _shutdownCount;

            public GatedPlugin(string name, bool gateShutdown = false)
            {
                Name = name;
                _releaseShutdown = gateShutdown ? NewSource() : null;
            }

            public string Name { get; }
            public string Version => "1.0.0";
            public PluginCapabilities Capabilities => PluginCapabilities.ObserveRequests;
            public bool FailInitialize { get; set; }

            public TaskCompletionSource<bool> InitializeEntered { get; } = NewSource();
            public TaskCompletionSource<bool> ShutdownEntered { get; } = NewSource();
            public int InitializeCount => Volatile.Read(ref _initializeCount);
            public int ShutdownCount => Volatile.Read(ref _shutdownCount);

            public void ReleaseInitialize() => _releaseInitialize.TrySetResult(true);
            public void ReleaseShutdown() => _releaseShutdown?.TrySetResult(true);

            public async ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _initializeCount);
                InitializeEntered.TrySetResult(true);
                await _releaseInitialize.Task.ConfigureAwait(false);
                if (FailInitialize)
                    throw new InvalidOperationException("Simulated initialization failure.");
            }

            public async ValueTask ShutdownAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _shutdownCount);
                ShutdownEntered.TrySetResult(true);
                if (_releaseShutdown != null)
                    await _releaseShutdown.Task.ConfigureAwait(false);
            }

            private static TaskCompletionSource<bool> NewSource()
            {
                return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
