using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// TLS provider using System.Net.Security.SslStream.
    /// Calls the .NET Standard 2.1 ALPN API (<see cref="SslClientAuthenticationOptions"/>,
    /// <see cref="SslApplicationProtocol"/>) directly, without reflection (#303): the static call
    /// sites make IL2CPP generate <c>List&lt;SslApplicationProtocol&gt;</c> and keep the members
    /// (link.xml preserves them as well). If building the options still fails on a runtime
    /// (missing AOT code or metadata), TLS works without ALPN (HTTP/1.1 fallback).
    /// Unity's runtimes (Editor and players) build the options but ignore them and cannot report
    /// a negotiated protocol (mono#12880), so they count as having no ALPN: HTTP/2 there needs the
    /// BouncyCastle provider.
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

        // Set once by the type initializer: true when this runtime can build the ALPN options
        // (the same builder every connection uses; false when it throws, #303) and can report the
        // negotiated protocol (false where reading it throws PlatformNotSupportedException).
        private static readonly bool _alpnSupported;

        // Cached so WrapAsync allocates no delegate per connection.
        private static readonly Func<string, string[], object> s_buildAlpnOptions = BuildAlpnOptionsObject;

        static SslStreamTlsProvider()
        {
            _alpnSupported = ProbeAlpnOptions() && ProbeNegotiatedProtocol();
        }

        // A type initializer must never throw: any failure only means "no ALPN" (HTTP/1.1 fallback,
        // or PlatformNotSupportedException for an offer without http/1.1), never weaker TLS.
        private static bool ProbeAlpnOptions()
        {
            try
            {
                return BuildAlpnOptionsObject("localhost", AlpnProtocols.H2AndHttp11) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // A runtime that accepts the ALPN options but ignores them throws
        // PlatformNotSupportedException when the result is read (Unity's Mono class library,
        // mono#12880): the extension never reaches the wire, so it can never select h2. The read
        // happens on a stream that never handshakes, so the probe touches no socket. The inner
        // stream is a MemoryStream: .NET rejects Stream.Null as an SslStream inner stream
        // (ArgumentNullException), which would read as "no ALPN" on every runtime.
        private static bool ProbeNegotiatedProtocol()
        {
            try
            {
                using var probe = new SslStream(new MemoryStream());
                return ReportsNegotiatedProtocol(() => ReadNegotiatedProtocol(probe));
            }
            catch (Exception)
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadNegotiatedProtocol(SslStream sslStream)
        {
            _ = sslStream.NegotiatedApplicationProtocol;
        }

        /// <summary>
        /// Whether a runtime reports the ALPN result, judged by one read of
        /// <c>NegotiatedApplicationProtocol</c> before any handshake: a value, or
        /// <see cref="InvalidOperationException"/> (not authenticated yet), means it does;
        /// <see cref="PlatformNotSupportedException"/> means it has no ALPN. Any other failure
        /// also counts as no ALPN (HTTP/1.1), never as weaker TLS.
        /// </summary>
        internal static bool ReportsNegotiatedProtocol(Action readNegotiatedProtocol)
        {
            try
            {
                readNegotiatedProtocol();
                return true;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (Exception)
            {
                return false;
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

            // The ALPN decision is final before the SslStream exists (#171). Building the options
            // touches no socket, so a failure here (missing AOT code or metadata, #303) happens
            // before any byte reaches the wire: with http/1.1 offered the handshake runs without
            // ALPN (RFC 7301 section 3.1), otherwise the requested protocol cannot be negotiated.
            // Held as object so this method never names the options type itself.
            object alpnOptions = null;
            if (useAlpn && _handshakeOverride == null)
            {
                alpnOptions = ResolveAlpnOptions(s_buildAlpnOptions, host, alpnProtocols, http11Offered);
                useAlpn = alpnOptions != null;
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
        /// Exception filter for building the ALPN options (#171, #303). These types can only come
        /// from a runtime that lacks the API, its metadata (stripping) or its AOT code (IL2CPP
        /// throws <see cref="ExecutionEngineException"/> or <see cref="NotSupportedException"/>
        /// for a generic instantiation it did not generate), before the handshake starts.
        /// <see cref="NotSupportedException"/> covers <see cref="PlatformNotSupportedException"/>.
        /// <see cref="InvalidOperationException"/> is deliberately absent: from
        /// <c>AuthenticateAsClientAsync</c> it is an SslStream state error (already authenticated,
        /// handshake in progress), never a missing ALPN capability, and it propagates.
        /// </summary>
        internal static bool IsAlpnSetupFailure(Exception ex)
        {
            return ex is System.Reflection.TargetInvocationException
                || ex is MemberAccessException
                || ex is TypeLoadException
                || ex is MissingMethodException
                || ex is NotSupportedException
                || ex is NotImplementedException
                || IsMissingAotCode(ex);
        }

        // ExecutionEngineException is obsolete in the target profile (the CLR no longer raises it),
        // but Mono AOT/IL2CPP still throw it for a generic instantiation with no generated code.
#pragma warning disable CS0618
        private static bool IsMissingAotCode(Exception ex) => ex is ExecutionEngineException;
#pragma warning restore CS0618

        /// <summary>
        /// Builds the ALPN options before the SslStream exists (#171). Returns null when the
        /// runtime cannot build them and the offer includes "http/1.1": the handshake then runs
        /// without ALPN (RFC 7301 section 3.1, no result means HTTP/1.1). Without "http/1.1" the
        /// requested protocol cannot be negotiated and <see cref="PlatformNotSupportedException"/>
        /// is thrown. Any other exception propagates.
        /// </summary>
        internal static object ResolveAlpnOptions(
            Func<string, string[], object> buildOptions,
            string host,
            string[] alpnProtocols,
            bool http11Offered)
        {
            try
            {
                return buildOptions(host, alpnProtocols);
            }
            catch (Exception ex) when (IsAlpnSetupFailure(ex))
            {
                if (!http11Offered)
                {
                    throw new PlatformNotSupportedException(
                        "SslStream ALPN negotiation failed on this runtime and the caller " +
                        "did not offer http/1.1, so the requested protocol cannot be negotiated.", ex);
                }

                return null;
            }
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

        // Boundary for callers that hold the options as object (see WrapAsync).
        private static object BuildAlpnOptionsObject(string host, string[] alpnProtocols)
        {
            return BuildAlpnOptions(host, alpnProtocols);
        }

        /// <summary>
        /// Client options for an ALPN handshake: <paramref name="host"/> as target host, the
        /// OS-negotiated <see cref="HandshakeProtocols"/>, and one
        /// <see cref="SslApplicationProtocol"/> per known offered protocol ("h2", "http/1.1") in
        /// offer order (RFC 7301 section 3.1: the client lists, the server picks). Unknown
        /// protocol strings are skipped. A fresh list per call: the options are per connection
        /// and nothing mutable is shared between concurrent connects.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static SslClientAuthenticationOptions BuildAlpnOptions(string host, string[] alpnProtocols)
        {
            // Static reference on purpose (#303): this call site makes IL2CPP generate
            // List<SslApplicationProtocol>..ctor/Add, which MakeGenericType could not guarantee.
            // NoInlining keeps a runtime failure (missing AOT code or type) inside this call,
            // where ResolveAlpnOptions turns it into the HTTP/1.1 fallback.
            var protocols = new List<SslApplicationProtocol>(alpnProtocols.Length);
            for (int i = 0; i < alpnProtocols.Length; i++)
            {
                string protocol = alpnProtocols[i];
                if (string.Equals(protocol, "h2", StringComparison.Ordinal))
                    protocols.Add(SslApplicationProtocol.Http2);
                else if (string.Equals(protocol, Http11Protocol, StringComparison.Ordinal))
                    protocols.Add(SslApplicationProtocol.Http11);
            }

            // Certificate validation stays with the SslStream constructor callback
            // (ValidateServerCertificate); the options set no callback of their own.
            return new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = HandshakeProtocols,
                ApplicationProtocols = protocols
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Task StartAlpnAuthentication(SslStream sslStream, object options, CancellationToken ct)
        {
            // checkCertificateRevocation: the options keep the default X509RevocationMode.NoCheck,
            // matching the plain overload (see StartAuthentication).
            return sslStream.AuthenticateAsClientAsync((SslClientAuthenticationOptions)options, ct);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ExtractAlpnResult(SslStream sslStream)
        {
            try
            {
                // Empty when the server selected nothing (RFC 7301 section 3.1: HTTP/1.1).
                var result = sslStream.NegotiatedApplicationProtocol.ToString();
                return string.IsNullOrEmpty(result) ? null : result;
            }
            catch
            {
                // A runtime that accepts the options but does not report the result (e.g. it
                // throws PlatformNotSupportedException here) negotiated nothing usable: HTTP/1.1.
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
