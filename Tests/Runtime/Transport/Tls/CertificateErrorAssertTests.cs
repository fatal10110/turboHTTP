using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #451: <see cref="CertificateErrorAssert"/> is what makes the TLS negative rows
    /// (<c>TlsPublicHostTests</c>, <see cref="TlsNegativeSuiteTests"/>) fail when the TLS stack cannot
    /// handshake at all. On the #450 Mono/High player every handshake died with a
    /// <see cref="TypeInitializationException"/> inside an <see cref="AuthenticationException"/>, which
    /// the transport reports as <see cref="UHttpErrorType.CertificateError"/>: a type-only assertion
    /// passed. A provider double reproduces that through the real transport mapping.
    /// </summary>
    [TestFixture]
    public class CertificateErrorAssertTests
    {
        private sealed class ThrowingTlsProvider : ITlsProvider
        {
            private readonly Exception _failure;

            public ThrowingTlsProvider(Exception failure)
            {
                _failure = failure;
            }

            public string ProviderName => "SslStream";

            public bool IsAlpnSupported() => true;

            public Task<TlsResult> WrapAsync(
                Stream innerStream, string host, string[] alpnProtocols, CancellationToken ct)
            {
                return Task.FromException<TlsResult>(_failure);
            }
        }

        [SetUp]
        [TearDown]
        public void ResetProbeState()
        {
            TlsProviderSelector.ResetProbeState();
        }

        [Test]
        public void CertificateRejection_ThroughTransport_Passes()
        {
            var failure = SendThroughTransport(new AuthenticationException(
                "The remote certificate is invalid according to the validation procedure."));

            Assert.IsNull(CertificateErrorAssert.Violation(failure), failure.ToString());
        }

        [Test]
        public void BrokenTlsStack_ThroughTransport_FailsTheAssertion()
        {
            var failure = SendThroughTransport(new AuthenticationException(
                "A call to SSPI failed, see inner exception.",
                new TypeInitializationException("System.Net.ServicePointManager", null)));

            // The transport still reports CertificateError: that is why a type-only row passed (#451).
            Assert.AreEqual(UHttpErrorType.CertificateError, failure.HttpError.Type, failure.ToString());
            var violation = CertificateErrorAssert.Violation(failure);
            Assert.IsNotNull(violation, "A handshake that died in a type initializer must not count as a certificate rejection.");
            StringAssert.Contains(nameof(TypeInitializationException), violation);
        }

        [TestCase(typeof(TypeInitializationException))]
        [TestCase(typeof(MissingMethodException))]
        [TestCase(typeof(DllNotFoundException))]
        [TestCase(typeof(EntryPointNotFoundException))]
        [TestCase(typeof(TypeLoadException))]
        [TestCase(typeof(PlatformNotSupportedException))]
        public void PlatformFailureInCauseChain_IsAViolation(Type causeType)
        {
            var cause = CreatePlatformFailure(causeType);
            var nested = new AuthenticationException("handshake failed", new IOException("wrapped", cause));

            var violation = CertificateErrorAssert.Violation(CertificateError(nested));

            Assert.IsNotNull(violation, causeType.Name + " anywhere in the cause chain is a broken TLS stack.");
            StringAssert.Contains(causeType.Name, violation);
        }

        [Test]
        public void PlatformFailureInsideAggregate_IsAViolation()
        {
            var aggregate = new AggregateException(
                new IOException("first"),
                new MissingMethodException("SslStream", "AuthenticateAsClientAsync"));

            Assert.IsNotNull(CertificateErrorAssert.Violation(
                CertificateError(new AuthenticationException("handshake failed", aggregate))));
        }

        [Test]
        public void CertificateErrorWithoutAuthenticationCause_IsAViolation()
        {
            var failure = new UHttpException(new UHttpError(UHttpErrorType.CertificateError, "no cause"));

            StringAssert.Contains(nameof(AuthenticationException), CertificateErrorAssert.Violation(failure));
        }

        [TestCase(UHttpErrorType.NetworkError)]
        [TestCase(UHttpErrorType.Timeout)]
        [TestCase(UHttpErrorType.Unknown)]
        public void OtherErrorType_IsAViolation(UHttpErrorType type)
        {
            var failure = new UHttpException(new UHttpError(type, "x", new AuthenticationException("x")));

            Assert.IsNotNull(CertificateErrorAssert.Violation(failure));
        }

        [Test]
        public void NonHttpException_IsAViolation()
        {
            Assert.IsNotNull(CertificateErrorAssert.Violation(new AuthenticationException("raw")));
            Assert.IsNotNull(CertificateErrorAssert.Violation(null));
        }

        [Test]
        public void ProviderLevelCause_IsCheckedToo()
        {
            Assert.IsNull(CertificateErrorAssert.FindBrokenTlsStackCause(
                new AuthenticationException("name mismatch")));
            Assert.IsInstanceOf<DllNotFoundException>(CertificateErrorAssert.FindBrokenTlsStackCause(
                new AuthenticationException("verifier", new DllNotFoundException("Security"))));
        }

        private static UHttpException CertificateError(AuthenticationException cause) =>
            new UHttpException(new UHttpError(UHttpErrorType.CertificateError, cause.Message, cause));

        private static Exception CreatePlatformFailure(Type type)
        {
            if (type == typeof(TypeInitializationException))
                return new TypeInitializationException("System.Net.ServicePointManager", null);
            if (type == typeof(MissingMethodException))
                return new MissingMethodException("SslStream", "AuthenticateAsClientAsync");
            if (type == typeof(DllNotFoundException))
                return new DllNotFoundException("libunitytls");
            if (type == typeof(EntryPointNotFoundException))
                return new EntryPointNotFoundException("SecTrustEvaluateWithError");
            if (type == typeof(TypeLoadException))
                return new TypeLoadException("Mono.Net.Security.MobileTlsProvider");
            if (type == typeof(PlatformNotSupportedException))
                return new PlatformNotSupportedException("no TLS");
            throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }

        /// <summary>
        /// Sends a request through <see cref="RawSocketTransport"/> to a loopback listener whose TLS
        /// provider throws <paramref name="handshakeFailure"/>, and returns what the caller observes.
        /// </summary>
        private static UHttpException SendThroughTransport(Exception handshakeFailure)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var provider = new ThrowingTlsProvider(handshakeFailure);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(new TcpConnectionPool(_ => provider)),
                    DisposeTransport = true
                });

                return AssertAsync.ThrowsAsync<UHttpException>(async () =>
                {
                    using var response = await client.Get($"https://127.0.0.1:{port}/")
                        .WithTimeout(TimeSpan.FromSeconds(30))
                        .SendBufferedAsync()
                        .ConfigureAwait(false);
                });
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
