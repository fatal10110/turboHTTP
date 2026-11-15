using System;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;

namespace TurboHTTP.Tests.Middleware
{
    /// <summary>
    /// Regression tests for #763: a Set-Cookie with a CTL character other than HTAB is ignored
    /// (draft-ietf-httpbis-rfc6265bis 5.6), so the jar never holds a cookie the Cookie request
    /// header serializer refuses; and the public <see cref="CookieJar.Remove"/> /
    /// <see cref="CookieJar.Clear"/> API.
    /// </summary>
    [TestFixture]
    public class CookieJarControlCharTests
    {
        private const string Origin = "https://example.com/";

        [TestCase(0x00)]
        [TestCase(0x01)]
        [TestCase(0x08)]
        [TestCase(0x0A)]
        [TestCase(0x0D)]
        [TestCase(0x1F)]
        [TestCase(0x7F)]
        public void ControlCharacterInName_IgnoresCookie(int code)
        {
            var jar = new CookieJar();
            Store(jar, "s" + (char)code + "id=1; Path=/");

            Assert.AreEqual(0, jar.Count);
            Assert.IsNull(Header(jar));
        }

        [TestCase(0x00)]
        [TestCase(0x01)]
        [TestCase(0x08)]
        [TestCase(0x0A)]
        [TestCase(0x0D)]
        [TestCase(0x1F)]
        [TestCase(0x7F)]
        public void ControlCharacterInValue_IgnoresCookie(int code)
        {
            var jar = new CookieJar();
            Store(jar, "sid=a" + (char)code + "b; Path=/");

            Assert.AreEqual(0, jar.Count);
            Assert.IsNull(Header(jar));
        }

        [Test]
        public void ControlCharacterInAttribute_IgnoresWholeSetCookieString()
        {
            var jar = new CookieJar();
            Store(jar, "sid=1; Path=/\u0001");

            Assert.AreEqual(0, jar.Count);
        }

        [Test]
        public void ControlCharacterCookie_DoesNotReplaceExistingCookie()
        {
            var jar = new CookieJar();
            Store(jar, "sid=good; Path=/");
            Store(jar, "sid=bad\rInjected: 1; Path=/");

            Assert.AreEqual("sid=good", Header(jar));
        }

        [Test]
        public void HorizontalTabInValue_IsAllowed()
        {
            var jar = new CookieJar();
            Store(jar, "sid=a\tb; Path=/");

            Assert.AreEqual(1, jar.Count);
            Assert.AreEqual("sid=a\tb", Header(jar));
        }

        [Test]
        public void NonLatin1CharacterInNameOrValue_IgnoresCookie()
        {
            var jar = new CookieJar();
            Store(jar, "sid=Ā; Path=/");
            Store(jar, "s一id=1; Path=/");

            Assert.AreEqual(0, jar.Count);
        }

        [Test]
        public void Remove_DeletesOnlyTheMatchingCookie()
        {
            var jar = new CookieJar();
            jar.StoreFromSetCookieHeaders(
                new Uri("https://www.example.com/app/page"),
                new[] { "a=1; Path=/", "b=2; Path=/app", "c=3; Domain=Example.com; Path=/" });

            Assert.AreEqual(3, jar.Count);
            Assert.IsFalse(jar.Remove("a", "other.example.com", "/"));
            Assert.IsFalse(jar.Remove("A", "www.example.com", "/"));
            Assert.IsTrue(jar.Remove("a", "WWW.example.com", "/"));
            Assert.IsFalse(jar.Remove("a", "www.example.com", "/"));
            Assert.IsTrue(jar.Remove("c", ".example.com"));
            Assert.AreEqual(1, jar.Count);
            Assert.AreEqual("b=2", jar.GetCookieHeader(new Uri("https://www.example.com/app/x"), HttpMethod.GET, false));
        }

        [Test]
        public void Remove_NullArguments_Throw()
        {
            var jar = new CookieJar();

            Assert.Throws<ArgumentNullException>(() => jar.Remove(null, "example.com", "/"));
            Assert.Throws<ArgumentNullException>(() => jar.Remove("sid", null, "/"));
        }

        [Test]
        public void Clear_RemovesEveryCookie()
        {
            var jar = new CookieJar();
            Store(jar, "a=1; Path=/");
            jar.StoreFromSetCookieHeaders(new Uri("https://other.test/"), new[] { "b=2; Path=/" });
            Assert.AreEqual(2, jar.Count);

            jar.Clear();

            Assert.AreEqual(0, jar.Count);
            Assert.IsNull(Header(jar));
        }

        [Test]
        public void RemoveAndClear_AfterDispose_Throw()
        {
            var jar = new CookieJar();
            jar.Dispose();

            Assert.Throws<ObjectDisposedException>(() => jar.Remove("sid", "example.com", "/"));
            Assert.Throws<ObjectDisposedException>(() => jar.Clear());
        }

        private static void Store(CookieJar jar, string setCookie)
        {
            jar.StoreFromSetCookieHeaders(new Uri(Origin), new[] { setCookie });
        }

        private static string Header(CookieJar jar)
        {
            return jar.GetCookieHeader(new Uri(Origin), HttpMethod.GET, isCrossSiteRequest: false);
        }
    }
}
