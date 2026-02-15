using System;
using System.IO;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// TLS provider using System.Net.Security.SslStream.
    /// Uses reflection to access ALPN APIs on .NET Standard 2.1.
    /// </summary>
    internal sealed class SslStreamTlsProvider : ITlsProvider
    {
        public static readonly SslStreamTlsProvider Instance = new();

        public string ProviderName => "SslStream";

        private static readonly PropertyInfo _alpnOptionsProperty;
        private static readonly PropertyInfo _alpnNegotiatedProperty;
        private static readonly bool _alpnSupported;

        // Cached reflection for ALPN auth path — avoids per-connection reflection overhead
        private static readonly Type _optionsType;
        private static readonly PropertyInfo _targetHostProp;
        private static readonly PropertyInfo _enabledProtocolsProp;
        private static readonly Type _alpnProtocolType;
        private static readonly MethodInfo _authWithOptionsMethod;
        private static readonly object _h2ProtocolValue;
        private static readonly object _http11ProtocolValue;

        static SslStreamTlsProvider()
        {
            // Reflect into SslClientAuthenticationOptions.ApplicationProtocols
            var optionsType = typeof(SslStream).Assembly.GetType(
                "System.Net.Security.SslClientAuthenticationOptions");

            if (optionsType != null)
            {
                _alpnOptionsProperty = optionsType.GetProperty(
                    "ApplicationProtocols",
                    BindingFlags.Public | BindingFlags.Instance);
            }

            // Reflect into SslStream.NegotiatedApplicationProtocol
            _alpnNegotiatedProperty = typeof(SslStream).GetProperty(
                "NegotiatedApplicationProtocol",
                BindingFlags.Public | BindingFlags.Instance);

            _alpnSupported = _alpnOptionsProperty != null && _alpnNegotiatedProperty != null;

            // Cache remaining ALPN auth reflection for per-connection reuse
            if (_alpnSupported && optionsType != null)
            {
                _optionsType = optionsType;
                _targetHostProp = optionsType.GetProperty("TargetHost");
                _enabledProtocolsProp = optionsType.GetProperty("EnabledSslProtocols");
                _alpnProtocolType = typeof(SslStream).Assembly.GetType(
                    "System.Net.Security.SslApplicationProtocol");
                _authWithOptionsMethod = typeof(SslStream).GetMethod(
                    "AuthenticateAsClientAsync",
                    new[] { optionsType, typeof(CancellationToken) });

                if (_alpnProtocolType != null)
                {
                    _h2ProtocolValue = _alpnProtocolType.GetField("Http2",
                        BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    _http11ProtocolValue = _alpnProtocolType.GetField("Http11",
                        BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                }
            }
        }

        public bool IsAlpnSupported() => _alpnSupported;

        public async Task<TlsResult> WrapAsync(
            Stream innerStream,
            string host,
            string[] alpnProtocols,
            CancellationToken ct)
        {
            var sslStream = new SslStream(
                innerStream,
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: ValidateServerCertificate);

            try
            {
                bool alpnAttempted = false;

                // Authenticate as client
                if (_alpnSupported && alpnProtocols != null && alpnProtocols.Length > 0)
                {
                    try
                    {
                        await AuthenticateWithAlpnAsync(sslStream, host, alpnProtocols, ct).ConfigureAwait(false);
                        alpnAttempted = true;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        // ALPN reflection failed (e.g. MakeGenericType under IL2CPP/AOT).
                        // Since BouncyCastle is available as a fallback TLS provider with
                        // guaranteed ALPN support, we fall back to non-ALPN authentication
                        // here. TlsProviderSelector.Auto will route to BouncyCastle on
                        // platforms where this path consistently fails.
                        alpnAttempted = false;
                    }
                }

                if (!alpnAttempted)
                {
                    // Non-ALPN path. The 4-param AuthenticateAsClientAsync overload does NOT
                    // accept a CancellationToken. We use Task.WhenAny with a cancellation-
                    // aware delay to abandon the await when the outer timeout fires.
                    // The underlying handshake may continue to completion on its thread,
                    // but the SslStream will be disposed by the caller (catch block below).
#pragma warning disable SYSLIB0039 // SslProtocols is obsolete in .NET 7+
                    var authTask = sslStream.AuthenticateAsClientAsync(
                        host,
                        clientCertificates: null,
                        enabledSslProtocols: SslProtocols.Tls12 | (SslProtocols)0x3000 /* Tls13 */,
                        checkCertificateRevocation: false);
#pragma warning restore SYSLIB0039

                    var cancelTask = Task.Delay(Timeout.Infinite, ct);
                    var completed = await Task.WhenAny(authTask, cancelTask).ConfigureAwait(false);

                    if (completed == cancelTask || ct.IsCancellationRequested)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    // Observe auth result (propagate exceptions)
                    await authTask.ConfigureAwait(false);
                }

                // Extract ALPN result
                string negotiatedAlpn = null;
                if (alpnAttempted && _alpnSupported)
                {
                    negotiatedAlpn = ExtractAlpnResult(sslStream);
                }

                return new TlsResult(
                    secureStream: sslStream,
                    negotiatedAlpn: negotiatedAlpn,
                    tlsVersion: FormatTlsVersion(sslStream.SslProtocol),
                    cipherSuite: null, // SslStream doesn't expose this easily
                    providerName: ProviderName);
            }
            catch
            {
                sslStream.Dispose();
                throw;
            }
        }

        private async Task AuthenticateWithAlpnAsync(
            SslStream sslStream,
            string host,
            string[] alpnProtocols,
            CancellationToken ct)
        {
            // All reflection results are cached in static fields (see static ctor).
            // Only Activator.CreateInstance + property sets run per connection.
            if (_optionsType == null || _targetHostProp == null || _authWithOptionsMethod == null)
                throw new PlatformNotSupportedException("SslClientAuthenticationOptions not available");

            var options = Activator.CreateInstance(_optionsType);

            _targetHostProp.SetValue(options, host);

            if (_enabledProtocolsProp != null)
            {
#pragma warning disable SYSLIB0039 // SslProtocols is obsolete in .NET 7+
                _enabledProtocolsProp.SetValue(
                    options, SslProtocols.Tls12 | (SslProtocols)0x3000 /* Tls13 */);
#pragma warning restore SYSLIB0039
            }

            // Set ApplicationProtocols (List<SslApplicationProtocol>)
            // NOTE: MakeGenericType can fail under IL2CPP/AOT if the generic instantiation
            // List<SslApplicationProtocol> was not preserved. The caller catches this and
            // falls back to non-ALPN auth. BouncyCastle provides ALPN on those platforms.
            if (_alpnProtocolType != null)
            {
                var alpnList = typeof(System.Collections.Generic.List<>)
                    .MakeGenericType(_alpnProtocolType)
                    .GetConstructor(Type.EmptyTypes)
                    .Invoke(null);

                var addMethod = alpnList.GetType().GetMethod("Add");

                foreach (var protocol in alpnProtocols)
                {
                    object value;
                    if (protocol == "h2")
                        value = _h2ProtocolValue;
                    else if (protocol == "http/1.1")
                        value = _http11ProtocolValue;
                    else
                        continue;

                    if (value != null)
                        addMethod.Invoke(alpnList, new[] { value });
                }

                _alpnOptionsProperty.SetValue(options, alpnList);
            }

            var task = (Task)_authWithOptionsMethod.Invoke(sslStream, new[] { options, ct });
            await task.ConfigureAwait(false);
        }

        private string ExtractAlpnResult(SslStream sslStream)
        {
            try
            {
                var alpnResult = _alpnNegotiatedProperty?.GetValue(sslStream);
                if (alpnResult == null)
                    return null;

                // NegotiatedApplicationProtocol is SslApplicationProtocol struct
                // Use ToString() to get the protocol string
                var toStringMethod = alpnResult.GetType().GetMethod("ToString", Type.EmptyTypes);
                var result = toStringMethod?.Invoke(alpnResult, null) as string;

                return string.IsNullOrEmpty(result) ? null : result;
            }
            catch
            {
                return null;
            }
        }

        private bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            // OS-level certificate validation. Returns true only if the certificate
            // chain validates successfully with no policy errors (hostname mismatch,
            // untrusted root, expired cert, etc. all cause SslPolicyErrors != None).
            // Certificate pinning support is deferred to a future phase.
            return sslPolicyErrors == SslPolicyErrors.None;
        }

#pragma warning disable SYSLIB0039 // SslProtocols is obsolete in .NET 7+
        private string FormatTlsVersion(SslProtocols protocol)
        {
            if (protocol == SslProtocols.Tls12)
                return "1.2";
            if (protocol == (SslProtocols)0x3000) // Tls13
                return "1.3";
            return protocol.ToString();
        }
#pragma warning restore SYSLIB0039
    }
}
