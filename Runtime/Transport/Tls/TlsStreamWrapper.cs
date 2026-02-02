using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Return type for TLS handshake containing the wrapped stream and negotiated protocol version.
    /// </summary>
    internal readonly struct TlsResult
    {
        public Stream Stream { get; }
        public SslProtocols NegotiatedProtocol { get; }

        public TlsResult(Stream stream, SslProtocols negotiatedProtocol)
        {
            Stream = stream;
            NegotiatedProtocol = negotiatedProtocol;
        }
    }

    /// <summary>
    /// Static utility for TLS handshake with runtime probe for SslClientAuthenticationOptions overload.
    /// Falls back to 4-arg overload on runtimes that lack the .NET 5+ API.
    /// </summary>
    internal static class TlsStreamWrapper
    {
        // Single cached MethodInfo probe for SslClientAuthenticationOptions overload.
        // One-time reflection cost at startup. Null if overload is unavailable.
        private static readonly MethodInfo _sslOptionsMethod = typeof(SslStream).GetMethod(
            "AuthenticateAsClientAsync",
            new[] { typeof(SslClientAuthenticationOptions), typeof(CancellationToken) });

        private static bool HasSslOptionsOverload => _sslOptionsMethod != null;

        // Reflection probes for ALPN support (.NET 5+ only)
        private static readonly PropertyInfo _applicationProtocolsProp =
            typeof(SslClientAuthenticationOptions).GetProperty("ApplicationProtocols");

        private static readonly Type _sslAppProtocolType =
            Type.GetType("System.Net.Security.SslApplicationProtocol, System.Net.Security");

        // Cached PropertyInfo for NegotiatedApplicationProtocol (.NET 5+ only)
        private static readonly PropertyInfo _negotiatedAppProtocolProp =
            typeof(SslStream).GetProperty("NegotiatedApplicationProtocol");

        /// <summary>
        /// Perform TLS handshake on the given stream, returning the wrapped SslStream and negotiated protocol.
        /// Uses SslClientAuthenticationOptions overload if available (supports CancellationToken and ALPN),
        /// otherwise falls back to 4-arg overload with Task.WhenAny cancellation.
        /// </summary>
        public static async Task<TlsResult> WrapAsync(
            Stream innerStream, string host, CancellationToken ct, string[] alpnProtocols = null)
        {
            var sslStream = new SslStream(innerStream, leaveInnerStreamOpen: false, ValidateServerCertificate);

            try
            {
                if (HasSslOptionsOverload)
                {
                    await AuthenticateWithOptionsAsync(sslStream, host, ct, alpnProtocols).ConfigureAwait(false);
                }
                else
                {
                    await AuthenticateWithFallbackAsync(sslStream, host, ct).ConfigureAwait(false);
                }

                // Post-handshake: TLS version enforcement
#pragma warning disable SYSLIB0039 // SslProtocol property is obsolete in .NET 7+ but needed here
                var negotiatedProtocol = sslStream.SslProtocol;
#pragma warning restore SYSLIB0039

                if (negotiatedProtocol < SslProtocols.Tls12)
                {
                    throw new AuthenticationException(
                        $"Server negotiated {negotiatedProtocol}, but minimum TLS 1.2 is required");
                }

                return new TlsResult(sslStream, negotiatedProtocol);
            }
            catch
            {
                sslStream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Primary path using SslClientAuthenticationOptions (supports CancellationToken + ALPN).
        /// Invoked via reflection since this overload is .NET 5+ and may not exist at compile time.
        /// </summary>
        private static async Task AuthenticateWithOptionsAsync(
            SslStream sslStream, string host, CancellationToken ct, string[] alpnProtocols)
        {
            var sslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.None, // OS negotiates best available
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            };

            if (alpnProtocols != null && alpnProtocols.Length > 0)
            {
                SetAlpnViaReflection(sslOptions, alpnProtocols);
            }

            // Invoke via reflection — the overload may not exist at compile time (.NET Std 2.1).
            // MethodInfo.Invoke wraps synchronous argument errors in TargetInvocationException;
            // unwrap to preserve the original exception type for correct error mapping upstream.
            Task task;
            try
            {
                task = (Task)_sslOptionsMethod.Invoke(sslStream, new object[] { sslOptions, ct });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw; // Unreachable, but satisfies compiler
            }
            await task.ConfigureAwait(false);
        }

        /// <summary>
        /// Fallback path using 4-arg overload (always available in .NET Standard 2.1).
        /// No CancellationToken support, no ALPN. Uses Task.WhenAny for cancellation.
        /// </summary>
        private static async Task AuthenticateWithFallbackAsync(
            SslStream sslStream, string host, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var handshakeTask = sslStream.AuthenticateAsClientAsync(
                host,
                null,                  // clientCertificates
                SslProtocols.None,     // OS negotiates
                checkCertificateRevocation: false
            );

            var completedTask = await Task.WhenAny(handshakeTask, Task.Delay(-1, ct)).ConfigureAwait(false);

            if (completedTask != handshakeTask)
            {
                // Cancellation fired before handshake completed.
                // Do NOT dispose sslStream here — the caller's catch block in WrapAsync handles it.
                ct.ThrowIfCancellationRequested();
            }

            await handshakeTask.ConfigureAwait(false);
        }

        /// <summary>
        /// Set ALPN protocols via reflection. SslApplicationProtocol and ApplicationProtocols
        /// are .NET 5+ additions — direct use would cause compile errors on .NET Standard 2.1.
        /// Silently returns if ALPN is not available on the current platform.
        /// </summary>
        private static void SetAlpnViaReflection(SslClientAuthenticationOptions options, string[] protocols)
        {
            if (_applicationProtocolsProp == null || _sslAppProtocolType == null)
                return;

            // Create List<SslApplicationProtocol> via reflection
            var listType = typeof(List<>).MakeGenericType(_sslAppProtocolType);
            var list = Activator.CreateInstance(listType);
            var addMethod = listType.GetMethod("Add");
            if (addMethod == null) return; // BCL broken, ALPN unavailable

            foreach (var proto in protocols)
            {
                string fieldName;
                if (proto == "h2")
                    fieldName = "Http2";
                else if (proto == "http/1.1")
                    fieldName = "Http11";
                else
                    continue;

                var field = _sslAppProtocolType.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
                if (field != null)
                    addMethod.Invoke(list, new[] { field.GetValue(null) });
            }

            _applicationProtocolsProp.SetValue(options, list);
        }

        /// <summary>
        /// Get the ALPN-negotiated application protocol ("h2", "http/1.1", or null).
        /// Must use reflection — NegotiatedApplicationProtocol is .NET 5+ only.
        /// Prepared for Phase 3B (HTTP/2).
        /// </summary>
        public static string GetNegotiatedProtocol(SslStream sslStream)
        {
            if (sslStream == null || _negotiatedAppProtocolProp == null) return null;

            try
            {
                var value = _negotiatedAppProtocolProp.GetValue(sslStream);
                if (value == null) return null;

                var toStringMethod = value.GetType().GetMethod("ToString", Type.EmptyTypes);
                var result = toStringMethod?.Invoke(value, null) as string;

                return string.IsNullOrEmpty(result) ? null : result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Certificate validation callback. Returns true only if no policy errors.
        /// Static method (no instance captures) to avoid delegate marshaling issues under IL2CPP.
        /// </summary>
        private static bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors != SslPolicyErrors.None)
            {
                // Log diagnostics for TLS failures — critical for debugging on-device issues.
                // Debug.WriteLine is stripped from release builds (conditional on DEBUG).
                // Phase 8 (Observability) will replace this with structured logging.
                Debug.WriteLine($"[TurboHTTP.TLS] Certificate validation failed: {sslPolicyErrors}");
                if (certificate != null)
                    Debug.WriteLine($"[TurboHTTP.TLS]   Subject: {certificate.Subject}, Issuer: {certificate.Issuer}");
                return false;
            }
            return true;
        }
    }
}
