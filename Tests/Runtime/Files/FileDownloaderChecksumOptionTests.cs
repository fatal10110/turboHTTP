using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Files;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Files
{
    /// <summary>
    /// An expected hash alone turns verification on; the obsolete VerifyChecksum flag without an
    /// expected hash is rejected instead of verifying nothing (#1018).
    /// </summary>
    public class FileDownloaderChecksumOptionTests
    {
        private static readonly byte[] Body = Encoding.UTF8.GetBytes("corrupt payload");
        private static readonly byte[] Expected = Encoding.UTF8.GetBytes("expected payload");

        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(TestTempPath.Root, "turbohttp_checksum_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        [Test]
        public void ExpectedSha256Only_Mismatch_ThrowsUHttpException_AndRemovesPartial()
        {
            var dest = Path.Combine(_tempDir, "sha.bin");
            var downloader = CreateDownloader(CreateTransport());

            var ex = AssertAsync.ThrowsAsync<ChecksumMismatchException>(() => downloader.DownloadFileAsync(
                "https://test.com/file.bin",
                dest,
                new DownloadOptions { ExpectedSha256 = Hex(SHA256.Create(), Expected) }));

            Assert.IsInstanceOf<UHttpException>(ex);
            Assert.AreEqual("SHA256", ex.Algorithm);
            Assert.AreEqual(Hex(SHA256.Create(), Body), ex.Actual.ToLowerInvariant());
            AssertNothingLeft(dest);
        }

        [Test]
        public void ExpectedMd5Only_Mismatch_ThrowsUHttpException_AndRemovesPartial()
        {
            var dest = Path.Combine(_tempDir, "md5.bin");
            var downloader = CreateDownloader(CreateTransport());

            var ex = AssertAsync.ThrowsAsync<ChecksumMismatchException>(() => downloader.DownloadFileAsync(
                "https://test.com/file.bin",
                dest,
                new DownloadOptions { ExpectedMd5 = Hex(MD5.Create(), Expected) }));

            Assert.IsInstanceOf<UHttpException>(ex);
            Assert.AreEqual("MD5", ex.Algorithm);
            AssertNothingLeft(dest);
        }

        [Test]
        public void ExpectedSha256Only_Match_CommitsFile()
        {
            AssertAsync.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "ok.bin");
                var downloader = CreateDownloader(CreateTransport());

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file.bin",
                    dest,
                    new DownloadOptions { ExpectedSha256 = Hex(SHA256.Create(), Body).ToUpperInvariant() });

                Assert.AreEqual(Body.Length, result.FileSize);
                Assert.AreEqual(Body, File.ReadAllBytes(dest));
                Assert.IsFalse(File.Exists(dest + ".part"));
            });
        }

        [TestCase(null)]
        [TestCase("")]
        public void VerifyChecksumWithoutExpectedHash_ThrowsArgumentException_BeforeRequest(string expected)
        {
            var dest = Path.Combine(_tempDir, "flag.bin");
            var transport = CreateTransport();
            var downloader = CreateDownloader(transport);

#pragma warning disable CS0618 // the obsolete flag is the subject of this test
            var options = new DownloadOptions { VerifyChecksum = true, ExpectedSha256 = expected, ExpectedMd5 = expected };
#pragma warning restore CS0618

            var ex = AssertAsync.ThrowsAsync<ArgumentException>(() => downloader.DownloadFileAsync(
                "https://test.com/file.bin",
                dest,
                options));

            Assert.AreEqual("options", ex.ParamName);
            Assert.AreEqual(0, transport.RequestCount);
            Assert.IsFalse(File.Exists(dest));
        }

        private static MockTransport CreateTransport()
        {
            var headers = new HttpHeaders();
            headers.Set("ETag", "\"v1\"");
            return new MockTransport(HttpStatusCode.OK, headers, Body);
        }

        private static FileDownloader CreateDownloader(IHttpTransport transport)
        {
            return new FileDownloader(new UHttpClient(new UHttpClientOptions { Transport = transport }));
        }

        private static void AssertNothingLeft(string dest)
        {
            Assert.IsFalse(File.Exists(dest), "unverified bytes must not be committed");
            Assert.IsFalse(File.Exists(dest + ".part"));
            Assert.IsFalse(File.Exists(dest + ".part.validator"));
        }

        private static string Hex(HashAlgorithm algorithm, byte[] data)
        {
            using (algorithm)
                return BitConverter.ToString(algorithm.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
    }
}
