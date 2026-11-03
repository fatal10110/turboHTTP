using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace TurboHTTP.Core
{
    public sealed class ProxySettings
    {
        public Uri Address { get; set; }
        public NetworkCredential Credentials { get; set; }
        /// <summary>
        /// Hosts that skip the proxy. Rules (case-insensitive, optional <c>:port</c> suffix):
        /// <list type="bullet">
        /// <item><c>example.com</c>: that host only.</item>
        /// <item><c>.example.com</c>: <c>example.com</c> and every subdomain (curl <c>NO_PROXY</c> semantics).</item>
        /// <item><c>*.example.com</c>: subdomains only; the apex <c>example.com</c> is not bypassed.</item>
        /// <item><c>10.0.0.0/8</c>, <c>fd00::/8</c>: CIDR, matched only against IP-literal hosts;
        /// a host name is never resolved to test it (curl does the same).</item>
        /// </list>
        /// <c>NO_PROXY</c>/<c>no_proxy</c> entries (comma-separated) use the same rules.
        /// </summary>
        public IReadOnlyList<string> BypassList { get; set; } = Array.Empty<string>();
        public bool UseEnvironmentVariables { get; set; } = true;
        public bool AllowHttpProxyFallbackForHttps { get; set; }
        public bool AllowPlaintextProxyAuth { get; set; }

        public ProxySettings Clone()
        {
            return new ProxySettings
            {
                Address = Address,
                Credentials = Credentials != null
                    ? new NetworkCredential(Credentials.UserName, Credentials.Password, Credentials.Domain)
                    : null,
                BypassList = BypassList != null ? BypassList.ToArray() : Array.Empty<string>(),
                UseEnvironmentVariables = UseEnvironmentVariables,
                AllowHttpProxyFallbackForHttps = AllowHttpProxyFallbackForHttps,
                AllowPlaintextProxyAuth = AllowPlaintextProxyAuth
            };
        }

        public void Validate()
        {
            if (Address == null)
            {
                if (Credentials != null)
                {
                    throw new ArgumentException(
                        "Proxy credentials cannot be set when Address is null.",
                        nameof(Credentials));
                }

                return;
            }

            if (!string.Equals(Address.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Only http:// proxy addresses are supported.", nameof(Address));
            }
        }
    }

    public static class ProxyBypassMatcher
    {
        public static bool IsBypassed(string host, int port, IReadOnlyList<string> bypassRules)
        {
            if (string.IsNullOrWhiteSpace(host) || bypassRules == null || bypassRules.Count == 0)
                return false;

            for (int i = 0; i < bypassRules.Count; i++)
            {
                var rule = bypassRules[i];
                if (string.IsNullOrWhiteSpace(rule))
                    continue;

                if (MatchesRule(host, port, rule.Trim()))
                    return true;
            }

            return false;
        }

        private static bool MatchesRule(string host, int port, string rule)
        {
            string hostPart = rule;
            int? rulePort = null;

            var lastColon = rule.LastIndexOf(':');
            if (lastColon > 0 && lastColon < rule.Length - 1 && int.TryParse(rule.Substring(lastColon + 1), out var parsedPort))
            {
                hostPart = rule.Substring(0, lastColon);
                rulePort = parsedPort;
            }

            if (rulePort.HasValue && rulePort.Value != port)
                return false;

            if (hostPart.StartsWith("*.", StringComparison.Ordinal))
            {
                // Wildcard: subdomains only. "*" stands for at least one label, so the apex
                // ("example.com" for "*.example.com") does not match (#173).
                var suffix = hostPart.Substring(1);
                return host.Length > suffix.Length
                    && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
            }

            if (hostPart.StartsWith(".", StringComparison.Ordinal))
            {
                return host.EndsWith(hostPart, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(host, hostPart.Substring(1), StringComparison.OrdinalIgnoreCase);
            }

            if (hostPart.Contains("/"))
            {
                // CIDR rules apply to IP-literal hosts only; host names are not resolved here.
                return MatchesCidr(host, hostPart);
            }

            return string.Equals(host, hostPart, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesCidr(string host, string cidr)
        {
            if (!IPAddress.TryParse(host, out var hostIp))
                return false;

            var slash = cidr.IndexOf('/');
            if (slash <= 0 || slash >= cidr.Length - 1)
                return false;

            if (!IPAddress.TryParse(cidr.Substring(0, slash), out var network))
                return false;
            if (!int.TryParse(cidr.Substring(slash + 1), out var bits))
                return false;

            if (hostIp.AddressFamily != network.AddressFamily)
            {
                if (hostIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                    hostIp.IsIPv4MappedToIPv6)
                {
                    hostIp = hostIp.MapToIPv4();
                }

                if (network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                    network.IsIPv4MappedToIPv6)
                {
                    network = network.MapToIPv4();
                }
            }

            var hostBytes = hostIp.GetAddressBytes();
            var netBytes = network.GetAddressBytes();
            if (hostBytes.Length != netBytes.Length)
                return false;
            var maxBits = hostBytes.Length * 8;
            if (bits < 0 || bits > maxBits)
                return false;

            var fullBytes = bits / 8;
            var remBits = bits % 8;

            for (int i = 0; i < fullBytes; i++)
            {
                if (hostBytes[i] != netBytes[i])
                    return false;
            }

            if (remBits > 0)
            {
                int mask = 0xFF << (8 - remBits);
                if ((hostBytes[fullBytes] & mask) != (netBytes[fullBytes] & mask))
                    return false;
            }

            return true;
        }
    }

    public static class ProxyEnvironmentResolver
    {
        public static ProxySettings Resolve(Uri targetUri, ProxySettings configured)
        {
            if (targetUri == null)
                throw new ArgumentNullException(nameof(targetUri));

            configured?.Validate();

            if (configured != null && configured.Address != null)
            {
                if (ProxyBypassMatcher.IsBypassed(targetUri.Host, targetUri.Port, configured.BypassList))
                    return null;
                return configured;
            }

            if (configured != null && !configured.UseEnvironmentVariables)
                return null;

            var envValue = ResolveEnvironmentProxy(
                targetUri.Scheme,
                configured?.AllowHttpProxyFallbackForHttps ?? false,
                out var envName);
            if (string.IsNullOrWhiteSpace(envValue))
                return null;

            if (!Uri.TryCreate(envValue, UriKind.Absolute, out var proxyUri))
                return null;

            var noProxy = Environment.GetEnvironmentVariable("NO_PROXY")
                ?? Environment.GetEnvironmentVariable("no_proxy");
            var bypassRules = ParseNoProxyRules(noProxy);
            if (ProxyBypassMatcher.IsBypassed(targetUri.Host, targetUri.Port, bypassRules))
                return null;

            if (!string.Equals(proxyUri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                // Typed failure instead of the raw ArgumentException of Validate() (#173). The
                // value is not echoed: proxy URLs can carry user:password.
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.InvalidRequest,
                    "The " + envName + " environment variable names a proxy that is not http://; only http:// proxies are supported. " +
                    "Configure UHttpClientOptions.Proxy, or set ProxySettings.UseEnvironmentVariables = false to ignore the environment."));
            }

            var resolved = new ProxySettings
            {
                Address = proxyUri,
                BypassList = bypassRules,
                UseEnvironmentVariables = true,
                AllowHttpProxyFallbackForHttps = configured?.AllowHttpProxyFallbackForHttps ?? false
            };

            return resolved;
        }

        private static string ResolveEnvironmentProxy(string scheme, bool allowHttpFallbackForHttps, out string envName)
        {
            if (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                var httpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                    ?? Environment.GetEnvironmentVariable("https_proxy");
                if (!string.IsNullOrWhiteSpace(httpsProxy))
                {
                    envName = "HTTPS_PROXY";
                    return httpsProxy;
                }

                if (!allowHttpFallbackForHttps)
                {
                    envName = null;
                    return null;
                }
            }

            envName = "HTTP_PROXY";
            return Environment.GetEnvironmentVariable("HTTP_PROXY")
                ?? Environment.GetEnvironmentVariable("http_proxy");
        }

        private static IReadOnlyList<string> ParseNoProxyRules(string noProxy)
        {
            if (string.IsNullOrWhiteSpace(noProxy))
                return Array.Empty<string>();

            var tokens = noProxy.Split(',');
            var result = new List<string>(tokens.Length);
            for (int i = 0; i < tokens.Length; i++)
            {
                var trimmed = tokens[i]?.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    result.Add(trimmed);
            }

            return result;
        }
    }
}
