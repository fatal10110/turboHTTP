using System;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;

namespace TurboHTTP.Tests.Middleware
{
    /// <summary>
    /// Regression tests for #59: public-suffix and IP-literal Domain scoping (RFC 6265 5.1.3, 5.3
    /// step 5), "leave secure cookies alone", cookie name prefixes and size limits
    /// (draft-ietf-httpbis-rfc6265bis 4.1.3, 5.6, 5.7).
    /// </summary>
    [TestFixture]
    public class CookieJarScopingTests
    {
        [TestCase("https://tenant.github.io/", "github.io", "https://other.github.io/")]
        [TestCase("https://tenant.blogspot.com/", "blogspot.com", "https://other.blogspot.com/")]
        [TestCase("https://tenant.herokuapp.com/", "herokuapp.com", "https://other.herokuapp.com/")]
        [TestCase("https://bucket.amazonaws.com/", "amazonaws.com", "https://other.amazonaws.com/")]
        [TestCase("https://shop.co.za/", "co.za", "https://other.co.za/")]
        [TestCase("https://shop.com.ua/", "com.ua", "https://other.com.ua/")]
        public void DomainAttributeOnPublicSuffix_IsRejected(string origin, string domain, string sibling)
        {
            var jar = new CookieJar();
            Store(jar, origin, "sid=1; Path=/; Domain=" + domain);

            Assert.IsNull(Header(jar, origin));
            Assert.IsNull(Header(jar, sibling));
            Assert.AreEqual(0, jar.Count);
        }

        [Test]
        public void DomainAttributeEqualToPublicSuffixHost_BecomesHostOnly()
        {
            // RFC 6265 5.3 step 5: a public-suffix Domain identical to the request host is treated
            // as an absent Domain attribute (host-only cookie).
            var jar = new CookieJar();
            Store(jar, "https://github.io/", "sid=1; Path=/; Domain=github.io");

            Assert.AreEqual("sid=1", Header(jar, "https://github.io/"));
            Assert.IsNull(Header(jar, "https://tenant.github.io/"));
        }

        [Test]
        public void CustomPublicSuffixPredicate_IsConsulted()
        {
            var jar = new CookieJar(isPublicSuffix: d => d == "corp-tenants.example");
            Store(jar, "https://a.corp-tenants.example/", "sid=1; Path=/; Domain=corp-tenants.example");
            Store(jar, "https://a.corp-tenants.example/", "ok=1; Path=/; Domain=a.corp-tenants.example");

            Assert.IsNull(Header(jar, "https://b.corp-tenants.example/"));
            Assert.AreEqual("ok=1", Header(jar, "https://a.corp-tenants.example/"));
        }

        [Test]
        public void IpHost_DomainAttributeSuffix_IsRejected()
        {
            var jar = new CookieJar();
            Store(jar, "https://192.168.1.5/", "sid=1; Path=/; Domain=168.1.5");

            Assert.AreEqual(0, jar.Count);
            Assert.IsNull(Header(jar, "https://192.168.1.5/"));
        }

        [Test]
        public void IpHost_DomainAttributeEqualToHost_IsHostOnly()
        {
            var jar = new CookieJar();
            Store(jar, "https://192.168.1.5/", "sid=1; Path=/; Domain=192.168.1.5");

            Assert.AreEqual(1, jar.Count);
            Assert.AreEqual("sid=1", Header(jar, "https://192.168.1.5/"));
            Assert.IsNull(Header(jar, "https://192.168.1.6/"));
        }

        [Test]
        public void InsecureResponse_CannotOverwriteSecureCookie()
        {
            var jar = new CookieJar();
            Store(jar, "https://example.com/", "sid=secure; Path=/; Secure");
            Store(jar, "http://example.com/", "sid=evil; Path=/");

            Assert.AreEqual("sid=secure", Header(jar, "https://example.com/"));
            Assert.IsNull(Header(jar, "http://example.com/"));
        }

        [Test]
        public void InsecureResponse_CannotDeleteSecureCookie()
        {
            var jar = new CookieJar();
            Store(jar, "https://example.com/", "sid=secure; Path=/; Secure");
            Store(jar, "http://example.com/", "sid=; Path=/; Max-Age=0");

            Assert.AreEqual("sid=secure", Header(jar, "https://example.com/"));
        }

