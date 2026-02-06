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
                // Authenticate as client
                if (_alpnSupported && alpnProtocols != null && alpnProtocols.Length > 0)
                {
                    await AuthenticateWithAlpnAsync(sslStream, host, alpnProtocols, ct).ConfigureAwait(false);
                }
                else
                {
                    // Fallback: no ALPN
#pragma warning disable SYSLIB0039 // SslProtocols is obsolete in .NET 7+
                    await sslStream.AuthenticateAsClientAsync(
                        host,
                        clientCertificates: null,
                        enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                        checkCertificateRevocation: false).ConfigureAwait(false);
#pragma warning restore SYSLIB0039
                }

                // Extract ALPN result
                string negotiatedAlpn = null;
                if (_alpnSupported)
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
            // Create SslClientAuthenticationOptions via reflection
            var optionsType = typeof(SslStream).Assembly.GetType(
                "System.Net.Security.SslClientAuthenticationOptions");
            var options = Activator.CreateInstance(optionsType);

            // Set TargetHost
            optionsType.GetProperty("TargetHost").SetValue(options, host);

            // Set EnabledSslProtocols
#pragma warning disable SYSLIB0039 // SslProtocols is obsolete in .NET 7+
            optionsType.GetProperty("EnabledSslProtocols").SetValue(
                options, SslProtocols.Tls12 | SslProtocols.Tls13);
#pragma warning restore SYSLIB0039

            // Set ApplicationProtocols (List<SslApplicationProtocol>)
            var alpnListType = typeof(SslStream).Assembly.GetType(
                "System.Net.Security.SslApplicationProtocol");

            if (alpnListType != null)
            {
                var alpnList = typeof(System.Collections.Generic.List<>)
                    .MakeGenericType(alpnListType)
                    .GetConstructor(Type.EmptyTypes)
                    .Invoke(null);

                var addMethod = alpnList.GetType().GetMethod("Add");

                foreach (var protocol in alpnProtocols)
                {
                    // Get well-known protocol values (Http2, Http11)
                    string fieldName;
                    if (protocol == "h2")
                        fieldName = "Http2";
                    else if (protocol == "http/1.1")
                        fieldName = "Http11";
                    else
                        continue;

                    var field = alpnListType.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
                    if (field != null)
                    {
                        addMethod.Invoke(alpnList, new[] { field.GetValue(null) });
                    }
                }

                _alpnOptionsProperty.SetValue(options, alpnList);
            }

            // AuthenticateAsClientAsync(SslClientAuthenticationOptions, CancellationToken)
            var authMethod = typeof(SslStream).GetMethod(
                "AuthenticateAsClientAsync",
                new[] { optionsType, typeof(CancellationToken) });

            var task = (Task)authMethod.Invoke(sslStream, new[] { options, ct });
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
            // Accept all certificates for now (basic validation)
            // TODO: In production, implement proper validation:
            //  - Check sslPolicyErrors
            //  - Verify certificate chain
            //  - Implement certificate pinning (Phase 6)
            return sslPolicyErrors == SslPolicyErrors.None;
        }

#pragma warning disable SYSLIB0039 // SslProtocols is obsolete in .NET 7+
        private string FormatTlsVersion(SslProtocols protocol)
        {
            return protocol switch
            {
                SslProtocols.Tls12 => "1.2",
                SslProtocols.Tls13 => "1.3",
                _ => protocol.ToString()
            };
        }
#pragma warning restore SYSLIB0039
    }
}
