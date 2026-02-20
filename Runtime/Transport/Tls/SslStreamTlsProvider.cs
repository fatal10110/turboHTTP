using System;
using System.IO;
using System.Linq.Expressions;
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
    /// On IL2CPP with aggressive stripping, preserve SslStream ALPN types (e.g., via
    /// link.xml or [Preserve]) to keep HTTP/2 ALPN negotiation available. Without that
    /// preservation, TurboHTTP falls back to BouncyCastle in Auto mode.
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
        private static readonly ConstructorInfo _alpnProtocolListCtor;
        private static readonly MethodInfo _alpnProtocolListAddMethod;
        private static readonly MethodInfo _authWithOptionsMethod;
        private static readonly object _h2ProtocolValue;
        private static readonly object _http11ProtocolValue;
        private static readonly ConstructorInfo _optionsCtor;
        private static readonly Func<object> _createOptionsInstance;

#pragma warning disable SYSLIB0039 // SslProtocols is obsolete in .NET 7+
        // TLS 1.3 bitmask used where the enum member may be unavailable at compile time
        // on older Unity/Mono profiles, but is accepted by runtime TLS stacks.
        private const SslProtocols Tls13Protocol = (SslProtocols)0x3000;
#pragma warning restore SYSLIB0039

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
                _optionsCtor = optionsType.GetConstructor(Type.EmptyTypes);
                if (_optionsCtor != null)
                {
                    try
                    {
                        var newExpr = Expression.New(_optionsCtor);
                        _createOptionsInstance = Expression.Lambda<Func<object>>(newExpr).Compile();
                    }
                    catch
                    {
                        // AOT/JIT restrictions may block dynamic delegate compilation.
                        // Fallback to ConstructorInfo.Invoke in AuthenticateWithAlpnAsync.
                    }
                }

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

                    try
                    {
                        var protocolListType = typeof(System.Collections.Generic.List<>)
                            .MakeGenericType(_alpnProtocolType);
                        _alpnProtocolListCtor = protocolListType.GetConstructor(Type.EmptyTypes);
                        _alpnProtocolListAddMethod = protocolListType.GetMethod("Add", new[] { _alpnProtocolType });
                    }
                    catch
                    {
                        // IL2CPP/AOT or aggressive stripping may reject this generic instantiation.
                        // Leave list metadata null so caller can fall back to non-ALPN auth.
                    }
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
                        enabledSslProtocols: SslProtocols.Tls12 | Tls13Protocol,
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
            // Option instance creation prefers cached delegate to avoid Activator overhead.
            if (_optionsType == null || _targetHostProp == null || _authWithOptionsMethod == null)
                throw new PlatformNotSupportedException("SslClientAuthenticationOptions not available");

            var options = _createOptionsInstance != null
                ? _createOptionsInstance()
                : _optionsCtor != null
                    ? _optionsCtor.Invoke(null)
                    : throw new PlatformNotSupportedException(
                        "SslClientAuthenticationOptions default constructor not available.");

            _targetHostProp.SetValue(options, host);

            if (_enabledProtocolsProp != null)
            {
#pragma warning disable SYSLIB0039 // SslProtocols is obsolete in .NET 7+
                _enabledProtocolsProp.SetValue(
                    options, SslProtocols.Tls12 | Tls13Protocol);
#pragma warning restore SYSLIB0039
            }

            // Set ApplicationProtocols (List<SslApplicationProtocol>)
            // NOTE: Cached reflection metadata can still be unavailable under IL2CPP/AOT
            // stripping. In that case this path is skipped and caller fallback applies.
            if (_alpnProtocolType != null && _alpnProtocolListCtor != null && _alpnProtocolListAddMethod != null)
            {
                var alpnList = _alpnProtocolListCtor.Invoke(null);

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
                        _alpnProtocolListAddMethod.Invoke(alpnList, new[] { value });
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

                var result = alpnResult.ToString();
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
            if (protocol == Tls13Protocol)
                return "1.3";
            return protocol.ToString();
        }
#pragma warning restore SYSLIB0039
    }
}
