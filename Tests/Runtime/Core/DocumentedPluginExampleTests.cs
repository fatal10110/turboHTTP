using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Observability;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// The plugin example in Documentation~/APIReference.md must register: a plugin with
    /// <see cref="PluginCapabilities.None"/> cannot register interceptors (#592, #544).
    /// </summary>
    [TestFixture]
    public class DocumentedPluginExampleTests
    {
        [Test]
        public void DocumentedLoggerPlugin_RegistersAndObservesRequests()
        {
            AssertAsync.Run(async () =>
            {
                var lines = new List<string>();
                using var client = CreateClient();

                await client.RegisterPluginAsync(new CustomLoggerPlugin(PluginCapabilities.ObserveRequests, lines.Add));
                using var response = await client.Get("https://example.test/documented").SendBufferedAsync();

                Assert.IsNotNull(response);
                Assert.IsNotEmpty(lines, "the registered LoggingInterceptor must observe the request");
            });
        }

        [Test]
        public void LoggerPluginWithNoCapabilities_FailsToRegister()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient();

                var ex = await TestHelpers.AssertThrowsAsync<PluginException>(async () =>
                {
                    await client.RegisterPluginAsync(new CustomLoggerPlugin(PluginCapabilities.None, _ => { }));
                });

                Assert.IsNotNull(ex);
            });
        }

        private static UHttpClient CreateClient()
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = new MockTransport(),
                DisposeTransport = true
            });
        }

        // Mirrors CustomLoggerPlugin in Documentation~/APIReference.md (the capabilities are a parameter so the
        // negative case can show why the old example failed).
        private sealed class CustomLoggerPlugin : IHttpPlugin
        {
            private readonly System.Action<string> _log;

            public CustomLoggerPlugin(PluginCapabilities capabilities, System.Action<string> log)
            {
                Capabilities = capabilities;
                _log = log;
            }

            public string Name => "CustomLogger";
            public string Version => "1.0.0";
            public PluginCapabilities Capabilities { get; }

            public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
            {
                context.RegisterInterceptor(new LoggingInterceptor(_log));
                return default;
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken) => default;
        }
    }
}
