using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Tests;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class PluginRegistryTests
    {
        [Test]
        public void Register_InitializeOnce()
        {
            Task.Run(async () =>
            {
                using var client = CreateClient();
                var plugin = new MiddlewarePlugin("dup", new RecordingMiddleware("P"));

                await client.RegisterPluginAsync(plugin);

                await TestHelpers.AssertThrowsAsync<PluginException>(async () =>
                {
                    await client.RegisterPluginAsync(plugin);
                });
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void InitFailure_RollsBackContributions()
        {
            Task.Run(async () =>
            {
                var recorder = new List<string>();
                using var client = CreateClient(recorder);

                var plugin = new FailingInitPlugin(
                    "failing",
                    PluginCapabilities.MutateRequests | PluginCapabilities.MutateResponses,
                    new RecordingMiddleware("Failing", recorder));

                await TestHelpers.AssertThrowsAsync<PluginException>(async () =>
                {
                    await client.RegisterPluginAsync(plugin);
                });

                await client.Get("https://example.test/after-fail").SendAsync();

                Assert.IsEmpty(recorder);
                Assert.AreEqual(0, client.GetRegisteredPlugins().Count);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CapabilityGating_BlocksForbiddenAccess()
        {
            Task.Run(async () =>
            {
                using var client = CreateClient();
                var plugin = new MiddlewarePlugin(
                    "forbidden",
                    new RecordingMiddleware("Nope"),
                    capabilities: PluginCapabilities.None);

                await TestHelpers.AssertThrowsAsync<PluginException>(async () =>
                {
                    await client.RegisterPluginAsync(plugin);
                });
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ReadOnlyCapability_AllowsObserverMiddleware()
        {
            Task.Run(async () =>
            {
                var recorder = new List<string>();
                using var client = CreateClient(recorder);
                var plugin = new MiddlewarePlugin(
                    "observer",
                    new RecordingMiddleware("Observer", recorder),
                    capabilities: PluginCapabilities.ReadOnlyMonitoring);

                await client.RegisterPluginAsync(plugin);
                await client.Get("https://example.test/observe").SendAsync();

                CollectionAssert.AreEqual(new[] { "Observer:req", "Observer:res" }, recorder);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ReadOnlyCapability_BlocksMutationAtRuntime()
        {
            Task.Run(async () =>
            {
                using var client = CreateClient();
                var plugin = new MiddlewarePlugin(
                    "observer-mutating",
                    new MutatingRequestMiddleware(),
                    capabilities: PluginCapabilities.ObserveRequests);

                await client.RegisterPluginAsync(plugin);
                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    await client.Get("https://example.test/mutate").SendAsync();
                });

                Assert.AreEqual(UHttpErrorType.Unknown, ex.HttpError.Type);
                Assert.IsInstanceOf<PluginException>(ex.HttpError.InnerException);
                StringAssert.Contains("MutateRequests capability", ex.HttpError.Message);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Unregister_RemovesHooks()
        {
            Task.Run(async () =>
            {
                var recorder = new List<string>();
                using var client = CreateClient(recorder);
                var plugin = new MiddlewarePlugin("hook", new RecordingMiddleware("Hook", recorder));

                await client.RegisterPluginAsync(plugin);
                await client.Get("https://example.test/one").SendAsync();

                Assert.AreEqual(2, recorder.Count); // req + res

                await client.UnregisterPluginAsync("hook");
                await client.Get("https://example.test/two").SendAsync();

                Assert.AreEqual(2, recorder.Count);
                Assert.AreEqual(0, client.GetRegisteredPlugins().Count);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Ordering_Deterministic()
        {
            Task.Run(async () =>
            {
                var recorder = new List<string>();
                using var client = CreateClient(recorder);

                await client.RegisterPluginAsync(new MiddlewarePlugin("p1", new RecordingMiddleware("A", recorder)));
                await client.RegisterPluginAsync(new MiddlewarePlugin("p2", new RecordingMiddleware("B", recorder)));
                await client.RegisterPluginAsync(new MiddlewarePlugin("p3", new RecordingMiddleware("C", recorder)));

                await client.Get("https://example.test/order").SendAsync();

                CollectionAssert.AreEqual(
                    new[]
                    {
                        "A:req",
                        "B:req",
                        "C:req",
                        "C:res",
                        "B:res",
                        "A:res"
                    },
                    recorder);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ReadOnlyPlugin_NotBlamedForDownstreamMutation()
        {
            Task.Run(async () =>
            {
                using var client = CreateClient();

                await client.RegisterPluginAsync(new MiddlewarePlugin(
                    "observer",
                    new RecordingMiddleware("Observer"),
                    capabilities: PluginCapabilities.ObserveRequests));
                await client.RegisterPluginAsync(new MiddlewarePlugin(
                    "mutator",
                    new MutatingRequestMiddleware(),
                    capabilities: PluginCapabilities.MutateRequests | PluginCapabilities.MutateResponses));

                using var response = await client.Get("https://example.test/ordered-capabilities").SendAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void NoPlugins_FastPath()
        {
            Task.Run(async () =>
            {
                using var client = CreateClient();
                var response = await client.Get("https://example.test/no-plugins").SendAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(0, client.GetRegisteredPlugins().Count);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ShutdownTimeout_Handled()
        {
            Task.Run(async () =>
            {
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new MockTransport(),
                    DisposeTransport = true,
                    PluginShutdownTimeout = TimeSpan.FromMilliseconds(20)
                });

                await client.RegisterPluginAsync(new SlowShutdownPlugin("slow"));

                await TestHelpers.AssertThrowsAsync<PluginException>(async () =>
                {
                    await client.UnregisterPluginAsync("slow");
                });

                Assert.AreEqual(0, client.GetRegisteredPlugins().Count);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void OptionsSnapshot_IsDefensiveClone()
        {
            Task.Run(async () =>
            {
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new MockTransport(),
                    DisposeTransport = true,
                    DefaultTimeout = TimeSpan.FromSeconds(30)
                });

                var plugin = new OptionsSnapshotMutationPlugin("snapshot");
                await client.RegisterPluginAsync(plugin);

                Assert.IsFalse(plugin.MutationPersisted);
            }).GetAwaiter().GetResult();
        }

        private static UHttpClient CreateClient(List<string> recorder = null)
        {
            var transport = new MockTransport((request, context, ct) =>
            {
                return Task.FromResult(new UHttpResponse(
                    HttpStatusCode.OK,
                    new HttpHeaders(),
                    Array.Empty<byte>(),
                    context.Elapsed,
                    request));
            });

            return new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });
        }

        private sealed class MiddlewarePlugin : IHttpPlugin
        {
            private readonly IHttpMiddleware _middleware;
            private readonly PluginCapabilities _capabilities;

            public MiddlewarePlugin(
                string name,
                IHttpMiddleware middleware,
                PluginCapabilities capabilities = PluginCapabilities.MutateRequests | PluginCapabilities.MutateResponses)
            {
                Name = name;
                _middleware = middleware;
                _capabilities = capabilities;
            }

            public string Name { get; }
            public string Version => "1.0.0";
            public PluginCapabilities Capabilities => _capabilities;

            public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
            {
                context.RegisterMiddleware(_middleware);
                return default;
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken)
            {
                return default;
            }
        }

        private sealed class FailingInitPlugin : IHttpPlugin
        {
            private readonly PluginCapabilities _capabilities;
            private readonly IHttpMiddleware _middleware;

            public FailingInitPlugin(string name, PluginCapabilities capabilities, IHttpMiddleware middleware)
            {
                Name = name;
                _capabilities = capabilities;
                _middleware = middleware;
            }

            public string Name { get; }
            public string Version => "1.0.0";
            public PluginCapabilities Capabilities => _capabilities;

            public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
            {
                context.RegisterMiddleware(_middleware);
                throw new InvalidOperationException("init failed");
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken)
            {
                return default;
            }
        }

        private sealed class SlowShutdownPlugin : IHttpPlugin
        {
            public SlowShutdownPlugin(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Version => "1.0.0";
            public PluginCapabilities Capabilities => PluginCapabilities.MutateRequests | PluginCapabilities.MutateResponses;

            public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
            {
                context.RegisterMiddleware(new RecordingMiddleware("slow"));
                return default;
            }

            public async ValueTask ShutdownAsync(CancellationToken cancellationToken)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        private sealed class OptionsSnapshotMutationPlugin : IHttpPlugin
        {
            public OptionsSnapshotMutationPlugin(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Version => "1.0.0";
            public PluginCapabilities Capabilities => PluginCapabilities.None;
            public bool MutationPersisted { get; private set; }

            public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
            {
                var snapshot = context.OptionsSnapshot;
                snapshot.DefaultTimeout = TimeSpan.FromSeconds(1);
                MutationPersisted = context.OptionsSnapshot.DefaultTimeout == TimeSpan.FromSeconds(1);
                return default;
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken)
            {
                return default;
            }
        }

        private sealed class RecordingMiddleware : IHttpMiddleware
        {
            private readonly string _name;
            private readonly List<string> _recorder;

            public RecordingMiddleware(string name, List<string> recorder = null)
            {
                _name = name;
                _recorder = recorder;
            }

            public async ValueTask<UHttpResponse> InvokeAsync(
                UHttpRequest request,
                RequestContext context,
                HttpPipelineDelegate next,
                CancellationToken cancellationToken)
            {
                _recorder?.Add(_name + ":req");
                var response = await next(request, context, cancellationToken);
                _recorder?.Add(_name + ":res");
                return response;
            }
        }

        private sealed class MutatingRequestMiddleware : IHttpMiddleware
        {
            public ValueTask<UHttpResponse> InvokeAsync(
                UHttpRequest request,
                RequestContext context,
                HttpPipelineDelegate next,
                CancellationToken cancellationToken)
            {
                request.WithTimeout(TimeSpan.FromSeconds(42));
                context.UpdateRequest(request);
                return next(request, context, cancellationToken);
            }
        }
    }
}
