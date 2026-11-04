using System;
using System.IO;
using System.Linq.Expressions;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// TLS provider using System.Net.Security.SslStream.
    /// Uses reflection to access ALPN APIs on .NET Standard 2.1.
    /// On IL2CPP with aggressive stripping, preserve SslStream ALPN types (e.g., via
    /// link.xml or [Preserve]) to keep HTTP/2 ALPN negotiation available. Without that
    /// preservation, TLS still works but ALPN may be unavailable (HTTP/1.1 fallback).
    /// ALPN is a negotiation (RFC 7301): when the caller also offers "http/1.1", a runtime
    /// without ALPN performs a plain handshake and reports no negotiated protocol (HTTP/1.1).
    /// Only a request that cannot fall back to HTTP/1.1 (e.g. "h2" alone) fails without ALPN.
    /// The protocol version is OS-negotiated (<see cref="HandshakeProtocols"/>) and anything
    /// below TLS 1.2 is rejected after the handshake.
    /// </summary>
    internal sealed class SslStreamTlsProvider : ITlsProvider
    {
        private const string Http11Protocol = "http/1.1";

        /// <summary>
        /// Protocol versions passed to the handshake: <see cref="SslProtocols.None"/> lets the OS
        /// pick its best supported version (TLS 1.3 where available) without enabling flag bits
        /// an older runtime may not recognize. The TLS 1.2 floor is enforced after the handshake
        /// by <see cref="EnsureMinimumTlsVersion"/>.
        /// </summary>
        internal const SslProtocols HandshakeProtocols = SslProtocols.None;

        private static readonly string[] EmptyAlpnProtocols = Array.Empty<string>();
        public static readonly SslStreamTlsProvider Instance = new();

        // Test seam: a provider that behaves as if the runtime had no ALPN APIs.
        private readonly bool _alpnDisabled;

        private SslStreamTlsProvider() { }

        /// <summary>
        /// Test-only constructor. <paramref name="simulateNoAlpn"/> makes this instance behave as
        /// if the runtime exposed no SslStream ALPN APIs.
        /// </summary>
        internal SslStreamTlsProvider(bool simulateNoAlpn)
        {
            _alpnDisabled = simulateNoAlpn;
        }

        // Test seam (#171, #172): replaces the authentication call (ALPN or plain) so a test can
        // drive a handshake that throws or never completes. Non-null also counts as ALPN support.
        private readonly Func<SslStream, CancellationToken, Task> _handshakeOverride;

        /// <summary>
        /// Test-only constructor. <paramref name="handshakeOverride"/> runs instead of the single
        /// <c>AuthenticateAsClientAsync</c> call: on the ALPN path unless
        /// <paramref name="simulateNoAlpn"/> selects the plain path.
        /// </summary>
        internal SslStreamTlsProvider(bool simulateNoAlpn, Func<SslStream, CancellationToken, Task> handshakeOverride)
        {
            _alpnDisabled = simulateNoAlpn;
            _handshakeOverride = handshakeOverride ?? throw new ArgumentNullException(nameof(handshakeOverride));
        }

        // Cached static delegate — eliminates per-WrapAsync delegate allocation since
        // ValidateServerCertificate has no instance state.
        private static readonly RemoteCertificateValidationCallback s_validateCertCallback =
            new RemoteCertificateValidationCallback(ValidateServerCertificate);

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
                    catch (PlatformNotSupportedException)
                    {
                        // IL2CPP does not support dynamic method compilation.
                        // Fall back to ConstructorInfo.Invoke in CreateAlpnOptions.
                    }
                    catch (NotSupportedException)
                    {
                        // Mono interpreter or AOT variant that disallows Compile(). Fall back.
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

        public bool IsAlpnSupported() => (_alpnSupported || _handshakeOverride != null) && !_alpnDisabled;

        public async Task<TlsResult> WrapAsync(
            Stream innerStream,
            string host,
            string[] alpnProtocols,
            CancellationToken ct)
        {
            if (innerStream == null)
                throw new ArgumentNullException(nameof(innerStream));
            if (string.IsNullOrEmpty(host))
                throw new ArgumentNullException(nameof(host));

            ct.ThrowIfCancellationRequested();

            // RFC 6066 section 3 (#142): A-label for IDN names, bare address for IP literals (the
            // platform omits SNI for an address). Also the name the certificate is matched against:
            // certificates carry A-labels (RFC 5280 section 7.2) and unbracketed addresses.
            host = TlsHostName.ToTargetHost(host);

            alpnProtocols ??= EmptyAlpnProtocols;
            bool alpnSupported = IsAlpnSupported();
            bool http11Offered = OffersProtocol(alpnProtocols, Http11Protocol);
            bool useAlpn = ShouldNegotiateAlpn(alpnProtocols, alpnSupported);

            // The ALPN decision is final before the SslStream exists (#171). The options are built
            // by reflection only, so a failure here (AOT/stripping edge case) happens before any
            // byte reaches the wire: with http/1.1 offered the handshake runs without ALPN
            // (RFC 7301 section 3.1), otherwise the requested protocol cannot be negotiated.
            object alpnOptions = null;
            if (useAlpn && _handshakeOverride == null)
            {
                try
                {
                    alpnOptions = CreateAlpnOptions(host, alpnProtocols);
                }
                catch (Exception ex) when (IsAlpnSetupFailure(ex))
                {
                    if (!http11Offered)
                    {
                        throw new PlatformNotSupportedException(
                            "SslStream ALPN negotiation failed on this runtime and the caller " +
                            "did not offer http/1.1, so the requested protocol cannot be negotiated.", ex);
                    }

                    useAlpn = false;
                }
            }

            var sslStream = new SslStream(
                innerStream,
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: s_validateCertCallback);

            try
            {
                // Exactly one authentication call per SslStream (#171): whatever the handshake
                // throws (AuthenticationException, IOException, InvalidOperationException for a
                // stream-state error) propagates, and the catch below disposes the stream. A
                // second ClientHello on a used stream is not a TLS retry (RFC 8446, RFC 5246).
                //
                // The plain overload takes no CancellationToken, and a runtime may ignore the token
                // of the ALPN overload. AbandonableTask returns as soon as ct fires and observes the
                // abandoned handshake, which ends when the catch below disposes the stream (#172).
                Task authTask = StartAuthentication(sslStream, host, useAlpn, alpnOptions, ct);
                await AbandonableTask.WaitAsync(authTask, ct).ConfigureAwait(false);

                // TLS 1.2 floor: the handshake let the OS choose the version, so verify it here.
                // Throws inside the try so the catch below disposes the SslStream.
                var negotiatedProtocol = sslStream.SslProtocol;
                EnsureMinimumTlsVersion(negotiatedProtocol);

                // RFC 6125 1.7.2: an IP host needs an iPAddress SAN. The platform check passed, but
                // some platforms also accept a dNSName spelling the address (#113).
                SubjectAltNameIpAddresses.EnsureIpLiteralIdentity(host, sslStream.RemoteCertificate);

                // Extract ALPN result
                string negotiatedAlpn = null;
                if (useAlpn)
                {
                    negotiatedAlpn = ExtractAlpnResult(sslStream);
                }

                return new TlsResult(
                    secureStream: sslStream,
                    negotiatedAlpn: negotiatedAlpn,
                    tlsVersion: FormatTlsVersion(negotiatedProtocol),
                    cipherSuite: null, // SslStream doesn't expose this easily
                    providerName: ProviderName);
            }
            catch
            {
                sslStream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Exception filter for building the ALPN options by reflection (#171). These types can
        /// only come from metadata or member access that AOT/stripping removed, before the
        /// handshake starts. <see cref="InvalidOperationException"/> is deliberately absent: from
        /// <c>AuthenticateAsClientAsync</c> it is an SslStream state error (already authenticated,
        /// handshake in progress), never a missing ALPN capability, and it propagates.
        /// </summary>
        internal static bool IsAlpnSetupFailure(Exception ex)
        {
            return ex is TargetInvocationException
                || ex is MemberAccessException
                || ex is TypeLoadException
                || ex is MissingMethodException
                || ex is PlatformNotSupportedException;
        }

        private Task StartAuthentication(
            SslStream sslStream,
            string host,
            bool useAlpn,
            object alpnOptions,
            CancellationToken ct)
        {
            if (_handshakeOverride != null)
                return _handshakeOverride(sslStream, ct);

            if (useAlpn)
                return StartAlpnAuthentication(sslStream, alpnOptions, ct);

            // checkCertificateRevocation: false — intentional design choice.
            // OCSP/CRL revocation checks are unreliable on mobile networks (servers
            // frequently unreachable) and add 50–200 ms per fresh handshake. The
            // BCL default for SslClientAuthenticationOptions is also false.
            return sslStream.AuthenticateAsClientAsync(
                host,
                clientCertificates: null,
                enabledSslProtocols: HandshakeProtocols,
                checkCertificateRevocation: false);
        }

        private static object CreateAlpnOptions(string host, string[] alpnProtocols)
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
                _enabledProtocolsProp.SetValue(options, HandshakeProtocols);
            }

            // Set ApplicationProtocols (List<SslApplicationProtocol>)
            // NOTE: Cached reflection metadata can still be unavailable under IL2CPP/AOT
            // stripping. In that case this path is skipped and the handshake carries no ALPN.
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

            return options;
        }

        private static Task StartAlpnAuthentication(SslStream sslStream, object options, CancellationToken ct)
        {
            try
            {
                return (Task)_authWithOptionsMethod.Invoke(sslStream, new[] { options, ct });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                // MethodInfo.Invoke wraps all target exceptions in TargetInvocationException.
                // Unwrap so the caller sees the real exception (AuthenticationException,
                // IOException, etc.) instead of a misleading reflection wrapper.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw; // unreachable, satisfies compiler
            }
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

        /// <summary>
        /// Decides whether the handshake should carry the ALPN extension. Returns false for a
        /// plain handshake. A runtime without ALPN still serves any request that offers
        /// "http/1.1" (no ALPN result means HTTP/1.1); a request that cannot fall back to
        /// HTTP/1.1 throws <see cref="PlatformNotSupportedException"/>.
        /// </summary>
        internal static bool ShouldNegotiateAlpn(string[] alpnProtocols, bool alpnSupported)
        {
            if (alpnProtocols == null || alpnProtocols.Length == 0)
                return false;

            if (alpnSupported)
                return true;

            if (OffersProtocol(alpnProtocols, Http11Protocol))
                return false;

            throw new PlatformNotSupportedException(
                "SslStream ALPN APIs are unavailable on this runtime and the caller did not offer " +
                "http/1.1, so the requested protocol (e.g. h2) cannot be negotiated.");
        }

        /// <summary>
        /// Rejects a negotiated protocol version below TLS 1.2 (RFC 8996 deprecates TLS 1.0/1.1).
        /// </summary>
        internal static void EnsureMinimumTlsVersion(SslProtocols negotiated)
        {
            if ((int)negotiated < (int)SslProtocols.Tls12)
            {
                throw new AuthenticationException(
                    "TLS handshake negotiated " + negotiated +
                    "; TurboHTTP requires TLS 1.2 or newer.");
            }
        }

        private static bool OffersProtocol(string[] protocols, string target)
        {
            if (protocols == null || protocols.Length == 0 || string.IsNullOrEmpty(target))
                return false;

            for (int i = 0; i < protocols.Length; i++)
            {
                if (string.Equals(protocols[i], target, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        // Static: no instance state. Cached via s_validateCertCallback to avoid
        // per-connection delegate allocation.
        // Strict by design: only accepts chains with zero policy errors.
        // Certificate validation failures, hostname mismatches, and untrusted roots
        // all set sslPolicyErrors != None and propagate as AuthenticationException
        // to the caller — no silent downgrade behavior.
        private static bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            return sslPolicyErrors == SslPolicyErrors.None;
        }

        internal static string FormatTlsVersion(SslProtocols protocol)
        {
            if (protocol == SslProtocols.Tls12)
                return "1.2";
            // TLS 1.3 (RFC 8446) is the only version above 1.2. Compare numerically because
            // the SslProtocols.Tls13 member is missing from older target profiles.
            if ((int)protocol > (int)SslProtocols.Tls12)
                return "1.3";
            return protocol.ToString();
        }
    }
}
