// Step 3C.7: BouncyCastleTlsProvider
//
// This file implements ITlsProvider using BouncyCastle's pure C# TLS implementation.
// REQUIRES: BouncyCastle source to be repackaged in the Lib/ directory first.
//
// To enable this implementation:
// 1. Download BouncyCastle source from https://github.com/bcgit/bc-csharp (v2.2.1+)
// 2. Extract to Assets/TurboHTTP/ThirdParty/BouncyCastle-Source
// 3. Run Tools > TurboHTTP > Repackage BouncyCastle in Unity Editor
// 4. Rename this file to BouncyCastleTlsProvider.cs and remove the .stub extension
// 5. Rename TurboTlsClient.cs.stub and TurboTlsAuthentication.cs.stub as well


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

            // BouncyCastle handshake is blocking; run on thread pool
            return await Task.Run(() => PerformHandshake(innerStream, host, alpnProtocols, ct), ct)
                .ConfigureAwait(false);
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

