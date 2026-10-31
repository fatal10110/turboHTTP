using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Testing
{
    /// <summary>
    /// #93: recordings never hold OAuth secrets (JSON, +json, form bodies; match-key header values).
    /// </summary>
    [TestFixture]
    public class RecordReplayRedactionTests
    {
        private const string CodeSecret = "CODE-SECRET-7f1";
        private const string VerifierSecret = "VERIFIER-SECRET-9a2";
        private const string ClientSecret = "CLIENT-SECRET-3c4";
        private const string PasswordSecret = "PASSWORD-SECRET-5d6";
        private const string AccessSecret = "ACCESS-SECRET-1b8";
        private const string RefreshSecret = "REFRESH-SECRET-2e9";
        private const string IdTokenSecret = "IDTOKEN-SECRET-4f0";
        private const string BearerSecret = "BEARER-SECRET-6a7";

        private static readonly string[] AllSecrets =
        {
            CodeSecret, VerifierSecret, ClientSecret, PasswordSecret, AccessSecret, RefreshSecret, IdTokenSecret
        };

        private string _path;

        [SetUp]
        public void SetUp()
        {
            JsonModuleRegistration.EnsureRegistered();
            _path = Path.Combine(Path.GetTempPath(), "turbohttp-rr-redaction-" + Guid.NewGuid().ToString("N") + ".json");
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }

        [TestCase("application/json")]
        [TestCase("application/vnd.api+json")]
        [TestCase("application/problem+json; charset=utf-8")]
        public void TokenExchange_JsonBodies_RecordNoSecrets(string contentType)
        {
            var requestBody = "{\"grant_type\":\"authorization_code\",\"code\":\"" + CodeSecret +
                              "\",\"code_verifier\":\"" + VerifierSecret + "\",\"client_secret\":\"" + ClientSecret +
                              "\",\"password\":\"" + PasswordSecret + "\"}";
            var forwarded = RecordTokenExchange(contentType, requestBody, contentType);

            Assert.That(forwarded, Does.Contain(AccessSecret), "the caller still gets the real response");
            AssertRecordingHasNoSecrets(AllSecrets);
            StringAssert.Contains("grant_type", DecodedRecording());
        }

        [Test]
        public void TokenExchange_FormUrlEncodedRequest_RecordsNoSecrets()
        {
            var requestBody = "grant_type=authorization_code&code=" + CodeSecret +
                              "&code_verifier=" + VerifierSecret +
                              "&client_secret=" + ClientSecret +
                              "&password=" + PasswordSecret +
                              "&redirect_uri=https%3A%2F%2Fapp.example%2Fcb";
            RecordTokenExchange("application/x-www-form-urlencoded", requestBody, "application/json");

            AssertRecordingHasNoSecrets(AllSecrets);
            var decoded = DecodedRecording();
            StringAssert.Contains("grant_type=authorization_code", decoded);
            StringAssert.Contains("redirect_uri=https%3A%2F%2Fapp.example%2Fcb", decoded);
        }

        [Test]
        public void JsonFieldMatching_IsCaseAndFormatInsensitive_AndNested()
        {
            var body = "{\"data\":{\"AccessToken\":\"" + AccessSecret + "\",\"Refresh-Token\":\"" + RefreshSecret +
                       "\"},\"items\":[{\"ID_TOKEN\":\"" + IdTokenSecret + "\"}]}";
            RecordTokenExchange("application/json", "{}", "application/json", body);

            AssertRecordingHasNoSecrets(AccessSecret, RefreshSecret, IdTokenSecret);
        }

        [Test]
        public void UnparseableJsonBody_IsNotRecordedRaw()
        {
            var body = "{\"access_token\":\"" + AccessSecret + "\""; // truncated JSON
            RecordTokenExchange("application/json", "{}", "application/json", body);

            AssertRecordingHasNoSecrets(AccessSecret);
        }

        [Test]
        public void SensitiveMatchHeader_IsStoredAsSaltedHash()
        {
            Record(BearerSecret);

            var text = File.ReadAllText(_path);
            Assert.That(DecodedRecording(), Does.Not.Contain(BearerSecret));
            StringAssert.Contains("authorization=hmac-sha256:", text);
            StringAssert.Contains("\"KeySalt\"", text);
        }

        [Test]
        public void SaltedHash_DiffersPerRecording()
        {
            Record(BearerSecret);
            var first = ExtractAuthorizationHash(File.ReadAllText(_path));
            File.Delete(_path);
            Record(BearerSecret);
            var second = ExtractAuthorizationHash(File.ReadAllText(_path));

            Assert.That(first, Is.Not.Null.And.Not.Empty);
            Assert.AreNotEqual(first, second);
        }

        [Test]
        public void Replay_HashedKey_MatchesSameBearerOnly()
        {
            Record(BearerSecret);

            using var replay = new RecordReplayTransport(
                innerTransport: null,
                new RecordReplayTransportOptions
                {
                    Mode = RecordReplayMode.Replay,
                    RecordingPath = _path,
                    MismatchPolicy = RecordReplayMismatchPolicy.Strict,
                    MatchHeaderNames = new[] { "Authorization" }
                });

            var other = Dispatch(replay, AuthorizedRequest("OTHER-BEARER"));
            Assert.IsNotNull(other.Error, "a different bearer token must not match");
            StringAssert.Contains("No replay recording matched", other.Error.Message);
            Assert.That(other.Error.Message, Does.Not.Contain("OTHER-BEARER"));

            var same = Dispatch(replay, AuthorizedRequest(BearerSecret));
            Assert.IsNull(same.Error);
            Assert.AreEqual("profile", same.Body);
        }

        private void Record(string bearer)
        {
            var inner = new MockTransport();
            inner.EnqueueResponse(HttpStatusCode.OK, body: Encoding.UTF8.GetBytes("profile"));
            using var transport = new RecordReplayTransport(
                inner,
                new RecordReplayTransportOptions
                {
                    Mode = RecordReplayMode.Record,
                    RecordingPath = _path,
                    MatchHeaderNames = new[] { "Authorization" }
                });
            var result = Dispatch(transport, AuthorizedRequest(bearer));
            Assert.IsNull(result.Error);
            transport.SaveRecordings();
        }

        private static UHttpRequest AuthorizedRequest(string bearer)
        {
            var headers = new HttpHeaders();
            headers.Set("Authorization", "Bearer " + bearer);
            return new UHttpRequest(HttpMethod.GET, new Uri("https://api.example.test/me"), headers);
        }

        private string RecordTokenExchange(
            string requestContentType,
            string requestBody,
            string responseContentType,
            string responseBody = null)
        {
            responseBody ??= "{\"access_token\":\"" + AccessSecret + "\",\"refresh_token\":\"" + RefreshSecret +
                             "\",\"id_token\":\"" + IdTokenSecret + "\",\"token_type\":\"Bearer\"}";
            var responseHeaders = new HttpHeaders();
            responseHeaders.Set("Content-Type", responseContentType);
            var inner = new MockTransport();
            inner.EnqueueResponse(HttpStatusCode.OK, responseHeaders, Encoding.UTF8.GetBytes(responseBody));

            using var transport = new RecordReplayTransport(
                inner,
                new RecordReplayTransportOptions { Mode = RecordReplayMode.Record, RecordingPath = _path });

            var headers = new HttpHeaders();
            headers.Set("Content-Type", requestContentType);
            var request = new UHttpRequest(
                HttpMethod.POST,
                new Uri("https://auth.example.test/token"),
                headers,
                Encoding.UTF8.GetBytes(requestBody));
            var result = Dispatch(transport, request);
            Assert.IsNull(result.Error);
            transport.SaveRecordings();
            return result.Body;
        }

        private static DispatchResult Dispatch(RecordReplayTransport transport, UHttpRequest request)
        {
            var handler = new BodyCollector();
            Task.Run(async () =>
                await transport.DispatchAsync(request, handler, new RequestContext(request), CancellationToken.None))
                .GetAwaiter().GetResult();
            return new DispatchResult(handler.Error, handler.Body);
        }

        // File text plus every base64 field decoded, so a secret hidden in a body is found too.
        private string DecodedRecording()
        {
            var text = File.ReadAllText(_path);
            var builder = new StringBuilder(text);
            foreach (Match match in Regex.Matches(text, "\"\\w*Base64\"\\s*:\\s*\"([^\"]*)\""))
            {
                builder.Append('\n').Append(Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value)));
            }

            return builder.ToString();
        }

        private void AssertRecordingHasNoSecrets(params string[] secrets)
        {
            Assert.That(File.Exists(_path), Is.True);
            var decoded = DecodedRecording();
            foreach (var secret in secrets)
                Assert.That(decoded, Does.Not.Contain(secret), "recording contains " + secret);
        }

        private static string ExtractAuthorizationHash(string text)
        {
            var match = Regex.Match(text, "authorization=(hmac-sha256:[0-9a-f]+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        private readonly struct DispatchResult
        {
            public readonly UHttpException Error;
            public readonly string Body;

            public DispatchResult(UHttpException error, string body)
            {
                Error = error;
                Body = body;
            }
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

                var bytes = new List<byte>();
                var buffer = new byte[4096];
                int read;
                while ((read = await body.ReadAsync(buffer, CancellationToken.None)) > 0)
                {
                    for (int i = 0; i < read; i++)
                        bytes.Add(buffer[i]);
                }

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
