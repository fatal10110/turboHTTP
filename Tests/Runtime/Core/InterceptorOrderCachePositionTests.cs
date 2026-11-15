using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// Regression tests for #536: the cache must sit inside the redirect and cookie interceptors, and the
    /// public <see cref="IInterceptorRole"/> contract lets interceptors outside the first-party assemblies
    /// take part in the order validation.
    /// </summary>
    [TestFixture]
    public class InterceptorOrderCachePositionTests
    {
        [Test]
        public void CacheOutsideRedirect_IsRejectedAtConstruction()
        {
            var ex = Assert.Throws<ArgumentException>(() => CreateClient(
                new CacheInterceptor(new CachePolicy()),
                new RedirectInterceptor()));

            StringAssert.Contains("CacheInterceptor", ex.Message);
            StringAssert.Contains("RedirectInterceptor", ex.Message);
        }

        [Test]
        public void CacheOutsideCookie_IsRejectedAtConstruction()
        {
            var ex = Assert.Throws<ArgumentException>(() => CreateClient(
                new CacheInterceptor(new CachePolicy()),
                new CookieInterceptor()));

            StringAssert.Contains("CacheInterceptor", ex.Message);
            StringAssert.Contains("CookieInterceptor", ex.Message);
        }

        [Test]
        public void CacheOutsideRedirect_IsRejectedWithInterceptorsBetween()
        {
            var ex = Assert.Throws<ArgumentException>(() => CreateClient(
                new CookieInterceptor(),
                new CacheInterceptor(new CachePolicy()),
                new DecompressionInterceptor(),
                new RedirectInterceptor()));

            StringAssert.Contains("RedirectInterceptor", ex.Message);
        }

        [Test]
        public void CacheInsideRedirectAndCookie_IsAcceptedInEitherRedirectCookieOrder()
        {
            using (CreateClient(new RedirectInterceptor(), new CookieInterceptor(), new CacheInterceptor(new CachePolicy())))
            {
            }

            using (CreateClient(new CookieInterceptor(), new RedirectInterceptor(), new CacheInterceptor(new CachePolicy())))
            {
            }
        }

        [Test]
        public void CookieInterceptor_DeclaresCookieRole()
        {
            IHttpInterceptor cookie = new CookieInterceptor();
            Assert.IsInstanceOf<IInterceptorRole>(cookie);
            Assert.AreEqual(InterceptorRole.Cookie, ((IInterceptorRole)cookie).Role);
        }

        [Test]
        public void ThirdPartyCacheRole_OutsideAuth_IsRejected()
        {
            var ex = Assert.Throws<ArgumentException>(() => CreateClient(
                new RoleInterceptor(InterceptorRole.Cache),
                new AuthInterceptor(new StaticTokenProvider("secret-token"))));

            StringAssert.Contains(nameof(RoleInterceptor), ex.Message);
            StringAssert.DoesNotContain("secret-token", ex.Message);
        }

        [Test]
        public void ThirdPartyCookieRole_InsideCache_IsRejected()
        {
            Assert.Throws<ArgumentException>(() => CreateClient(
                new CacheInterceptor(new CachePolicy()),
                new RoleInterceptor(InterceptorRole.Cookie)));
        }

        [Test]
        public void ThirdPartyRoles_InValidOrder_AreAccepted()
        {
            using (CreateClient(
                new RoleInterceptor(InterceptorRole.Redirect),
                new RoleInterceptor(InterceptorRole.Cookie),
                new RoleInterceptor(InterceptorRole.Auth),
                new RoleInterceptor(InterceptorRole.Cache)))
            {
            }
        }

        [Test]
        public void PluginContributingCookieInsideBaseCache_IsRejected()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient(new CacheInterceptor(new CachePolicy()));
                // Not every capability: the contribution is wrapped, and the wrapper must keep its role.
                var plugin = new ContributingPlugin(
                    new CookieInterceptor(),
                    PluginCapabilities.MutateRequests | PluginCapabilities.MutateResponses);

                var error = await TestHelpers.AssertThrowsAsync<PluginException>(
                    () => client.RegisterPluginAsync(plugin));

                StringAssert.Contains("CacheInterceptor", error.Message);
                Assert.AreEqual(0, client.GetRegisteredPlugins().Count);
            });
        }

        private static UHttpClient CreateClient(params IHttpInterceptor[] interceptors)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = new MockTransport(),
                DisposeTransport = true,
                Interceptors = new List<IHttpInterceptor>(interceptors)
            });
        }

        /// <summary>A custom (non first-party) interceptor that declares its role through the public contract.</summary>
        private sealed class RoleInterceptor : IHttpInterceptor, IInterceptorRole
        {
            public RoleInterceptor(InterceptorRole role)
            {
                Role = role;
            }

            public InterceptorRole Role { get; }

            public DispatchFunc Wrap(DispatchFunc next)
            {
                return next;
            }
        }

        private sealed class ContributingPlugin : IHttpPlugin
        {
            private readonly IHttpInterceptor _interceptor;

            public ContributingPlugin(IHttpInterceptor interceptor, PluginCapabilities capabilities)
            {
                _interceptor = interceptor;
                Capabilities = capabilities;
            }

            public string Name => "cookie-plugin";
            public string Version => "1.0.0";
            public PluginCapabilities Capabilities { get; }

            public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
            {
                context.RegisterInterceptor(_interceptor);
                return default;
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken)
            {
                return default;
            }
        }
    }
}
