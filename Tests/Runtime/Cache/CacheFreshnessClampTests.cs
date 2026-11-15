using System;
using System.Net;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Cache
{
    /// <summary>
    /// Delta-seconds clamping (RFC 9111 section 1.2.2) and overflow-safe stale-while-revalidate arithmetic in
    /// CacheEntry and MemoryCacheStorage (#457, #459).
    /// </summary>
    public class CacheFreshnessClampTests
    {
        private static readonly TimeSpan HugeWindow = TimeSpan.FromSeconds(300000000000);

        [TestCase("0", 0L)]
        [TestCase("60", 60L)]
        [TestCase(" 60 ", 60L)]
        [TestCase("\"60\"", 60L)]
        [TestCase("2147483647", 2147483647L)]
        [TestCase("2147483648", 2147483648L)]
        [TestCase("2147483649", 2147483648L)]
        [TestCase("999999999999", 2147483648L)]
        [TestCase("99999999999999999999", 2147483648L)]
        [TestCase("0000000000000000000000060", 60L)]
        public void DeltaSeconds_Digits_SaturateAtTwoToThe31(string value, long expected)
        {
            Assert.IsTrue(CacheDeltaSeconds.TryParse(value, out var seconds));
            Assert.AreEqual(expected, seconds);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("-1")]
        [TestCase("+5")]
        [TestCase("1.5")]
        [TestCase("6 0")]
        [TestCase("99999999999999999999x")]
        [TestCase((string)null)]
        public void DeltaSeconds_NonDigits_AreRejected(string value)
        {
            Assert.IsFalse(CacheDeltaSeconds.TryParse(value, out _));
        }

        [Test]
        public void DeltaSeconds_AddClamped_SaturatesAtDateTimeBounds()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(DateTime.MaxValue, CacheDeltaSeconds.AddClamped(now, TimeSpan.MaxValue));
            Assert.AreEqual(DateTime.MinValue, CacheDeltaSeconds.AddClamped(now, TimeSpan.MinValue));
            Assert.AreEqual(now.AddSeconds(5), CacheDeltaSeconds.AddClamped(now, TimeSpan.FromSeconds(5)));
        }

        [Test]
        public void CacheEntry_HugeStaleWhileRevalidate_InsideWindow_DoesNotThrow()
        {
            var now = DateTime.UtcNow;
            var entry = CreateEntry("k", now.AddHours(-1), HugeWindow);

            Assert.IsTrue(entry.IsStaleWhileRevalidate(now));
            Assert.IsFalse(entry.IsFresh(now));
        }

        [Test]
        public void CacheEntry_HugeStaleWhileRevalidate_IsClampedToTwoToThe31Seconds()
        {
            // 300000000000 s after 1900 is past DateTime.MaxValue; clamped to 2^31 s the window closed in 1968.
            var expiredAt = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var entry = CreateEntry("k", expiredAt, HugeWindow);
            var windowEnd = expiredAt.AddSeconds(2147483648d);

            Assert.IsTrue(entry.IsStaleWhileRevalidate(windowEnd.AddSeconds(-1)));
            Assert.IsFalse(entry.IsStaleWhileRevalidate(windowEnd));
            Assert.IsFalse(entry.IsStaleWhileRevalidate(DateTime.UtcNow));
        }

        [Test]
        public void CacheEntry_StaleWhileRevalidate_NearDateTimeMax_DoesNotThrow()
        {
            var expiredAt = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc).AddDays(-1);
            var entry = CreateEntry("k", expiredAt, TimeSpan.MaxValue);

            Assert.IsTrue(entry.IsStaleWhileRevalidate(expiredAt.AddHours(1)));
            Assert.IsFalse(entry.IsFresh(expiredAt.AddHours(1)));
        }

        [Test]
        public void MemoryCacheStorage_EntryWithHugeStaleWhileRevalidate_StorageKeepsWorking()
        {
            AssertAsync.Run(async () =>
            {
                using var storage = new MemoryCacheStorage();
                var now = DateTime.UtcNow;
                var malformed = CreateEntry("bad", new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc), HugeWindow);
                await storage.SetAsync("bad", malformed);
                await storage.SetAsync("good", CreateEntry("good", now.AddHours(1), null));

                // Storage never evicts on freshness (#500); the middleware decides what a stale entry is worth.
                Assert.AreEqual(2, await storage.GetCountAsync());
                Assert.IsNotNull(await storage.GetAsync("bad"));
                Assert.IsNotNull(await storage.GetAsync("good"));

                await storage.SetAsync("third", CreateEntry("third", now.AddHours(1), HugeWindow));
                Assert.AreEqual(3, await storage.GetCountAsync());
            });
        }

        private static CacheEntry CreateEntry(string key, DateTime expiresAtUtc, TimeSpan? staleWhileRevalidate)
        {
            return new CacheEntry(
                key: key,
                statusCode: HttpStatusCode.OK,
                headers: new HttpHeaders(),
                body: Encoding.UTF8.GetBytes("payload"),
                cachedAtUtc: expiresAtUtc,
                expiresAtUtc: expiresAtUtc,
                eTag: "\"v1\"",
                lastModified: null,
                responseUrl: new Uri("https://example.test/" + key),
                varyHeaders: null,
                varyKey: null,
                staleWhileRevalidate: staleWhileRevalidate,
                mustRevalidate: false,
                trailers: null);
        }
    }
}
