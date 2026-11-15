using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Auth
{
    /// <summary>
    /// #741: redirect_uri goes out exactly as registered (RFC 6749 sections 3.1.2.3 and 4.1.3,
    /// RFC 8252 section 7.3), and endpoint URLs are not re-normalised through <c>Uri.ToString()</c>.
    /// </summary>
    [TestFixture]
    public class OAuthRedirectUriTests
    {
        private static readonly string[] RegisteredRedirects =
        {
            "http://localhost:7890",
            "myapp://callback",
            "myapp://cb?x=a%20b&y=%41"
        };

        [Test]
        public void AuthorizationRequest_SendsRegisteredRedirectUri()
        {
            AssertAsync.Run(async () =>
            {
                using var oauth = new OAuthClient(new UHttpClient(new UHttpClientOptions
                {
                    Transport = new MockTransport(),
                    DisposeTransport = true
                }));

                foreach (var registered in RegisteredRedirects)
                {
                    var request = await oauth.CreateAuthorizationRequestAsync(
                        CreateConfig(registered), CancellationToken.None);

                    Assert.AreEqual(registered, QueryValue(request.AuthorizationUri.Query, "redirect_uri"),
                        "authorization request redirect_uri for " + registered);
                }
            });
        }

        [Test]
        public void AuthorizationRequest_KeepsEndpointQueryVerbatim()
        {
            AssertAsync.Run(async () =>
            {
                using var oauth = new OAuthClient(new UHttpClient(new UHttpClientOptions
                {
                    Transport = new MockTransport(),
                    DisposeTransport = true
                }));

                var config = CreateConfig("http://localhost:7890");
                config.AuthorizationEndpoint = new Uri("https://auth.example.com/authorize?tenant=a%2Fb");

                var request = await oauth.CreateAuthorizationRequestAsync(config, CancellationToken.None);

                StringAssert.StartsWith(
                    "https://auth.example.com/authorize?tenant=a%2Fb&",
                    request.AuthorizationUri.OriginalString);
            });
        }

        [Test]
        public void CodeExchange_SendsRegisteredRedirectUri_ToConfiguredEndpoint()
        {
            AssertAsync.Run(async () =>
            {
                const string tokenEndpoint = "https://auth.example.com/token?tenant=%41";
                var forms = new List<string>();
                var uris = new List<string>();
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    forms.Add(req.GetBodyAsString());
                    uris.Add(req.Uri.OriginalString);
                    var headers = new HttpHeaders();
                    headers.Set("Content-Type", "application/json");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        headers,
                        Encoding.UTF8.GetBytes("{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":3600}"),
                        ctx.Elapsed,
                        req));
                });

                using var oauth = new OAuthClient(new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                }));

                foreach (var registered in RegisteredRedirects)
                {
                    var config = CreateConfig(registered);
                    config.TokenEndpoint = new Uri(tokenEndpoint);
                    await oauth.ExchangeCodeAsync(new OAuthCodeExchangeRequest
                    {
                        Config = config,
                        AuthorizationCode = "code-1",
                        CodeVerifier = "verifier"
                    }, CancellationToken.None);

                    Assert.AreEqual(registered, QueryValue(forms[forms.Count - 1], "redirect_uri"),
                        "token request redirect_uri for " + registered);
                    Assert.AreEqual(tokenEndpoint, uris[uris.Count - 1], "token endpoint URL");
                }
            });
        }

        [Test]
        public void Discovery_RequestsConfiguredEndpoint()
        {
            AssertAsync.Run(async () =>
            {
                const string discovery = "https://auth.example.com/.well-known/openid-configuration?tenant=%41";
                string requested = null;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    requested = req.Uri.OriginalString;
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Encoding.UTF8.GetBytes("{\"issuer\":\"https://auth.example.com\"}"),
                        ctx.Elapsed,
                        req));
                });

                using var oauth = new OAuthClient(new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                }));

                await oauth.DiscoverAsync(new Uri(discovery), CancellationToken.None);

                Assert.AreEqual(discovery, requested);
            });
        }

        private static OAuthConfig CreateConfig(string redirect)
        {
            return new OAuthConfig
            {
                ClientId = "client-1",
                AuthorizationEndpoint = new Uri("https://auth.example.com/authorize"),
                TokenEndpoint = new Uri("https://auth.example.com/token"),
                RedirectUri = new Uri(redirect),
                Scopes = new[] { "profile" },
                UsePkce = true
            };
        }

        // Decodes one application/x-www-form-urlencoded value from a query ("?a=b&c=d") or form body.
        private static string QueryValue(string query, string name)
        {
            var text = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
            foreach (var pair in text.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0)
                    continue;
                if (Uri.UnescapeDataString(pair.Substring(0, eq)) == name)
                    return Uri.UnescapeDataString(pair.Substring(eq + 1));
            }

            Assert.Fail(name + " missing from " + query);
            return null;
        }
    }
}
