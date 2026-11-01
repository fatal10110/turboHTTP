

using System;
using System.IO;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Security;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto.Impl.BC;
using TurboHTTP.Transport.Tls;
using UnityEngine.Scripting;

namespace TurboHTTP.Transport.BouncyCastle
{
    /// <summary>
    /// TLS provider using BouncyCastle pure C# implementation.
    /// Guaranteed to work on all platforms, including IL2CPP/AOT builds.
    /// </summary>
    [Preserve]  // Prevent IL2CPP from stripping (loaded via reflection)
    internal sealed class BouncyCastleTlsProvider : ITlsProvider
    {
        public static readonly BouncyCastleTlsProvider Instance = new();

        private readonly int _handshakeTimeoutMillis;

        public string ProviderName => "BouncyCastle";

        private BouncyCastleTlsProvider()
            : this(TurboTlsClient.DefaultHandshakeTimeoutMillis)
        {
        }

        /// <summary>
        /// A provider with a non-default handshake bound. Production code uses <see cref="Instance"/>;
        /// tests reach this constructor by reflection (the test assembly does not reference this one).
        /// </summary>
        [Preserve]
        internal BouncyCastleTlsProvider(int handshakeTimeoutMillis)
        {
            if (handshakeTimeoutMillis <= 0)
                throw new ArgumentOutOfRangeException(nameof(handshakeTimeoutMillis));
            _handshakeTimeoutMillis = handshakeTimeoutMillis;
        }

        public bool IsAlpnSupported() => true;  // Always supported

        /// <summary>
        /// Wrap a raw TCP stream with TLS using BouncyCastle.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The handshake and all later I/O use the inner stream's async API through
        /// <see cref="BouncyCastleTlsStream"/> (BouncyCastle in non-blocking mode), so no thread
        /// blocks on socket I/O whatever the inner stream is (NetworkStream, SAEA or poll/select).
        /// The returned stream is full duplex: a pending read never delays a write.
        /// </para>
        /// <para>
        /// Cancellation disposes <paramref name="innerStream"/>, which fails any pending I/O, and
        /// surfaces as <see cref="OperationCanceledException"/>. On any failure the inner stream
        /// is disposed.
        /// </para>
        /// <para>
        /// The whole handshake is bounded by <see cref="TurboTlsClient.GetHandshakeTimeoutMillis"/>
        /// (30 s by default, #142): at the deadline the inner stream is disposed the same way, so a
        /// peer that never answers or trickles records cannot hold the connection, and the failure
        /// surfaces as a retryable <see cref="IOException"/> whose inner exception is a
        /// <see cref="TimeoutException"/>. BouncyCastle checks its own handshake timeout only in
        /// blocking mode, which this provider does not use. The deadline is independent of the
        /// connect timeout (which ends when the socket is connected) and of the request timeout
        /// (<paramref name="ct"/>), which still applies: the effective bound is the smaller one.
        /// Certificate evaluation runs inside the handshake; a deadline that expires during it
        /// takes effect when it returns (its own bound belongs to the evaluation, #33).
        /// </para>
        /// <para>
        /// A fatal TLS alert (sent or received) that reports a certificate, negotiation or protocol
        /// verdict throws <see cref="AuthenticationException"/> (not retryable); transient alerts
        /// such as internal_error and transport failures throw <see cref="IOException"/>.
        /// </para>
        /// </remarks>
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

            // Disposing the inner stream fails a pending read/write inside the handshake, both on
            // cancellation and at the handshake deadline.
            using var deadline = new CancellationTokenSource();
            var cancelRegistration = ct.Register(DisposeStream, innerStream);
            var deadlineRegistration = deadline.Token.Register(DisposeStream, innerStream);

            TlsResult result;
            try
            {
                deadline.CancelAfter(_handshakeTimeoutMillis);

                // Start on a pool thread: Connect() generates key shares (CPU work) before the
                // first await, and the caller may be a thread that must not run it.
                result = await Task.Run(
                    () => HandshakeAsync(innerStream, host, alpnProtocols, _handshakeTimeoutMillis),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("TLS handshake was canceled.", ct);
            }
            catch (Exception ex) when (deadline.IsCancellationRequested)
            {
                throw CreateHandshakeTimeout(ex);
            }
            finally
            {
                // Dispose waits for a callback that is already running, so after this no callback
                // can dispose the inner stream behind the checks below.
                cancelRegistration.Dispose();
                deadlineRegistration.Dispose();
            }

            if (ct.IsCancellationRequested)
            {
                // Canceled after the handshake finished: the registration may already have
                // disposed the inner stream, so the result is unusable.
                result.SecureStream.Dispose();
                throw new OperationCanceledException("TLS handshake was canceled.", ct);
            }

            if (deadline.IsCancellationRequested)
            {
                // Same race with the deadline.
                result.SecureStream.Dispose();
                throw CreateHandshakeTimeout(null);
            }

            return result;
        }

