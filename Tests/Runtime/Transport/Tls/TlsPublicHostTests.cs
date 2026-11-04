#if TURBOHTTP_INTEGRATION_TESTS
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// Public-host TLS rows (#361): a <see cref="UHttpClient"/> request to a public server with a bad
    /// certificate must fail with <see cref="UHttpErrorType.CertificateError"/> through the platform
    /// verifier on every backend, and a valid chain must succeed. The loopback twin with committed
    /// identities is <see cref="TlsNegativeSuiteTests"/>; this fixture covers the real public chain,
    /// the platform trust store and the device network.
    ///
    /// Hosts (chains checked 2026-11-04):
    /// <list type="bullet">
    /// <item><c>self-signed.badssl.com</c>: a self-signed <c>*.badssl.com</c> leaf.</item>
    /// <item><c>untrusted-root.badssl.com</c>: leaf under "BadSSL Untrusted Root Certificate Authority".</item>
    /// <item><c>expired.badssl.com</c>: expired leaf under the COMODO RSA chain.</item>
    /// <item><c>wrong.host.badssl.com</c>: valid <c>*.badssl.com</c> leaf for another name (its chain ends
    /// in ISRG Root X1, so Android 6 also rejects the root; both reasons are certificate errors).</item>
    /// <item><c>www.google.com</c> (valid): ECDSA leaf, WE2, GTS Root R4 cross-signed by GlobalSign Root
    /// CA. GlobalSign Root CA is in every store we target, including Android 6 (API 23), which lacks
    /// ISRG Root X1/X2 and Amazon Root CA 1, so Let's Encrypt and Amazon hosts are not used for the
    /// valid rows.</item>
    /// </list>
    /// A run that cannot reach a host (DNS, connect, timeout) is an environment skip, never a pass.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    [Category("ExternalNetwork")]
    public class TlsPublicHostTests
    {
        internal const string ValidChainUrl = "https://www.google.com/";

        private static readonly TimeSpan RequestBound = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan TestBound = TimeSpan.FromSeconds(40);

        [SetUp]
        public void SetUp()
        {
            HttpTransportFactory.Reset();
            RawSocketTransport.EnsureRegistered();
        }

        [TearDown]
        public void TearDown()
        {
            HttpTransportFactory.Reset();
        }

        [Test]
        public void SelfSigned_IsCertificateError(
            [Values(TlsBackend.SslStream, TlsBackend.BouncyCastle, TlsBackend.Auto)] TlsBackend backend)
        {
            AssertCertificateError(backend, "https://self-signed.badssl.com/");
        }

        [Test]
        public void UntrustedRoot_IsCertificateError(
            [Values(TlsBackend.SslStream, TlsBackend.BouncyCastle, TlsBackend.Auto)] TlsBackend backend)
        {
            AssertCertificateError(backend, "https://untrusted-root.badssl.com/");
        }

        [Test]
        public void Expired_IsCertificateError(
            [Values(TlsBackend.SslStream, TlsBackend.BouncyCastle, TlsBackend.Auto)] TlsBackend backend)
        {
            AssertCertificateError(backend, "https://expired.badssl.com/");
        }

        [Test]
        public void WrongHost_IsCertificateError(
            [Values(TlsBackend.SslStream, TlsBackend.BouncyCastle, TlsBackend.Auto)] TlsBackend backend)
        {
            AssertCertificateError(backend, "https://wrong.host.badssl.com/");
        }

        [Test]
        public void ValidChain_Succeeds(
            [Values(TlsBackend.SslStream, TlsBackend.BouncyCastle, TlsBackend.Auto)] TlsBackend backend)
        {
            RequireBackend(backend);
            var outcome = Send(backend, ValidChainUrl);
            IgnoreIfUnreachable(outcome, backend, ValidChainUrl);

            Assert.IsNull(outcome.Failure, outcome.Failure?.ToString());
            Assert.AreEqual(HttpStatusCode.OK, outcome.Status);
        }

        private static void AssertCertificateError(TlsBackend backend, string url)
        {
            RequireBackend(backend);
            var outcome = Send(backend, url);

            // A bad certificate that produced a response is a validation bypass, not an infra problem.
            Assert.IsNotNull(outcome.Failure,
                $"{url} over {backend} returned {outcome.Status}; its certificate must be rejected.");
            IgnoreIfUnreachable(outcome, backend, url);

            var ex = outcome.Failure as UHttpException;
            Assert.IsNotNull(ex, $"Expected UHttpException(CertificateError), got {outcome.Failure}");
            Assert.AreEqual(UHttpErrorType.CertificateError, ex.HttpError.Type, ex.ToString());
        }

        private static void RequireBackend(TlsBackend backend)
        {
            if (backend == TlsBackend.BouncyCastle)
                TestRequirements.RequireBouncyCastle();
        }

        /// <summary>
        /// Skips the test when the failure says the host was not reached: no DNS answer, no route, a
        /// refused connect, or the request bound expired. A TLS-level failure is never skipped.
        /// </summary>
        internal static void IgnoreIfUnreachable(SendOutcome outcome, TlsBackend backend, string url)
        {
            if (outcome.Failure == null || !IsUnreachable(outcome.Failure))
                return;

            TestRequirements.IgnoreEnvironment(
                $"{url} over {backend} was not reachable from this environment: " +
                $"{outcome.Failure.GetType().Name}: {outcome.Failure.Message}");
        }

        internal static bool IsUnreachable(Exception failure)
        {
            if (failure is OperationCanceledException)
                return true;
            if (failure is UHttpException http && http.HttpError.Type == UHttpErrorType.Timeout)
                return true;
            if (failure is UHttpException network && network.HttpError.Type == UHttpErrorType.NetworkError)
            {
                for (var inner = failure.InnerException; inner != null; inner = inner.InnerException)
                {
                    if (inner is SocketException)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Sends one bounded GET and captures the outcome without asserting inside the try, so an
        /// NUnit assertion exception never meets a catch clause (#320).
        /// </summary>
        internal static SendOutcome Send(TlsBackend backend, string url)
        {
            var outcome = new SendOutcome();
            AssertAsync.Run(async () =>
            {
                using var client = new UHttpClient(new UHttpClientOptions { TlsBackend = backend });
                using var cts = new CancellationTokenSource(RequestBound);
                try
                {
                    using var response = await client.Get(url).WithTimeout(RequestBound)
                        .SendBufferedAsync(cts.Token).ConfigureAwait(false);
                    outcome.Status = response.StatusCode;
                }
                catch (Exception ex)
                {
                    outcome.Failure = ex;
                }
            }, TestBound);
            return outcome;
        }

        internal sealed class SendOutcome
        {
            public HttpStatusCode Status;
            public Exception Failure;
        }
    }
}
#endif
