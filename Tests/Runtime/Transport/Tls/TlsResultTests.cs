using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    [TestFixture]
    public class TlsResultTests
    {
        [Test]
        public void Constructor_StoresSecureStream()
        {
            using var stream = new System.IO.MemoryStream();
            var result = new TlsResult(stream, "h2", "1.3", "TLS_AES_256_GCM_SHA384", "SslStream");
            Assert.AreSame(stream, result.SecureStream);
        }

        [Test]
        public void Constructor_StoresNegotiatedAlpn()
        {
            using var stream = new System.IO.MemoryStream();
            var result = new TlsResult(stream, "h2", "1.3", "TLS_AES_256_GCM_SHA384", "SslStream");
            Assert.AreEqual("h2", result.NegotiatedAlpn);
        }

        [Test]
        public void Constructor_StoresTlsVersion()
        {
            using var stream = new System.IO.MemoryStream();
            var result = new TlsResult(stream, "h2", "1.3", "TLS_AES_256_GCM_SHA384", "SslStream");
            Assert.AreEqual("1.3", result.TlsVersion);
        }

        [Test]
        public void Constructor_StoresCipherSuite()
        {
            using var stream = new System.IO.MemoryStream();
            var result = new TlsResult(stream, "h2", "1.3", "TLS_AES_256_GCM_SHA384", "SslStream");
            Assert.AreEqual("TLS_AES_256_GCM_SHA384", result.CipherSuite);
        }

        [Test]
        public void Constructor_StoresProviderName()
        {
            using var stream = new System.IO.MemoryStream();
            var result = new TlsResult(stream, "h2", "1.3", "TLS_AES_256_GCM_SHA384", "SslStream");
            Assert.AreEqual("SslStream", result.ProviderName);
        }

        [Test]
        public void Constructor_AllowsNullAlpn()
        {
            using var stream = new System.IO.MemoryStream();
            var result = new TlsResult(stream, null, "1.2", null, "SslStream");
            Assert.IsNull(result.NegotiatedAlpn);
        }

        [Test]
        public void Constructor_AllowsNullCipherSuite()
        {
            using var stream = new System.IO.MemoryStream();
            var result = new TlsResult(stream, "h2", "1.2", null, "SslStream");
            Assert.IsNull(result.CipherSuite);
        }

        [Test]
        public void Constructor_HttpVersion1_1_AlpnValue()
        {
            using var stream = new System.IO.MemoryStream();
            var result = new TlsResult(stream, "http/1.1", "1.2", null, "SslStream");
            Assert.AreEqual("http/1.1", result.NegotiatedAlpn);
        }
    }
}
