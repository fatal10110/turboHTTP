using System;
using System.Collections.Generic;

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
        }

        public Uri ProxyUri { get; }

        public ProxyCredentials? Credentials { get; }

        public IReadOnlyList<string> BypassList { get; }

        public bool IsConfigured => ProxyUri != null;

        public bool ShouldBypass(string host)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(host) || BypassList == null || BypassList.Count == 0)
                return false;

            for (int i = 0; i < BypassList.Count; i++)
            {
                string rule = BypassList[i];
                if (string.IsNullOrWhiteSpace(rule))
                    continue;

                string trimmed = rule.Trim();
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
    }
}
