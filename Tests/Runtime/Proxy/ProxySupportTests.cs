using System;
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Proxy
{
    [TestFixture]
    public class ProxySupportTests
    {
        [Test]
        public void NoProxyBypass_SkipsProxy()
        {
            var bypass = new[] { ".example.com", "*.corp.local", "10.0.0.0/8", "api.test:8443" };

            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("api.example.com", 443, bypass));
            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("example.com", 443, bypass));
            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("svc.corp.local", 443, bypass));
            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("10.1.2.3", 443, bypass));
            Assert.IsTrue(ProxyBypassMatcher.IsBypassed("api.test", 8443, bypass));
            Assert.IsFalse(ProxyBypassMatcher.IsBypassed("api.test", 443, bypass));
        }

        [Test]
        public void HttpsProxy_DoesNotFallbackToHttpProxy_ByDefault()
        {
            var originalHttps = Environment.GetEnvironmentVariable("HTTPS_PROXY");
            var originalHttpsLower = Environment.GetEnvironmentVariable("https_proxy");
            var originalHttp = Environment.GetEnvironmentVariable("HTTP_PROXY");
            var originalHttpLower = Environment.GetEnvironmentVariable("http_proxy");

            try
            {
                Environment.SetEnvironmentVariable("HTTPS_PROXY", null);
                Environment.SetEnvironmentVariable("https_proxy", null);
                Environment.SetEnvironmentVariable("HTTP_PROXY", "http://plain-proxy.example:8080");
                Environment.SetEnvironmentVariable("http_proxy", null);

                var result = ProxyEnvironmentResolver.Resolve(new Uri("https://api.example.com"), configured: null);
                Assert.IsNull(result);
            }
            finally
            {
                Environment.SetEnvironmentVariable("HTTPS_PROXY", originalHttps);
                Environment.SetEnvironmentVariable("https_proxy", originalHttpsLower);
                Environment.SetEnvironmentVariable("HTTP_PROXY", originalHttp);
                Environment.SetEnvironmentVariable("http_proxy", originalHttpLower);
            }
        }
    }
}
