using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.JSON.Lite;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Testing
{
    /// <summary>
    /// RecordReplayTransport: compressed bodies are decoded before redaction and stored with matching
    /// Content-Encoding/Content-Length (#1078); the request-body digest never covers a redacted value
    /// (#1079); recordings above LiteJson's 16 MiB default load in Replay (#1084).
    /// </summary>
    [TestFixture]
    public class RecordReplayEncodingAndLimitTests
    {
        private const string Url = "https://api.example.test/items/7";
        private const string AccessSecret = "ACCESS-SECRET-71c";
        private const string PasswordSecret = "hunter2";

        private string _path;

        [SetUp]
        public void SetUp()
        {
            JsonModuleRegistration.EnsureRegistered();
            _path = Path.Combine(TestTempPath.Root, "turbohttp-rr-encoding-" + Guid.NewGuid().ToString("N") + ".json");
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }

        [TestCase("gzip", false)]
        [TestCase("deflate", true)]
        [TestCase("deflate", false, TestName = "CompressedJson_RawDeflate_RecordsIdentityBody_AndReplaysOriginalJson")]
        public void CompressedJson_RecordsIdentityBody_AndReplaysOriginalJson(string coding, bool zlib)
        {
            const string json = "{\"id\":7,\"name\":\"widget\",\"tags\":[\"a\",\"b\"]}";
            var encoded = Encode(coding, zlib, Encoding.UTF8.GetBytes(json));

            AssertAsync.Run(async () =>
            {
                var live = await RecordThroughClientAsync(coding, encoded);
                Assert.AreEqual(json, live);

                var entry = ReadSingleEntry();
                var headers = (Dictionary<string, object>)entry["ResponseHeaders"];
                var storedBody = Convert.FromBase64String((string)entry["ResponseBodyBase64"]);
                Assert.That(headers.ContainsKey("Content-Encoding"), Is.False,
                    "the stored body is identity, so no Content-Encoding may describe it");
                Assert.AreEqual(json, Encoding.UTF8.GetString(storedBody));
                Assert.AreEqual(storedBody.Length.ToString(), HeaderValue(headers, "Content-Length"));

                Assert.AreEqual(json, await ReplayThroughClientAsync());
            });
        }

        [Test]
        public void GzipJson_WithSecret_IsRedacted_AndReplaysWithMatchingLength()
        {
            var json = "{\"access_token\":\"" + AccessSecret + "\",\"token_type\":\"Bearer\"}";
            var encoded = Encode("gzip", false, Encoding.UTF8.GetBytes(json));

            AssertAsync.Run(async () =>
            {
                await RecordThroughClientAsync("gzip", encoded);

                var entry = ReadSingleEntry();
                var storedBody = Encoding.UTF8.GetString(Convert.FromBase64String((string)entry["ResponseBodyBase64"]));
                Assert.That(storedBody, Does.Not.Contain(AccessSecret));
                StringAssert.Contains("\"token_type\":\"Bearer\"", storedBody);
                var headers = (Dictionary<string, object>)entry["ResponseHeaders"];
                Assert.AreEqual(Encoding.UTF8.GetByteCount(storedBody).ToString(), HeaderValue(headers, "Content-Length"));

                var replayed = await ReplayThroughClientAsync();
                Assert.AreEqual(storedBody, replayed);
                StringAssert.Contains("[REDACTED]", replayed);
            });
        }

        [Test]
        public void IdentityJson_RedactionRewritesContentLength()
        {
            var json = "{\"access_token\":\"" + AccessSecret + "\",\"token_type\":\"Bearer\"}";
            var body = Encoding.UTF8.GetBytes(json);
            var inner = new MockTransport();
            inner.EnqueueResponse(HttpStatusCode.OK, Headers("application/json", null, body.Length), body);

            RecordDirect(inner, new UHttpRequest(HttpMethod.GET, new Uri(Url)));

            var entry = ReadSingleEntry();
            var storedBody = Convert.FromBase64String((string)entry["ResponseBodyBase64"]);
            var headers = (Dictionary<string, object>)entry["ResponseHeaders"];
            Assert.AreNotEqual(body.Length, storedBody.Length);
            Assert.AreEqual(storedBody.Length.ToString(), HeaderValue(headers, "Content-Length"));
        }

        [Test]
        public void UnsupportedCoding_OnRedactableBody_FailsTheRecording()
        {
            var body = new byte[] { 0x1b, 0x02, 0x00, 0x7f, 0x42 };
            var inner = new MockTransport();
            inner.EnqueueResponse(HttpStatusCode.OK, Headers("application/json", "br", body.Length), body);
            using var transport = new RecordReplayTransport(inner, RecordOptions(autoFlush: false));

            var result = Dispatch(transport, new UHttpRequest(HttpMethod.GET, new Uri(Url)));
            Assert.IsNull(result.Error, "the live response still reaches the caller");

            var ex = Assert.Throws<InvalidOperationException>(transport.SaveRecordings);
            StringAssert.Contains("Content-Encoding 'br'", ex.Message);
            Assert.That(File.Exists(_path), Is.False);
        }

        [Test]
        public void UnsupportedCoding_OnNonRedactableBody_IsStoredAsReceived()
        {
            var body = new byte[] { 0x1b, 0x02, 0x00, 0x7f, 0x42 };
            var inner = new MockTransport();
            inner.EnqueueResponse(HttpStatusCode.OK, Headers("application/octet-stream", "br", body.Length), body);

            RecordDirect(inner, new UHttpRequest(HttpMethod.GET, new Uri(Url)));

            var entry = ReadSingleEntry();
            var headers = (Dictionary<string, object>)entry["ResponseHeaders"];
            Assert.AreEqual("br", HeaderValue(headers, "Content-Encoding"));
            Assert.AreEqual(body, Convert.FromBase64String((string)entry["ResponseBodyBase64"]));
        }

        [Test]
        public void RequestBodyDigest_DoesNotCoverRedactedPassword_AndReplayStillMatches()
        {
            var rawBody = "grant_type=password&username=alice&password=" + PasswordSecret;
            var inner = new MockTransport();
            inner.EnqueueResponse(HttpStatusCode.OK, body: Encoding.UTF8.GetBytes("ok"));
            RecordDirect(inner, FormRequest(rawBody));

            var root = ReadRoot();
            var salt = Convert.FromBase64String((string)root["KeySalt"]);
            var entry = (Dictionary<string, object>)((List<object>)root["Entries"])[0];
            var storedDigest = (string)entry["RequestBodyHash"];

            // Anyone holding the file has the salt: a guessed password must not reproduce the digest.
            Assert.AreNotEqual(Hmac(salt, rawBody), storedDigest);
            StringAssert.EndsWith(storedDigest, (string)entry["RequestKey"]);

            using var replay = new RecordReplayTransport(null, ReplayOptions());
            var replayed = Dispatch(replay, FormRequest(rawBody));
            Assert.IsNull(replayed.Error);
            Assert.AreEqual("ok", Encoding.UTF8.GetString(replayed.Body));
        }

        [Test]
        public void Version1Recording_StillReplays_WithRawBodyKeys()
        {
            var rawBody = "grant_type=password&username=alice&password=" + PasswordSecret;
            var inner = new MockTransport();
            inner.EnqueueResponse(HttpStatusCode.OK, body: Encoding.UTF8.GetBytes("ok"));
            RecordDirect(inner, FormRequest(rawBody));

            // Rewrite the file as a version-1 recording: digests over the raw body.
            var root = ReadRoot();
            var salt = Convert.FromBase64String((string)root["KeySalt"]);
            var entry = (Dictionary<string, object>)((List<object>)root["Entries"])[0];
            var text = File.ReadAllText(_path)
                .Replace((string)entry["RequestBodyHash"], Hmac(salt, rawBody));
            text = Regex.Replace(text, "\"Version\"\\s*:\\s*2", "\"Version\":1");
            File.WriteAllText(_path, text);

            using var replay = new RecordReplayTransport(null, ReplayOptions());
            var replayed = Dispatch(replay, FormRequest(rawBody));
            Assert.IsNull(replayed.Error);
            Assert.AreEqual("ok", Encoding.UTF8.GetString(replayed.Body));
        }

        [Test]
        public void RecordingAbove16MiB_LoadsInReplay()
        {
            const int bodyBytes = 7 * 1024 * 1024;
            var first = Pattern(bodyBytes, 3);
            var second = Pattern(bodyBytes, 5);
            var inner = new MockTransport();
            inner.EnqueueResponse(HttpStatusCode.OK, Headers("application/octet-stream", null, bodyBytes), first);
            inner.EnqueueResponse(HttpStatusCode.OK, Headers("application/octet-stream", null, bodyBytes), second);

            using (var transport = new RecordReplayTransport(inner, RecordOptions(autoFlush: false)))
            {
                Assert.IsNull(Dispatch(transport, new UHttpRequest(HttpMethod.GET, new Uri(Url + "?n=1"))).Error);
                Assert.IsNull(Dispatch(transport, new UHttpRequest(HttpMethod.GET, new Uri(Url + "?n=2"))).Error);
                transport.SaveRecordings();
            }

            Assert.That(new FileInfo(_path).Length, Is.GreaterThan(LiteJsonSerializer.DefaultMaxInputBytes),
                "test premise: the recording exceeds LiteJson's default input cap");

            using var replay = new RecordReplayTransport(null, ReplayOptions());
            var replayedSecond = Dispatch(replay, new UHttpRequest(HttpMethod.GET, new Uri(Url + "?n=2")));
            Assert.IsNull(replayedSecond.Error);
            Assert.That(replayedSecond.Body, Is.EqualTo(second));
        }

        private async Task<string> RecordThroughClientAsync(string coding, byte[] encoded)
        {
            var inner = new MockTransport();
            inner.EnqueueResponse(HttpStatusCode.OK, Headers("application/json", coding, encoded.Length), encoded);
            using var record = new RecordReplayTransport(inner, RecordOptions(autoFlush: false));
            using var client = NewClient(record);
            using var response = await client.Get(Url).SendBufferedAsync();
            record.SaveRecordings();
            return response.GetBodyAsString();
        }

        private async Task<string> ReplayThroughClientAsync()
        {
            using var replay = new RecordReplayTransport(null, ReplayOptions());
            using var client = NewClient(replay);
            using var response = await client.Get(Url).SendBufferedAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            return response.GetBodyAsString();
        }

        // The canonical stack: DecompressionInterceptor above the transport sends Accept-Encoding.
        private static UHttpClient NewClient(IHttpTransport transport)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                Interceptors = new List<IHttpInterceptor> { new DecompressionInterceptor() }
            });
        }

        private void RecordDirect(MockTransport inner, UHttpRequest request)
        {
            using var transport = new RecordReplayTransport(inner, RecordOptions(autoFlush: false));
            Assert.IsNull(Dispatch(transport, request).Error);
            transport.SaveRecordings();
        }

        private RecordReplayTransportOptions RecordOptions(bool autoFlush)
        {
            return new RecordReplayTransportOptions
            {
                Mode = RecordReplayMode.Record,
                RecordingPath = _path,
                AutoFlushOnDispose = autoFlush
            };
        }

        private RecordReplayTransportOptions ReplayOptions()
        {
            return new RecordReplayTransportOptions
            {
                Mode = RecordReplayMode.Replay,
                RecordingPath = _path,
                MismatchPolicy = RecordReplayMismatchPolicy.Strict
            };
        }

        private static UHttpRequest FormRequest(string body)
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", "application/x-www-form-urlencoded");
            return new UHttpRequest(
                HttpMethod.POST,
                new Uri("https://auth.example.test/token"),
                headers,
                Encoding.UTF8.GetBytes(body));
        }

        private static HttpHeaders Headers(string contentType, string contentEncoding, int contentLength)
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", contentType);
            if (contentEncoding != null)
                headers.Set("Content-Encoding", contentEncoding);
            headers.Set("Content-Length", contentLength.ToString());
            return headers;
        }

        private Dictionary<string, object> ReadRoot()
        {
            return LiteJsonSerializer.Instance.Deserialize<Dictionary<string, object>>(File.ReadAllText(_path));
        }

        private Dictionary<string, object> ReadSingleEntry()
        {
            var entries = (List<object>)ReadRoot()["Entries"];
            Assert.AreEqual(1, entries.Count);
            return (Dictionary<string, object>)entries[0];
        }

        private static string HeaderValue(Dictionary<string, object> headers, string name)
        {
            foreach (var pair in headers)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                    return (string)((List<object>)pair.Value)[0];
            }

            return null;
        }

        private static string Hmac(byte[] key, string body)
        {
            using var hmac = new HMACSHA256(key);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
            var builder = new StringBuilder("hmac-sha256:", 12 + hash.Length * 2);
            foreach (var b in hash)
                builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        private static byte[] Pattern(int length, int seed)
        {
            var bytes = new byte[length];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)((i * seed) ^ (i >> 9));
            return bytes;
        }

        private static byte[] Encode(string coding, bool zlib, byte[] data)
        {
            using var output = new MemoryStream();
            if (coding == "gzip")
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
                    gzip.Write(data, 0, data.Length);
                return output.ToArray();
            }

            if (zlib)
            {
                output.WriteByte(0x78);
                output.WriteByte(0x9C);
            }

            using (var deflate = new DeflateStream(output, CompressionMode.Compress, leaveOpen: true))
                deflate.Write(data, 0, data.Length);

            if (zlib)
            {
                uint a = 1, b = 0;
                foreach (var value in data)
                {
                    a = (a + value) % 65521;
                    b = (b + a) % 65521;
                }

                var adler = (b << 16) | a;
                output.WriteByte((byte)(adler >> 24));
                output.WriteByte((byte)(adler >> 16));
                output.WriteByte((byte)(adler >> 8));
                output.WriteByte((byte)adler);
            }

            return output.ToArray();
        }

        private static DispatchResult Dispatch(RecordReplayTransport transport, UHttpRequest request)
        {
            var handler = new BodyCollector();
            Task.Run(async () =>
                await transport.DispatchAsync(request, handler, new RequestContext(request), CancellationToken.None))
                .GetAwaiter().GetResult();
            return new DispatchResult(handler.Error, handler.Body);
        }

        private readonly struct DispatchResult
        {
            public readonly UHttpException Error;
            public readonly byte[] Body;

            public DispatchResult(UHttpException error, byte[] body)
            {
                Error = error;
                Body = body;
            }
        }

        private sealed class BodyCollector : IHttpHandler
        {
            public UHttpException Error { get; private set; }
            public byte[] Body { get; private set; }

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
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await body.ReadAsync(buffer, CancellationToken.None)) > 0)
                    bytes.Write(buffer, 0, read);

                await body.DisposeAsync();
                Body = bytes.ToArray();
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Error = error;
            }
        }
    }
}
