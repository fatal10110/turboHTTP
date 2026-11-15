using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Editor;
using TurboHTTP.Observability;

namespace TurboHTTP.Tests.Editor
{
    /// <summary>
    /// #1009: the HTTP Monitor export (and the window's header masking) use the active capture policy plus
    /// the default sensitive headers, redact URLs in the URL, headers, error text and timeline, and omit
    /// bodies unless the user opts in. #478 / #1059: the export redacts timeline values again.
    /// </summary>
    [TestFixture]
    public class HttpMonitorExportRedactionTests
    {
        private const string QuerySecret = "QUERY-SECRET-71a";
        private const string CustomSecret = "CUSTOM-SECRET-2b8";
        private const string BearerSecret = "BEARER-SECRET-c09";
        private const string ApiKeySecret = "APIKEY-SECRET-d4e";
        private const string BodySecret = "BODY-SECRET-f35";
        private const string CodeSecret = "CODE-SECRET-605";

        [Test]
        public void Export_RedactsWithActivePolicyPlusDefaults_AndOmitsBodies()
        {
            // Capture-time redaction off (raw values captured) but a custom sensitive header configured.
            var active = RedactionPolicy.None.WithSensitiveHeaders("X-Custom-Secret");
            var json = HttpMonitorWindow.BuildExportJson(CreateRawEvent(), active, includeBodies: false);

            foreach (var secret in new[] { QuerySecret, CustomSecret, BearerSecret, ApiKeySecret, BodySecret, CodeSecret })
                Assert.That(json, Does.Not.Contain(secret), "export contains " + secret);
            StringAssert.Contains("access_token=[REDACTED]", json);
            StringAssert.IsMatch("\"omitted\"\\s*:\\s*true", json);
            StringAssert.IsMatch("\"value\"\\s*:\\s*\"application/json\"", json);
        }

        [Test]
        public void Export_IncludesBodiesOnlyWhenOptedIn()
        {
            var json = HttpMonitorWindow.BuildExportJson(CreateRawEvent(), RedactionPolicy.Default, includeBodies: true);

            StringAssert.Contains(BodySecret, json);
            Assert.That(json, Does.Not.Contain(BearerSecret));
            Assert.That(json, Does.Not.Contain(QuerySecret));
        }

        [Test]
        public void ExportPolicy_KeepsCustomHeadersAndAlwaysAddsDefaults()
        {
            var active = RedactionPolicy.Default
                .WithoutSensitiveHeaders("Authorization")
                .WithSensitiveHeaders("X-Custom-Secret")
                .WithQueryValueRedaction(false);

            var export = HttpMonitorWindow.ResolveExportPolicy(active);

            Assert.IsTrue(export.IsSensitiveHeader("X-Custom-Secret"));
            Assert.IsTrue(export.IsSensitiveHeader("Authorization"));
            Assert.IsTrue(export.RedactQueryValues);
            Assert.AreSame(export, HttpMonitorWindow.ResolveExportPolicy(active), "cached per active policy");
            Assert.IsTrue(HttpMonitorWindow.ResolveExportPolicy(null).RedactQueryValues);
            Assert.IsTrue(HttpMonitorWindow.ResolveExportPolicy(null).IsSensitiveHeader("Cookie"));
        }

        private static HttpMonitorEvent CreateRawEvent()
        {
            var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Authorization", "Bearer " + BearerSecret },
                { "X-Custom-Secret", CustomSecret },
                { "X-Api-Key", ApiKeySecret },
                { "Accept", "application/json" }
            };
            var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Location", "https://cdn.example.test/o?code=" + CodeSecret },
                { "Content-Type", "application/json" }
            };
            var timeline = new List<HttpMonitorTimelineEvent>
            {
                new HttpMonitorTimelineEvent(
                    "CacheMiss",
                    TimeSpan.FromMilliseconds(1),
                    new Dictionary<string, string>
                    {
                        { "key", "GET https://api.example.test/items?access_token=" + QuerySecret }
                    }),
                new HttpMonitorTimelineEvent(
                    "Custom",
                    TimeSpan.FromMilliseconds(2),
                    new Dictionary<string, string> { { "X-Api-Key", ApiKeySecret } })
            };
            var body = Encoding.UTF8.GetBytes("{\"access_token\":\"" + BodySecret + "\"}");

            return new HttpMonitorEvent(
                id: "evt1",
                timestamp: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                method: "GET",
                url: "https://api.example.test/items?access_token=" + QuerySecret,
                requestHeaders: requestHeaders,
                requestBody: body,
                requestBodyCaptureNote: string.Empty,
                originalRequestBodySize: body.Length,
                isRequestBodyTruncated: false,
                isRequestBodyBinary: false,
                statusCode: 302,
                statusText: "Found",
                responseHeaders: responseHeaders,
                responseBody: body,
                originalResponseBodySize: body.Length,
                isResponseBodyTruncated: false,
                isResponseBodyBinary: false,
                elapsedTime: TimeSpan.FromMilliseconds(5),
                timeline: timeline,
                error: "Redirect to https://cdn.example.test/o?code=" + CodeSecret + " failed",
                errorType: UHttpErrorType.NetworkError,
                failureKind: HttpMonitorFailureKind.TransportError);
        }
    }
}