        private static void DisposeStream(object state)
        {
            try { ((Stream)state).Dispose(); } catch { }
        }

        private Exception CreateHandshakeTimeout(Exception inner)
        {
            string message = $"TLS handshake timed out after {_handshakeTimeoutMillis} ms.";
            return new IOException(message, new TimeoutException(message, inner));
        }

        private async Task<TlsResult> HandshakeAsync(
            Stream innerStream,
            string host,
            string[] alpnProtocols,
            int handshakeTimeoutMillis)
        {
            var crypto = new BcTlsCrypto(new SecureRandom());
            var client = new TurboTlsClient(
                crypto, host, alpnProtocols ?? Array.Empty<string>(), handshakeTimeoutMillis);
            var secureStream = new BouncyCastleTlsStream(innerStream);

            try
            {
                // Cancellation works through the registration in WrapAsync (inner dispose); the
                // token is not passed down so a cancel never leaves a half-written record.
                await secureStream.HandshakeAsync(client, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The stream already disposed the inner stream.
                var mapped = MapHandshakeFailure(ex);
                if (mapped == null)
                    throw;
                throw mapped;
            }

            return new TlsResult(
                secureStream: secureStream,
                negotiatedAlpn: client.NegotiatedAlpn,
                tlsVersion: client.NegotiatedVersion ?? "Unknown",
                cipherSuite: client.NegotiatedCipherSuite,
                providerName: ProviderName);
        }

        /// <summary>
        /// Classifies a handshake failure. Returns null when <paramref name="ex"/> is already the
        /// right exception (a transport <see cref="IOException"/>).
        /// </summary>
        internal static Exception MapHandshakeFailure(Exception ex)
        {
            switch (ex)
            {
                case TlsFatalAlertReceived received:
                    return MapFatalAlert(received.AlertDescription, "received from", ex);
                case TlsFatalAlert sent:
                    return MapFatalAlert(sent.AlertDescription, "sent to", ex);
                case IOException _:
                    return null;
                default:
                    return new IOException("TLS handshake failed", ex);
            }
        }

        private static Exception MapFatalAlert(short alertDescription, string direction, Exception ex)
        {
            string message =
                $"TLS handshake failed: fatal alert {AlertDescription.GetText(alertDescription)} {direction} the server";
            return IsNonTransientAlert(alertDescription)
                ? new AuthenticationException(message, ex)
                : new IOException(message, ex);
        }

        /// <summary>
        /// True for alerts that report a certificate, negotiation or protocol verdict
        /// (RFC 8446 section 6.2, RFC 5246 section 7.2.2): repeating the same handshake gets the
        /// same answer. Record-layer corruption, internal_error, user_canceled and unknown codes
        /// stay transient network errors.
        /// </summary>
        internal static bool IsNonTransientAlert(short alertDescription)
        {
            switch (alertDescription)
            {
                case AlertDescription.unexpected_message:
                case AlertDescription.handshake_failure:
                case AlertDescription.no_certificate:
                case AlertDescription.bad_certificate:
                case AlertDescription.unsupported_certificate:
                case AlertDescription.certificate_revoked:
                case AlertDescription.certificate_expired:
                case AlertDescription.certificate_unknown:
                case AlertDescription.illegal_parameter:
                case AlertDescription.unknown_ca:
                case AlertDescription.access_denied:
                case AlertDescription.decode_error:
                case AlertDescription.decrypt_error:
                case AlertDescription.export_restriction:
                case AlertDescription.protocol_version:
                case AlertDescription.insufficient_security:
                case AlertDescription.inappropriate_fallback:
                case AlertDescription.missing_extension:
                case AlertDescription.unsupported_extension:
                case AlertDescription.certificate_unobtainable:
                case AlertDescription.unrecognized_name:
                case AlertDescription.bad_certificate_status_response:
                case AlertDescription.bad_certificate_hash_value:
                case AlertDescription.unknown_psk_identity:
                case AlertDescription.certificate_required:
                case AlertDescription.no_application_protocol:
                    return true;
                default:
                    return false;
            }
        }
    }
}
