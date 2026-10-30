using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// Loopback TLS server with a self-signed certificate that serves exactly one connection.
    /// Clients using strict validation reject its certificate, which gives a deterministic
    /// certificate failure without public network. A second connection stays in the accept
    /// backlog, so <see cref="HasPendingConnection"/> detects a client-side reconnect/retry.
    /// </summary>
    internal sealed class LocalTlsTestServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private TcpClient _client;

        private LocalTlsTestServer(X509Certificate2 certificate)
        {
            _certificate = certificate;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = Task.Run(ServeOneAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>True when a further connection is waiting in the accept backlog.</summary>
        public bool HasPendingConnection => _listener.Pending();

        /// <summary>Returns null when the runtime cannot create a self-signed certificate.</summary>
        public static LocalTlsTestServer TryStart()
        {
            var certificate = TryCreateLocalhostCertificate();
            return certificate == null ? null : new LocalTlsTestServer(certificate);
        }

        private async Task ServeOneAsync()
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                Volatile.Write(ref _client, client);
                using var sslStream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsServerAsync(
                    _certificate,
                    clientCertificateRequired: false,
                    enabledSslProtocols: SslProtocols.Tls12,
                    checkCertificateRevocation: false).ConfigureAwait(false);
            }
            catch
            {
                // Expected: the client rejects the self-signed certificate, or the test ends.
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            try { Volatile.Read(ref _client)?.Close(); } catch { }
            _certificate.Dispose();
        }

        internal static X509Certificate2 TryCreateLocalhostCertificate()
        {
            try
            {
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest(
                    "CN=localhost",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, false));
                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                        false));

                using var certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddDays(7));
                return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
            }
            catch (CryptographicException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                // Also covers PlatformNotSupportedException.
                return null;
            }
            catch (NotImplementedException)
            {
                return null;
            }
        }
    }
}
