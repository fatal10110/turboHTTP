using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Testing;
using UnityEngine;
using UnityEngine.Scripting;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// Modules that reference only Core reach TurboHTTP.JSON through the registered
    /// <see cref="IJsonBridge"/>, not through a by-name type lookup (#67, #186). Without a
    /// registration every JSON path fails with an error that names TurboHTTP.JSON.
    /// </summary>
    [TestFixture]
    public class JsonBridgeRegistrationTests
    {
        private const string Key = "provider|client|scope";
        private const string TokenEndpoint = "https://auth.example.com/token";

        [TearDown]
        public void TearDown()
        {
            // The mock, OAuth and coroutine tests in the same run depend on the default registration.
            JsonModuleRegistration.EnsureRegistered();
        }

        [Test]
        public void Bootstrap_RegistersJsonBridge_AtSubsystemRegistration_AndSurvivesStripping()
        {
            var method = typeof(TurboHttpBootstrap).GetMethod(
                "RegisterJsonBridge", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "TurboHttpBootstrap.RegisterJsonBridge");

            var hook = method.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>();
            Assert.That(hook, Is.Not.Null, "[RuntimeInitializeOnLoadMethod] on the JSON registration");
            Assert.That(hook.loadType, Is.EqualTo(RuntimeInitializeLoadType.SubsystemRegistration));
            Assert.That(method.GetCustomAttribute<PreserveAttribute>(), Is.Not.Null, "[Preserve]");

            ProjectJsonBridge.Reset();
            Assert.That(ProjectJsonBridge.IsRegistered, Is.False);

            TurboHttpBootstrap.RegisterJsonBridge();

            Assert.That(ProjectJsonBridge.IsRegistered, Is.True);
            var parsed = ProjectJsonBridge.Deserialize(
                "{\"a\":\"b\"}", typeof(Dictionary<string, object>), "bootstrap test") as Dictionary<string, object>;
            Assert.That(parsed, Is.Not.Null);
            Assert.That(parsed["a"], Is.EqualTo("b"));
        }

        [Test]
        public void NoBridge_DeserializeAndSerialize_ThrowNamingJsonModule()
        {
            ProjectJsonBridge.Reset();

            var deserialize = Assert.Throws<InvalidOperationException>(() =>
                ProjectJsonBridge.Deserialize("{}", typeof(Dictionary<string, object>), "unit test parsing"));
            StringAssert.Contains("TurboHTTP.JSON", deserialize.Message);
            StringAssert.Contains("unit test parsing", deserialize.Message);

            var serialize = Assert.Throws<InvalidOperationException>(() =>
                ProjectJsonBridge.Serialize(new Dictionary<string, object>(), typeof(Dictionary<string, object>), "unit test writing"));
            StringAssert.Contains("TurboHTTP.JSON", serialize.Message);

            var required = Assert.Throws<InvalidOperationException>(() => ProjectJsonBridge.GetRequired("feature x"));
            StringAssert.Contains("TurboHTTP.JSON", required.Message);
            StringAssert.Contains("feature x", required.Message);
        }

        [Test]
        public void RegisteredBridge_IsUsed_AndItsFailuresAreWrapped()
        {
            var bridge = new RecordingBridge();
            ProjectJsonBridge.Register(bridge);

            Assert.That(ProjectJsonBridge.GetRequired("x"), Is.SameAs(bridge));
            Assert.That(ProjectJsonBridge.Deserialize("[1]", typeof(int[]), "x"), Is.EqualTo("deserialized"));
            Assert.That(bridge.LastJson, Is.EqualTo("[1]"));
            Assert.That(bridge.LastType, Is.EqualTo(typeof(int[])));
            Assert.That(ProjectJsonBridge.Serialize(5, typeof(int), "x"), Is.EqualTo("serialized"));
            Assert.That(bridge.LastType, Is.EqualTo(typeof(int)));

            bridge.Fail = true;
            var ex = Assert.Throws<InvalidOperationException>(() =>
                ProjectJsonBridge.Deserialize("{", typeof(object), "broken payload"));
            Assert.That(ex.InnerException, Is.InstanceOf<FormatException>());
            StringAssert.Contains("broken payload", ex.Message);

            Assert.Throws<ArgumentNullException>(() => ProjectJsonBridge.Register(null));
        }

        [Test]
        public void MockTransport_EnqueueJsonResponse_WithoutBridge_ThrowsNamingJsonModule()
        {
            ProjectJsonBridge.Reset();
            using var transport = new MockTransport();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                transport.EnqueueJsonResponse(new Dictionary<string, object> { { "a", "b" } }));
            StringAssert.Contains("TurboHTTP.JSON", ex.Message);
        }

        [Test]
        public void OAuthTokenParsing_WithJson_ReturnsAccessRefreshAndExpiry()
        {
            AssertAsync.Run(async () =>
            {
                var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                using var oauth = await NewOAuthClientAsync(clock,
                    "{\"access_token\":\"access-new\",\"refresh_token\":\"refresh-new\"," +
                    "\"token_type\":\"Bearer\",\"expires_in\":1200}");

                var token = await oauth.Client.GetValidTokenAsync(Key, NewRefreshRequest(), CancellationToken.None);

                Assert.AreEqual("access-new", token.AccessToken);
                Assert.AreEqual("refresh-new", token.RefreshToken);
                Assert.AreEqual(clock.AddSeconds(1200), token.ExpiresAtUtc);
            });
        }

        [Test]
        public void OAuthTokenParsing_WithoutJson_ThrowsNamingJsonModule()
        {
            AssertAsync.Run(async () =>
            {
                var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                using var oauth = await NewOAuthClientAsync(clock,
                    "{\"access_token\":\"access-new\",\"token_type\":\"Bearer\",\"expires_in\":1200}");
                ProjectJsonBridge.Reset();

                try
                {
                    await oauth.Client.GetValidTokenAsync(Key, NewRefreshRequest(), CancellationToken.None);
                    Assert.Fail("Token parsing without TurboHTTP.JSON must fail.");
                }
                catch (InvalidOperationException ex)
                {
                    StringAssert.Contains("TurboHTTP.JSON", ex.Message);
                    StringAssert.Contains("OAuth token parsing", ex.Message);
                }
            });
        }

        private static async Task<OAuthHarness> NewOAuthClientAsync(DateTime clock, string tokenJson)
        {
            var transport = new MockTransport((req, ctx, ct) =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Type", "application/json");
                return Task.FromResult(new UHttpResponse(
                    HttpStatusCode.OK, headers, Encoding.UTF8.GetBytes(tokenJson), ctx.Elapsed, req));
            });
            var client = new UHttpClient(new UHttpClientOptions { Transport = transport, DisposeTransport = true });
            var store = new InMemoryTokenStore();
            await store.SetAsync(Key, new OAuthToken("stale", clock.AddMinutes(-10), "refresh-1"), CancellationToken.None);
            return new OAuthHarness(client, store, new OAuthClient(client, store, () => clock));
        }

        private static OAuthRefreshRequest NewRefreshRequest()
        {
            return new OAuthRefreshRequest
            {
                Config = new OAuthConfig
                {
                    ClientId = "client-1",
                    AuthorizationEndpoint = new Uri("https://auth.example.com/authorize"),
                    TokenEndpoint = new Uri(TokenEndpoint),
                    RedirectUri = new Uri("myapp://oauth/callback"),
                    Scopes = new[] { "openid", "profile" },
                    UsePkce = true
                },
                Scope = "openid profile"
            };
        }

        private sealed class OAuthHarness : IDisposable
        {
            private readonly UHttpClient _http;
            private readonly InMemoryTokenStore _store;

            public OAuthHarness(UHttpClient http, InMemoryTokenStore store, OAuthClient client)
            {
                _http = http;
                _store = store;
                Client = client;
            }

            public OAuthClient Client { get; }

            public void Dispose()
            {
                Client.Dispose();
                _store.Dispose();
                _http.Dispose();
            }
        }

        private sealed class RecordingBridge : IJsonBridge
        {
            public bool Fail;
            public string LastJson;
            public Type LastType;

            public string Serialize(object value, Type type)
            {
                LastType = type;
                return "serialized";
            }

            public object Deserialize(string json, Type type)
            {
                if (Fail)
                    throw new FormatException("bad json");
                LastJson = json;
                LastType = type;
                return "deserialized";
            }
        }
    }
}
