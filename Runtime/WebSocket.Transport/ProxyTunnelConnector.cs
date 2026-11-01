using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Http1;
using TurboHTTP.WebSocket;

namespace TurboHTTP.WebSocket.Transport
{
    internal static class ProxyTunnelConnector
    {
        /// <summary>
        /// Sends CONNECT over <paramref name="stream"/> (the cleartext leg to an http:// proxy) and
        /// returns the stream once the proxy reports a 2xx. On a 407 the request is retried once
        /// with Basic credentials, and only when the proxy offers the Basic scheme
        /// (RFC 9110 section 11.7.1) and <paramref name="allowCleartextCredentials"/> permits
        /// sending them unencrypted (RFC 7617 section 4).
        /// </summary>
        public static async Task<Stream> EstablishAsync(
            Stream stream,
            Uri targetUri,
            WebSocketProxySettings proxySettings,
            bool allowCleartextCredentials,
            CancellationToken ct)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (targetUri == null)
                throw new ArgumentNullException(nameof(targetUri));
            if (proxySettings == null)
                throw new ArgumentNullException(nameof(proxySettings));

            int targetPort = targetUri.IsDefaultPort
                ? (string.Equals(targetUri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                : targetUri.Port;

            string authority = targetUri.Host + ":" + targetPort;
            bool attemptedAuth = false;

            while (true)
            {
                string proxyAuthorization = null;
                if (attemptedAuth && proxySettings.Credentials.HasValue)
                {
                    proxyAuthorization = BuildBasicAuthorization(proxySettings.Credentials.Value);
                    TurboDiagnostics.Log(
                        "[TurboHTTP] Using Basic proxy authentication over an unencrypted proxy connection.");
                }

                try
                {
                    await WriteConnectRequestAsync(stream, authority, proxyAuthorization, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    attemptedAuth &&
                    !(ex is OperationCanceledException))
                {
                    throw new WebSocketException(
                        WebSocketError.ProxyTunnelFailed,
                        "Proxy closed the connection while retrying CONNECT after 407.",
                        ex);
                }

                var response = await ReadReplyAsync(stream, ct).ConfigureAwait(false);

                // RFC 9110 section 9.3.6: any 2xx switches to tunnel mode right after the header
                // block. The shared reader stops there, so every following byte (including any the
                // proxy announced with Content-Length) stays on the stream for the tunnel (#166).
                if (response.IsTunnelEstablished)
                    return stream;

                if (response.StatusCode == 407)
                {
                    if (!proxySettings.Credentials.HasValue)
                    {
                        throw new WebSocketException(
                            WebSocketError.ProxyAuthenticationRequired,
                            "Proxy authentication is required (407) but no credentials were configured.");
                    }

                    if (attemptedAuth)
                    {
                        throw new WebSocketException(
                            WebSocketError.ProxyTunnelFailed,
                            "Proxy rejected CONNECT after Basic authentication.");
                    }

                    // RFC 9110 section 11.7.1: answer only a challenge the client supports. Basic
                    // credentials go out only when the proxy offers Basic (#85).
                    if (!AuthChallengeParser.OffersScheme(response.Headers.GetValues("Proxy-Authenticate"), "Basic"))
                    {
                        throw new WebSocketException(
                            WebSocketError.ProxyAuthenticationRequired,
                            "Proxy authentication is required (407) but the proxy does not offer the Basic scheme; " +
                            "credentials were not sent.");
                    }

                    // RFC 7617 section 4: Basic over an unencrypted proxy leg exposes the
                    // credentials; it needs the explicit cleartext opt-in (#85).
                    if (!allowCleartextCredentials)
                    {
                        throw new WebSocketException(
                            WebSocketError.CleartextNotPermitted,
                            "Proxy requires Basic authentication, which would send the credentials over a cleartext " +
                            "proxy connection; credentials were not sent. Set WebSocketConnectionOptions.AllowCleartext = true to allow it.");
                    }

                    // One reuse rule with the HTTP transport (#229). This stack works on a single
                    // stream, so a reply that is not reusable fails instead of reconnecting.
                    if (!response.CanRetryOnSameConnection)
                    {
                        throw new WebSocketException(
                            WebSocketError.ProxyTunnelFailed,
                            "Proxy 407 response cannot be safely retried on the same connection " +
                            "(unsupported body framing or connection close).");
                    }

                    await DrainReplyBodyAsync(stream, response, ct).ConfigureAwait(false);

                    // Assumes proxies keep the TCP connection alive after 407 when framing permits reuse.
                    attemptedAuth = true;
                    continue;
                }

                throw new WebSocketException(
                    WebSocketError.ProxyTunnelFailed,
                    "Proxy CONNECT failed with status code " + response.StatusCode + ".");
            }
        }

        private static async Task WriteConnectRequestAsync(
            Stream stream,
            string authority,
            string proxyAuthorization,
            CancellationToken ct)
        {
            var builder = new StringBuilder(256);
            builder.Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\n");
            builder.Append("Host: ").Append(authority).Append("\r\n");
            builder.Append("Proxy-Connection: Keep-Alive\r\n");
            if (!string.IsNullOrWhiteSpace(proxyAuthorization))
                builder.Append("Proxy-Authorization: ").Append(proxyAuthorization).Append("\r\n");
            builder.Append("\r\n");

            byte[] requestBytes = Encoding.ASCII.GetBytes(builder.ToString());
            await stream.WriteAsync(requestBytes, 0, requestBytes.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static async Task<ConnectReply> ReadReplyAsync(Stream stream, CancellationToken ct)
        {
            try
            {
                return await ConnectReplyReader.ReadAsync(stream, ct).ConfigureAwait(false);
            }
            catch (UHttpException ex) when (!(ex is WebSocketException))
            {
                throw new WebSocketException(WebSocketError.ProxyTunnelFailed, ex.HttpError.Message, ex);
            }
        }

        private static async Task DrainReplyBodyAsync(Stream stream, ConnectReply reply, CancellationToken ct)
        {
            try
            {
                await ConnectReplyReader.DrainBodyAsync(stream, reply, ct).ConfigureAwait(false);
            }
            catch (UHttpException ex) when (!(ex is WebSocketException))
            {
                throw new WebSocketException(WebSocketError.ProxyTunnelFailed, ex.HttpError.Message, ex);
            }
        }

        private static string BuildBasicAuthorization(ProxyCredentials credentials)
        {
            string userPass = credentials.Username + ":" + credentials.Password;
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(userPass));
            return "Basic " + base64;
        }
    }
}
