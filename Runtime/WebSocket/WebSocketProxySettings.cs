using System;
using System.Collections.Generic;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.WebSocket
{
    public readonly struct ProxyCredentials
    {
        public ProxyCredentials(string username, string password)
        {
            Username = username ?? string.Empty;
            Password = password ?? string.Empty;
        }

        public string Username { get; }

        public string Password { get; }
    }

    public sealed class WebSocketProxySettings
    {
        private static readonly IReadOnlyList<string> EmptyBypassList = Array.Empty<string>();

        // BypassList rules in match form (#368): trimmed, a Unicode name (or the name after "*.") in
        // A-label form; null for a blank rule. Computed once: the settings are immutable.
        private readonly string[] _matchRules = Array.Empty<string>();

        public static readonly WebSocketProxySettings None = new WebSocketProxySettings();

        public WebSocketProxySettings()
        {
            ProxyUri = null;
            Credentials = null;
            BypassList = EmptyBypassList;
        }

        public WebSocketProxySettings(
            Uri proxyUri,
            ProxyCredentials? credentials = null,
            IReadOnlyList<string> bypassList = null)
        {
            if (proxyUri == null)
                throw new ArgumentNullException(nameof(proxyUri));
            if (!proxyUri.IsAbsoluteUri)
                throw new ArgumentException("Proxy URI must be absolute.", nameof(proxyUri));
            if (!string.Equals(proxyUri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Only http:// proxy endpoints are supported in this release.",
                    nameof(proxyUri));
            }
            if (string.IsNullOrWhiteSpace(proxyUri.Host))
                throw new ArgumentException("Proxy URI host is required.", nameof(proxyUri));

            ProxyUri = proxyUri;
            Credentials = credentials;
            BypassList = bypassList != null
                ? new List<string>(bypassList)
                : EmptyBypassList;

            _matchRules = new string[BypassList.Count];
            for (int i = 0; i < _matchRules.Length; i++)
                _matchRules[i] = ToMatchRule(BypassList[i]);
        }

        public Uri ProxyUri { get; }

        public ProxyCredentials? Credentials { get; }

        public IReadOnlyList<string> BypassList { get; }

        public bool IsConfigured => ProxyUri != null;

        /// <summary>
        /// Whether a connection to <paramref name="host"/> skips the proxy: a <see cref="BypassList"/>
        /// rule equals the host, or a <c>*.domain</c> rule matches a subdomain of <c>domain</c> (not
        /// <c>domain</c> itself), case-insensitively. An internationalized name is compared in its
        /// A-label form on both sides (RFC 5890 Section 2.3.2.1), so <c>münchen.de</c> and
        /// <c>xn--mnchen-3ya.de</c> match each other; ASCII names and IP literals are compared as given.
        /// </summary>
        /// <param name="host">Target host without port, as a name, IPv4 literal or bare IPv6 literal.</param>
        public bool ShouldBypass(string host)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(host) || _matchRules.Length == 0)
                return false;

            host = ToALabel(host);

            for (int i = 0; i < _matchRules.Length; i++)
            {
                string trimmed = _matchRules[i];
                if (trimmed == null)
                    continue;

                if (trimmed.StartsWith("*.", StringComparison.Ordinal))
                {
                    string suffix = trimmed.Substring(1); // ".example.com"
                    if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                        host.Length > suffix.Length)
                    {
                        return true;
                    }

                    continue;
                }

                if (string.Equals(host, trimmed, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string ToMatchRule(string rule)
        {
            if (string.IsNullOrWhiteSpace(rule))
                return null;

            string trimmed = rule.Trim();
            return trimmed.StartsWith("*.", StringComparison.Ordinal)
                ? "*." + ToALabel(trimmed.Substring(2))
                : ToALabel(trimmed);
        }

        // A non-ASCII name in A-label form; anything else (ASCII, or a name without a valid A-label,
        // which then matches only its own spelling) unchanged. IPv6 literals are ASCII, so their
        // spelling (brackets) is not rewritten.
        private static string ToALabel(string name)
        {
            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] > 0x7F)
                {
                    return WireHost.TryFormat(name, out var aLabel, out _) ? aLabel : name;
                }
            }

            return name;
        }
    }
}