        [Test]
        public void InsecureResponse_CannotShadowSecureCookie_OnSubdomainOrSubPath()
        {
            var jar = new CookieJar();
            Store(jar, "https://www.example.com/", "sid=secure; Path=/; Secure; Domain=example.com");
            Store(jar, "http://other.example.com/app/x", "sid=evil; Path=/app");
            Store(jar, "http://other.example.com/app/x", "unrelated=1; Path=/app");

            Assert.AreEqual("unrelated=1", Header(jar, "http://other.example.com/app/y"));
            Assert.AreEqual("sid=secure", Header(jar, "https://other.example.com/"));
        }

        [Test]
        public void SecureResponse_CanOverwriteSecureCookie()
        {
            var jar = new CookieJar();
            Store(jar, "https://example.com/", "sid=old; Path=/; Secure");
            Store(jar, "https://example.com/", "sid=new; Path=/; Secure");

            Assert.AreEqual("sid=new", Header(jar, "https://example.com/"));
        }

        [TestCase("__Secure-sid=1; Path=/", "https://example.com/", false)]
        [TestCase("__Secure-sid=1; Path=/; Secure", "https://example.com/", true)]
        [TestCase("__secure-sid=1; Path=/", "https://example.com/", false)]
        [TestCase("__Host-sid=1; Path=/; Secure", "https://example.com/", true)]
        [TestCase("__Host-sid=1; Secure", "https://example.com/", false)]
        [TestCase("__Host-sid=1; Path=/app; Secure", "https://example.com/app/x", false)]
        [TestCase("__Host-sid=1; Path=/", "https://example.com/", false)]
        [TestCase("__Host-sid=1; Path=/; Secure; Domain=example.com", "https://example.com/", false)]
        [TestCase("__HOST-sid=1; Path=/; Secure; Domain=example.com", "https://example.com/", false)]
        public void CookieNamePrefixes_AreEnforced(string setCookie, string origin, bool expectStored)
        {
            var jar = new CookieJar();
            Store(jar, origin, setCookie);

            Assert.AreEqual(expectStored ? 1 : 0, jar.Count, setCookie);
        }

        [Test]
        public void OversizedNameValue_IsRejected()
        {
            var jar = new CookieJar();
            Store(jar, "https://example.com/", "big=" + new string('v', 4096 - 3 + 1) + "; Path=/");
            Assert.AreEqual(0, jar.Count);

            Store(jar, "https://example.com/", "big=" + new string('v', 4096 - 3) + "; Path=/");
            Assert.AreEqual(1, jar.Count);
        }

        [Test]
        public void OversizedAttributeValue_IsIgnored()
        {
            var jar = new CookieJar();
            var longPath = "/" + new string('p', 1024);
            Store(jar, "https://a.example.com/dir/page", "sid=1; Path=" + longPath + "; Domain=example.com");
            Store(jar, "https://a.example.com/dir/page", "hid=1; Domain=" + new string('d', 1020) + ".example.com; Path=/");

            // Path attribute over 1024 octets is ignored (default path /dir); the Domain attribute
            // over 1024 octets is ignored (host-only cookie).
            Assert.AreEqual("sid=1", Header(jar, "https://b.example.com/dir/x"));
            Assert.AreEqual(2, jar.Count);
            Assert.IsNull(Header(jar, "https://b.example.com/"));
            Assert.AreEqual("hid=1", Header(jar, "https://a.example.com/"));
        }

        [Test]
        public void ConcurrentReaders_SeeConsistentHeaders()
        {
            AssertAsync.Run(async () =>
            {
                var jar = new CookieJar();
                Store(jar, "https://example.com/", "a=1; Path=/");
                Store(jar, "https://example.com/", "b=2; Path=/");

                var tasks = new Task[4];
                for (int t = 0; t < tasks.Length; t++)
                {
                    tasks[t] = Task.Run(() =>
                    {
                        for (int i = 0; i < 500; i++)
                            Assert.AreEqual("a=1; b=2", Header(jar, "https://example.com/"));
                    });
                }

                await Task.WhenAll(tasks);
            });
        }

        private static void Store(CookieJar jar, string uri, string setCookie)
        {
            jar.StoreFromSetCookieHeaders(new Uri(uri), new[] { setCookie });
        }

        private static string Header(CookieJar jar, string uri)
        {
            return jar.GetCookieHeader(new Uri(uri), HttpMethod.GET, isCrossSiteRequest: false);
        }
    }
}
