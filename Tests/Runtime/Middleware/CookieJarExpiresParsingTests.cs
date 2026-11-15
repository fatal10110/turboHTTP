using System;
using System.Globalization;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;

namespace TurboHTTP.Tests.Middleware
{
    /// <summary>
    /// Regression tests for #767: the Expires attribute is parsed with the RFC 6265 section 5.1.1
    /// cookie-date algorithm (tokens in any order, weekday and zone ignored, two-digit years,
    /// range checks), never with a culture-dependent DateTime parser.
    /// </summary>
    [TestFixture]
    public class CookieJarExpiresParsingTests
    {
        private static readonly Uri Origin = new Uri("https://example.com/");

        [TestCase("Thu, 01 Jan 1970 00:00:00 GMT", "1970-01-01T00:00:00")]
        [TestCase("Thu, 01 Jan 1970 00:00:00 UTC", "1970-01-01T00:00:00")]
        [TestCase("Thursday, 01-Jan-1970 00:00:00 UTC", "1970-01-01T00:00:00")]
        [TestCase("Thursday, 01-Jan-70 00:00:00 GMT", "1970-01-01T00:00:00")]
        [TestCase("Thu Jan  1 00:00:00 1970", "1970-01-01T00:00:00")]
        [TestCase("Mon, 09 Jun 2021 10:18:14 GMT", "2021-06-09T10:18:14")]
        [TestCase("Wed, 09 Jun 2021 10:18:14 +0200", "2021-06-09T10:18:14")]
        [TestCase("Wed, 09 Jun 2021 10:18:14 -0800", "2021-06-09T10:18:14")]
        [TestCase("09 jun 2021 10:18:14", "2021-06-09T10:18:14")]
        [TestCase("2021 JUNE 9 1:2:3", "2021-06-09T01:02:03")]
        [TestCase("Sat, 01-Jan-69 12:30:45 GMT", "2069-01-01T12:30:45")]
        [TestCase("Sun, 29 Feb 2032 23:59:59 GMT", "2032-02-29T23:59:59")]
        public void CookieDate_ExpiresAtParsedInstant(string expires, string expectedUtc)
        {
            var expected = ParseUtc(expectedUtc);

            Assert.IsTrue(CookieJar.TryParseCookieDate(expires, out var parsed), expires);
            Assert.AreEqual(expected, parsed);
            Assert.AreEqual(DateTimeKind.Utc, parsed.Kind);

            var jar = new CookieJar();
            jar.StoreFromSetCookieHeaders(Origin, new[] { "sid=1; Path=/; Expires=" + expires }, expected.AddDays(-1));

            Assert.AreEqual("sid=1", Header(jar, expected.AddSeconds(-1)));
            Assert.IsNull(Header(jar, expected));
        }

        [TestCase("1/2/2030")]
        [TestCase("2021-06-09T10:18:14Z")]
        [TestCase("Thu, 32 Jan 2030 00:00:00 GMT")]
        [TestCase("Thu, 00 Jan 2030 00:00:00 GMT")]
        [TestCase("Thu, 31 Feb 2030 00:00:00 GMT")]
        [TestCase("Thu, 01 Jan 1600 00:00:00 GMT")]
        [TestCase("Thu, 01 Jan 2030 24:00:00 GMT")]
        [TestCase("Thu, 01 Jan 2030 23:60:00 GMT")]
        [TestCase("Thu, 01 Jan 2030 23:59:60 GMT")]
        [TestCase("Thu, 01 Jan 2030 GMT")]
        [TestCase("Thu, 01 2030 00:00:00 GMT")]
        [TestCase("Thu, Jan 2030 00:00:00 GMT")]
        [TestCase("Thu, 01 Jan 00:00:00 GMT")]
        [TestCase("Thu, 01 Jan 12345 00:00:00 GMT")]
        [TestCase("Thu, 01 Jan 2030 000:00:00 GMT")]
        [TestCase("garbage")]
        [TestCase("")]
        public void NotACookieDate_LeavesSessionCookie(string expires)
        {
            Assert.IsFalse(CookieJar.TryParseCookieDate(expires, out _), expires);

            var jar = new CookieJar();
            jar.StoreFromSetCookieHeaders(Origin, new[] { "sid=1; Path=/; Expires=" + expires }, ParseUtc("2000-01-01T00:00:00"));

            Assert.AreEqual(1, jar.Count);
            Assert.AreEqual("sid=1", Header(jar, ParseUtc("2100-01-01T00:00:00")));
        }

        [TestCase("Thu, 01 Jan 1970 00:00:00 GMT")]
        [TestCase("Thu, 01 Jan 1970 00:00:00 UTC")]
        [TestCase("Thursday, 01-Jan-70 00:00:00 UTC")]
        public void PastExpires_DeletesExistingCookie(string expires)
        {
            var now = ParseUtc("2026-11-15T00:00:00");
            var jar = new CookieJar();
            jar.StoreFromSetCookieHeaders(Origin, new[] { "sid=1; Path=/" }, now);
            Assert.AreEqual(1, jar.Count);

            jar.StoreFromSetCookieHeaders(Origin, new[] { "sid=1; Path=/; Expires=" + expires }, now);

            Assert.AreEqual(0, jar.Count);
        }

        [Test]
        public void MaxAge_StillOverridesPastExpires()
        {
            var now = ParseUtc("2026-11-15T00:00:00");
            var jar = new CookieJar();
            jar.StoreFromSetCookieHeaders(
                Origin,
                new[] { "sid=1; Path=/; Max-Age=60; Expires=Thu, 01 Jan 1970 00:00:00 UTC" },
                now);

            Assert.AreEqual("sid=1", Header(jar, now.AddSeconds(59)));
            Assert.IsNull(Header(jar, now.AddSeconds(60)));
        }

        private static DateTime ParseUtc(string iso)
        {
            return DateTime.SpecifyKind(
                DateTime.ParseExact(iso, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                DateTimeKind.Utc);
        }

        private static string Header(CookieJar jar, DateTime nowUtc)
        {
            return jar.GetCookieHeader(Origin, HttpMethod.GET, isCrossSiteRequest: false, utcNowOverride: nowUtc);
        }
    }
}
