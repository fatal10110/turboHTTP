using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// Pins the 1.0 transport contract (#574): only TurboHTTP's own transports are supported (the
    /// raw-socket transport and the TurboHTTP.Testing transports), the request body hierarchy is
    /// closed explicitly, and the Testing transports keep working through the public
    /// <see cref="UHttpClientOptions.Transport"/> option with every body kind. Public custom
    /// transport support is tracked in #1237.
    /// </summary>
    [TestFixture]
    public sealed class BuiltInTransportContractTests
    {
        private static readonly byte[] Payload = Encoding.UTF8.GetBytes("payload");

        [Test]
        public void UHttpRequestBody_ConstructorsArePrivateProtected()
        {
            var constructors = typeof(UHttpRequestBody).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.IsNotEmpty(constructors);
            foreach (var constructor in constructors)
            {
                Assert.IsTrue(constructor.IsFamilyAndAssembly,
                    "UHttpRequestBody constructors must be private protected so that only TurboHTTP " +
                    "assemblies derive request bodies; found " + DescribeAccess(constructor));
            }
        }

        [Test]
        public void UHttpRequestBody_PublicImplementations_AreSealed()
        {
            var bodies = new[]
            {
                typeof(EmptyRequestBody),
                typeof(BufferedRequestBody),
                typeof(OwnedMemoryRequestBody),
                typeof(StreamRequestBody),
                typeof(FactoryRequestBody),
                typeof(TurboHTTP.Files.FileRequestBody),
            };

            foreach (var body in bodies)
            {
                Assert.IsTrue(body.IsSealed, body.Name + " must stay sealed");
                Assert.IsTrue(typeof(UHttpRequestBody).IsAssignableFrom(body));
            }
        }

        [Test]
        public void MockTransport_ThroughPublicOptions_SendsStreamAndFactoryBodies()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport();
                transport.EnqueueResponse(HttpStatusCode.Created);
                transport.EnqueueResponse(HttpStatusCode.Accepted);
                using var client = new UHttpClient(new UHttpClientOptions { Transport = transport });

                using (var streamed = await client.Post("https://example.test/stream")
                           .WithStreamBody(new MemoryStream(Payload, writable: false), Payload.Length)
                           .SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.Created, streamed.StatusCode);
                }

                using (var produced = await client.Post("https://example.test/factory")
                           .WithBodyFactory(
                               _ => new ValueTask<Stream>(new MemoryStream(Payload, writable: false)),
                               Payload.Length)
                           .SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.Accepted, produced.StatusCode);
                }

                Assert.AreEqual(2, transport.RequestCount);
                Assert.AreEqual(2, transport.CapturedRequests.Count);
                Assert.AreEqual("/stream", transport.CapturedRequests[0].Uri.AbsolutePath);
                Assert.AreEqual("/factory", transport.CapturedRequests[1].Uri.AbsolutePath);
            });
        }

        [Test]
        public void MockHttpServerTransport_ThroughPublicOptions_SendsStreamBody()
        {
            AssertAsync.Run(async () =>
            {
                var server = new MockHttpServer();
                server.When(HttpMethod.POST, "/upload").Respond(r => r.Status(HttpStatusCode.OK));
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = server.CreateTransport(),
                    DisposeTransport = true
                });

                using var response = await client.Post("https://example.test/upload")
                    .WithStreamBody(new MemoryStream(Payload, writable: false), Payload.Length)
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                server.AssertReceived("/upload", 1);
            });
        }

        [Test]
        public void RecordReplayTransport_Passthrough_ThroughPublicOptions_SendsFactoryBody()
        {
            AssertAsync.Run(async () =>
            {
                var inner = new MockTransport();
                inner.EnqueueResponse(HttpStatusCode.OK);
                var transport = new RecordReplayTransport(inner, new RecordReplayTransportOptions
                {
                    Mode = RecordReplayMode.Passthrough
                });
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                using var response = await client.Post("https://example.test/factory")
                    .WithBodyFactory(
                        (CancellationToken _) => new ValueTask<Stream>(new MemoryStream(Payload, writable: false)),
                        Payload.Length)
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(1, inner.RequestCount);
            });
        }

        private static string DescribeAccess(MethodBase method)
        {
            if (method.IsPublic) return "public";
            if (method.IsFamily) return "protected";
            if (method.IsFamilyOrAssembly) return "protected internal";
            if (method.IsAssembly) return "internal";
            if (method.IsPrivate) return "private";
            return "private protected";
        }
    }
}
