

using System;
using System.IO;
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

        /// <summary>
        /// Limits concurrent TLS handshakes to avoid ThreadPool starvation on mobile,
        /// where handshakes are blocking and run on thread pool threads.
        /// </summary>
        private static readonly SemaphoreSlim HandshakeSemaphore = new SemaphoreSlim(4);

        public string ProviderName => "BouncyCastle";

        private BouncyCastleTlsProvider()
        {
            // Singleton pattern
        }

        public bool IsAlpnSupported() => true;  // Always supported

        /// <summary>
        /// Wrap a raw TCP stream with TLS using BouncyCastle.
        /// </summary>
        /// <remarks>
        /// <para>
        /// CANCELLATION LIMITATION: The BouncyCastle TLS handshake is a blocking operation
        /// that cannot be interrupted once started. The cancellation token is checked:
        /// </para>
        /// <list type="bullet">
        /// <item>Before starting the handshake (via Task.Run's ct parameter)</item>
        /// <item>At the beginning of PerformHandshake</item>
        /// <item>After SecureRandom initialization</item>
        /// </list>
        /// <para>
        /// Once protocol.Connect() begins, it will run to completion or failure regardless
        /// of cancellation requests. For timeout enforcement, callers should use a combined
        /// timeout/cancellation strategy at a higher level.
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

            // BouncyCastle handshake is blocking; run on thread pool with concurrency limit
            await HandshakeSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // BouncyCastle handshake is blocking. Dispose the stream on cancellation to
                // break out of blocking IO inside protocol.Connect().
                using var cancelRegistration = ct.Register(static state =>
                {
                    try { ((Stream)state).Dispose(); } catch { }
                }, innerStream);

                try
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await Task.Run(
                        () => PerformHandshake(innerStream, host, alpnProtocols, ct),
                        CancellationToken.None).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return result;
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException("TLS handshake was canceled.", ct);
                }
            }
            finally
            {
                HandshakeSemaphore.Release();
            }
        }

        private TlsResult PerformHandshake(
            Stream innerStream,
            string host,
            string[] alpnProtocols,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            
            var secureRandom = new SecureRandom();
            var crypto = new BcTlsCrypto(secureRandom);
            
            ct.ThrowIfCancellationRequested();

            var protocol = new TlsClientProtocol(innerStream);

            try
            {
                var client = new TurboTlsClient(crypto, host, alpnProtocols ?? Array.Empty<string>());

                protocol.Connect(client);

                return new TlsResult(
                    secureStream: protocol.Stream,
                    negotiatedAlpn: client.NegotiatedAlpn,
                    tlsVersion: client.NegotiatedVersion ?? "Unknown",
                    cipherSuite: client.NegotiatedCipherSuite,
                    providerName: ProviderName);
            }
            catch (Exception ex)
            {
                // Close protocol to release resources — protocol.Stream may not
                // cascade disposal back to TlsClientProtocol.
                try { protocol.Close(); } catch { }
                
                if (ex is TlsFatalAlert alert)
                {
                    throw new System.Security.Authentication.AuthenticationException(
                        $"TLS handshake failed: {alert.AlertDescription}", ex);
                }
                else if (ex is IOException)
                {
                    throw;
                }
                else
                {
                    throw new IOException("TLS handshake failed", ex);
                }
            }
        }
    }
}
