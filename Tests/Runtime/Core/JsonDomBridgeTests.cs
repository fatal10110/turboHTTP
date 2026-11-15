using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.JSON.Lite;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// #1034: internal consumers that walk the JSON tree (record/replay files, OAuth token responses)
    /// parse through the bridge's document model, which is LiteJson whatever
    /// <see cref="JsonSerializer.Default"/> is. A custom default only affects typed user payloads.
    /// </summary>
    [TestFixture]
    public class JsonDomBridgeTests
    {
        private const string Key = "provider|client|scope";

        private string _path;

        [SetUp]
        public void SetUp()
        {
            JsonModuleRegistration.EnsureRegistered();
            _path = Path.Combine(TestTempPath.Root, "turbohttp-dom-bridge-" + Guid.NewGuid().ToString("N") + ".json");
        }

        [TearDown]
        public void TearDown()
        {
            JsonSerializer.ResetToDefault();
            JsonModuleRegistration.EnsureRegistered();
            if (File.Exists(_path))
                File.Delete(_path);
        }

        [Test]
        public void RecordReplay_RoundTrips_UnderNonLiteDefaultSerializer()
        {
            JsonSerializer.SetDefault(new ForeignTreeSerializer());

            var responseHeaders = new HttpHeaders();
            responseHeaders.Set("Content-Type", "application/json");
            var inner = new MockTransport();
            inner.EnqueueResponse(
                HttpStatusCode.OK,
                responseHeaders,
                Encoding.UTF8.GetBytes("{\"access_token\":\"SECRET-1034\",\"token_type\":\"Bearer\"}"));

            using (var record = new RecordReplayTransport(
                inner,
                new RecordReplayTransportOptions { Mode = RecordReplayMode.Record, RecordingPath = _path }))
            {
                Assert.IsNull(Dispatch(record, NewRequest()).Error);
                record.SaveRecordings();
            }

            using var replay = new RecordReplayTransport(
                null,
                new RecordReplayTransportOptions { Mode = RecordReplayMode.Replay, RecordingPath = _path });
            var replayed = Dispatch(replay, NewRequest());

            Assert.IsNull(replayed.Error);
            Assert.That(replayed.Body, Does.Not.Contain("SECRET-1034"));
            StringAssert.Contains("\"token_type\":\"Bearer\"", replayed.Body);
        }

        [Test]
        public void RecordReplay_EntriesOfUnexpectedShape_Throws()
        {
            File.WriteAllText(_path, "{\"Version\":2,\"Entries\":{\"0\":{}}}");

            var ex = Assert.Throws<InvalidOperationException>(() => new RecordReplayTransport(
                null,
                new RecordReplayTransportOptions { Mode = RecordReplayMode.Replay, RecordingPath = _path }));
            StringAssert.Contains("Entries", ex.Message);
        }

        [Test]
        public void OAuthTokenParsing_Works_UnderNonLiteDefaultSerializer()
        {
            JsonSerializer.SetDefault(new ForeignTreeSerializer());

            AssertAsync.Run(async () =>
            {
                var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("Content-Type", "application/json");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        headers,
                        Encoding.UTF8.GetBytes(
                            "{\"access_token\":\"access-new\",\"token_type\":\"Bearer\",\"expires_in\":1200}"),
                        ctx.Elapsed,
                        req));
                });
                using var client = new UHttpClient(new UHttpClientOptions { Transport = transport, DisposeTransport = true });
                using var store = new InMemoryTokenStore();
                await store.SetAsync(Key, new OAuthToken("stale", clock.AddMinutes(-10), "refresh-1"), CancellationToken.None);
                using var oauth = new OAuthClient(client, store, () => clock);

                var token = await oauth.GetValidTokenAsync(Key, NewRefreshRequest(), CancellationToken.None);

                Assert.AreEqual("access-new", token.AccessToken);
                Assert.AreEqual(clock.AddSeconds(1200), token.ExpiresAtUtc);
            });
        }

        [Test]
        public void TypedPayloads_StillFollowTheDefaultSerializer()
        {
            var serializer = new ForeignTreeSerializer();
            JsonSerializer.SetDefault(serializer);

            var parsed = ProjectJsonBridge.Deserialize("{\"a\":1}", typeof(Dictionary<string, object>), "test");

            Assert.That(parsed, Is.InstanceOf<ForeignNode>());
            Assert.That(ProjectJsonBridge.DeserializeDom("{\"a\":1}", "test"), Is.InstanceOf<Dictionary<string, object>>());
        }

        private static UHttpRequest NewRequest()
        {
            return new UHttpRequest(HttpMethod.GET, new Uri("https://api.example.test/token-info"));
        }

        private static OAuthRefreshRequest NewRefreshRequest()
        {
            return new OAuthRefreshRequest
            {
                Config = new OAuthConfig
                {
                    ClientId = "client-1",
                    AuthorizationEndpoint = new Uri("https://auth.example.com/authorize"),
                    TokenEndpoint = new Uri("https://auth.example.com/token"),
                    RedirectUri = new Uri("myapp://oauth/callback"),
                    Scopes = new[] { "openid" },
                    UsePkce = true
                },
                Scope = "openid"
            };
        }

        private static (UHttpException Error, string Body) Dispatch(RecordReplayTransport transport, UHttpRequest request)
        {
            var handler = new BodyCollector();
            Task.Run(async () =>
                await transport.DispatchAsync(request, handler, new RequestContext(request), CancellationToken.None))
                .GetAwaiter().GetResult();
            return (handler.Error, handler.Body);
        }

        private sealed class ForeignNode
        {
        }

        // Stands in for a Newtonsoft/System.Text.Json adapter: untyped JSON comes back as its own node
        // type, never as LiteJson's Dictionary<string, object> / List<object>.
        private sealed class ForeignTreeSerializer : IJsonSerializer
        {
            private static IJsonSerializer Inner => LiteJsonSerializer.Instance;

            public string Serialize<T>(T value) => "{}";
            public string Serialize(object value, Type type) => "{}";
            public T Deserialize<T>(string json) => (T)Deserialize(json, typeof(T));

            public object Deserialize(string json, Type type)
            {
                if (type == typeof(object) || type == typeof(Dictionary<string, object>) || type == typeof(List<object>))
                    return new ForeignNode();
                return Inner.Deserialize(json, type);
            }

            public void Serialize<T>(T value, IBufferWriter<byte> output) => Inner.Serialize(value, output);
            public T Deserialize<T>(ReadOnlySequence<byte> input) => Inner.Deserialize<T>(input);
        }

        private sealed class BodyCollector : IHttpHandler
        {
            public UHttpException Error { get; private set; }
            public string Body { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                if (body == null)
                    return;

                using var bytes = new MemoryStream();
                var buffer = new byte[4096];
                int read;
                while ((read = await body.ReadAsync(buffer, CancellationToken.None)) > 0)
                    bytes.Write(buffer, 0, read);

                await body.DisposeAsync();
                Body = Encoding.UTF8.GetString(bytes.ToArray());
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Error = error;
            }
        }
    }
}
