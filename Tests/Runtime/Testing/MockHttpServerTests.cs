using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Tests;

namespace TurboHTTP.Tests.Testing
{
    [TestFixture]
    public class MockHttpServerTests
    {
        [Test]
        public void RoutePriority_HighestWins()
        {
            Task.Run(async () =>
            {
                var server = new MockHttpServer();
                server.When(HttpMethod.GET, "/users/*")
                    .Priority(1)
                    .Respond(r => r.Status(HttpStatusCode.OK).Text("low"));

                server.When(HttpMethod.GET, "/users/{id}")
                    .Priority(5)
                    .Respond(r => r.Status(HttpStatusCode.OK).Text("high"));

                using var client = CreateClient(server);
                var response = await client.Get("https://example.test/users/42").SendAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("high", response.GetBodyAsString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void OneShotRoute_Expires()
        {
            Task.Run(async () =>
            {
                var server = new MockHttpServer();
                server.When(HttpMethod.GET, "/one-shot")
                    .Priority(10)
                    .Times(1)
                    .Respond(r => r.Status(HttpStatusCode.OK).Text("once"));
                server.When(HttpMethod.GET, "/one-shot")
                    .Priority(1)
                    .Respond(r => r.Status(HttpStatusCode.Gone).Text("expired"));

                using var client = CreateClient(server);

                var first = await client.Get("https://example.test/one-shot").SendAsync();
                var second = await client.Get("https://example.test/one-shot").SendAsync();

                Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
                Assert.AreEqual(HttpStatusCode.Gone, second.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SequenceResponses_Ordered()
        {
            Task.Run(async () =>
            {
                var server = new MockHttpServer();
                server.When(HttpMethod.GET, "/sequence")
                    .RespondSequence(
                        r => r.Status(200).Text("first"),
                        r => r.Status(201).Text("second"),
                        r => r.Status(202).Text("third"));

                using var client = CreateClient(server);

                var r1 = await client.Get("https://example.test/sequence").SendAsync();
                var r2 = await client.Get("https://example.test/sequence").SendAsync();
                var r3 = await client.Get("https://example.test/sequence").SendAsync();

                Assert.AreEqual(HttpStatusCode.OK, r1.StatusCode);
                Assert.AreEqual(HttpStatusCode.Created, r2.StatusCode);
                Assert.AreEqual(HttpStatusCode.Accepted, r3.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void HeaderBodyMatchers_FilterCorrectly()
        {
            Task.Run(async () =>
            {
                var server = new MockHttpServer();
                server.When(HttpMethod.POST, "/match")
                    .WithHeader("Authorization", value => value.StartsWith("Bearer ", StringComparison.Ordinal))
                    .WithBody(bytes => Encoding.UTF8.GetString(bytes).Contains("ok"))
                    .Respond(r => r.Status(HttpStatusCode.OK).Text("matched"));

                using var client = CreateClient(server);

                var matched = await client.Post("https://example.test/match")
                    .WithHeader("Authorization", "Bearer token")
                    .WithBody("{\"state\":\"ok\"}")
                    .SendAsync();

                var notMatched = await client.Post("https://example.test/match")
                    .WithHeader("Authorization", "Basic abc")
                    .WithBody("{\"state\":\"ok\"}")
                    .SendAsync();

                Assert.AreEqual(HttpStatusCode.OK, matched.StatusCode);
                Assert.AreEqual(HttpStatusCode.NotFound, notMatched.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void InjectedDelay_RespectsCancellation()
        {
            Task.Run(async () =>
            {
                var server = new MockHttpServer();
                server.When(HttpMethod.GET, "/slow")
                    .Respond(r => r.Status(HttpStatusCode.OK).Delay(TimeSpan.FromSeconds(5)));

                using var client = CreateClient(server);
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await client.Get("https://example.test/slow").SendAsync(cts.Token);
                });
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void HistoryBounded_OldestEvicted()
        {
            Task.Run(async () =>
            {
                var server = new MockHttpServer(historyCapacity: 2);
                server.When(HttpMethod.GET, "/a").Respond(r => r.Status(200));
                server.When(HttpMethod.GET, "/b").Respond(r => r.Status(200));
                server.When(HttpMethod.GET, "/c").Respond(r => r.Status(200));

                using var client = CreateClient(server);
                await client.Get("https://example.test/a").SendAsync();
                await client.Get("https://example.test/b").SendAsync();
                await client.Get("https://example.test/c").SendAsync();

                var history = server.GetHistory();
                Assert.AreEqual(2, history.Count);
                Assert.AreEqual("/b", history[0].Path);
                Assert.AreEqual("/c", history[1].Path);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ParallelRequests_NoStateCorruption()
        {
            Task.Run(async () =>
            {
                var server = new MockHttpServer();
                var lockObject = new object();
                var ids = new HashSet<string>();

                server.When(HttpMethod.GET, "/parallel/{id}")
                    .Respond(async ctx =>
                    {
                        await Task.Yield();
                        lock (lockObject)
                        {
                            ids.Add(ctx.Path);
                        }

                        return new MockResponse
                        {
                            StatusCode = 200,
                            Headers = new HttpHeaders(),
                            Body = Encoding.UTF8.GetBytes("ok")
                        };
                    });

                using var client = CreateClient(server);

                var tasks = new List<Task<UHttpResponse>>();
                for (int i = 0; i < 40; i++)
                {
                    tasks.Add(client.Get("https://example.test/parallel/" + i).SendAsync());
                }

                await Task.WhenAll(tasks);

                var history = server.GetHistory();
                Assert.AreEqual(40, history.Count);
                Assert.AreEqual(40, ids.Count);
                server.AssertNoUnexpectedRequests();
            }).GetAwaiter().GetResult();
        }

        private static UHttpClient CreateClient(MockHttpServer server)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = server.CreateTransport(),
                DisposeTransport = true
            });
        }
    }
}
