using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Files;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Files
{
    public class FileDownloaderTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "turbohttp_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        [Test]
        public void Download_WritesBodyToFile()
        {
            Task.Run(async () =>
            {
                var content = Encoding.UTF8.GetBytes("Hello, downloaded file!");
                var transport = new MockTransport(HttpStatusCode.OK, body: content);
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                var downloader = new FileDownloader(client);
                var dest = Path.Combine(_tempDir, "test.txt");

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file.txt", dest);

                Assert.AreEqual(dest, result.FilePath);
                Assert.AreEqual(content.Length, result.FileSize);
                Assert.IsFalse(result.WasResumed);

                var written = File.ReadAllBytes(dest);
                Assert.AreEqual(content, written);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Download_Resume_AppendsToExistingFile()
        {
            Task.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "resume.bin");
                var part1 = new byte[] { 1, 2, 3, 4, 5 };
                var part2 = new byte[] { 6, 7, 8, 9, 10 };

                // Write initial partial file
                File.WriteAllBytes(dest, part1);

                // Mock server returns 206 with remaining bytes
                var headers = new HttpHeaders();
                headers.Set("Content-Length", part2.Length.ToString());
                var transport = new MockTransport(
                    HttpStatusCode.PartialContent, headers, part2);
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                var downloader = new FileDownloader(client);

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/big.bin", dest,
                    new DownloadOptions { EnableResume = true });

                Assert.IsTrue(result.WasResumed);
                Assert.AreEqual(10, result.FileSize);

                var final = File.ReadAllBytes(dest);
                Assert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, final);

                // Verify Range header was sent
                var range = transport.LastRequest.Headers.Get("Range");
                Assert.AreEqual("bytes=5-", range);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Download_ResumeNotSupported_StartsFromScratch()
        {
            Task.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "noresume.bin");
                var partial = new byte[] { 1, 2, 3 };
                File.WriteAllBytes(dest, partial);

                // Server returns 200 (not 206) — doesn't support resume
                var fullContent = new byte[] { 10, 20, 30, 40, 50 };
                var transport = new MockTransport(HttpStatusCode.OK, body: fullContent);
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                var downloader = new FileDownloader(client);

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file.bin", dest,
                    new DownloadOptions { EnableResume = true });

                Assert.IsFalse(result.WasResumed);
                Assert.AreEqual(5, result.FileSize);

                var final = File.ReadAllBytes(dest);
                Assert.AreEqual(fullContent, final);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Download_Checksum_MD5_Valid_Passes()
        {
            Task.Run(async () =>
            {
                var content = Encoding.UTF8.GetBytes("checksum test");
                var md5 = ComputeMd5(content);

                var transport = new MockTransport(HttpStatusCode.OK, body: content);
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                var downloader = new FileDownloader(client);
                var dest = Path.Combine(_tempDir, "checksum.txt");

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file", dest,
                    new DownloadOptions
                    {
                        VerifyChecksum = true,
                        ExpectedMd5 = md5
                    });

                Assert.AreEqual(content.Length, result.FileSize);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Download_Checksum_MD5_Mismatch_Throws()
        {
            AssertAsync.ThrowsAsync<ChecksumMismatchException>(() =>
            {
                return Task.Run(async () =>
                {
                    var content = Encoding.UTF8.GetBytes("good data");
                    var transport = new MockTransport(HttpStatusCode.OK, body: content);
                    var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                    var downloader = new FileDownloader(client);
                    var dest = Path.Combine(_tempDir, "badchecksum.txt");

                    await downloader.DownloadFileAsync(
                        "https://test.com/file", dest,
                        new DownloadOptions
                        {
                            VerifyChecksum = true,
                            ExpectedMd5 = "0000000000000000000000000000dead"
                        });
                });
            });
        }

        [Test]
        public void Download_Checksum_SHA256_Valid_Passes()
        {
            Task.Run(async () =>
            {
                var content = Encoding.UTF8.GetBytes("sha256 test");
                var sha256 = ComputeSha256(content);

                var transport = new MockTransport(HttpStatusCode.OK, body: content);
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                var downloader = new FileDownloader(client);
                var dest = Path.Combine(_tempDir, "sha256.txt");

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file", dest,
                    new DownloadOptions
                    {
                        VerifyChecksum = true,
                        ExpectedSha256 = sha256
                    });

                Assert.AreEqual(content.Length, result.FileSize);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Download_ProgressReported()
        {
            Task.Run(async () =>
            {
                var content = Encoding.UTF8.GetBytes("progress data here");
                var headers = new HttpHeaders();
                headers.Set("Content-Length", content.Length.ToString());
                var transport = new MockTransport(HttpStatusCode.OK, headers, content);
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                var downloader = new FileDownloader(client);
                var dest = Path.Combine(_tempDir, "progress.txt");

                DownloadProgress lastProgress = null;
                var options = new DownloadOptions
                {
                    Progress = new Progress<DownloadProgress>(p => lastProgress = p)
                };

                await downloader.DownloadFileAsync(
                    "https://test.com/file", dest, options);

                // Progress may be reported asynchronously via Progress<T>
                // Wait briefly for the Progress callback to fire
                await Task.Delay(100);

                Assert.IsNotNull(lastProgress);
                Assert.AreEqual(content.Length, lastProgress.BytesDownloaded);
                Assert.AreEqual(content.Length, lastProgress.TotalBytes);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Download_StreamingResponse_ReportsProgressPerChunk()
        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Length", "9");
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        headers,
                        new MockResponseBodySource(
                            new[]
                            {
                                (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("abc"),
                                Encoding.UTF8.GetBytes("def"),
                                Encoding.UTF8.GetBytes("ghi")
                            },
                            length: 9,
                            trailers: HttpHeaders.Empty,
                            exposeBufferedData: false),
                        ctx).AsTask();
                });

                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                var downloader = new FileDownloader(client);
                var dest = Path.Combine(_tempDir, "stream-progress.txt");

                int progressCount = 0;
                long maxBytesDownloaded = 0;
                long maxTotalBytes = 0;
                var options = new DownloadOptions
                {
                    Progress = new Progress<DownloadProgress>(progress =>
                    {
                        progressCount++;
                        if (progress.BytesDownloaded > maxBytesDownloaded)
                            maxBytesDownloaded = progress.BytesDownloaded;
                        if (progress.TotalBytes > maxTotalBytes)
                            maxTotalBytes = progress.TotalBytes;
                    })
                };

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file", dest, options);

                await Task.Delay(100).ConfigureAwait(false);

                Assert.AreEqual(9, result.FileSize);
                Assert.That(progressCount, Is.GreaterThanOrEqualTo(3));
                Assert.AreEqual(9, maxBytesDownloaded);
                Assert.AreEqual(9, maxTotalBytes);
                Assert.AreEqual("abcdefghi", Encoding.UTF8.GetString(File.ReadAllBytes(dest)));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Download_HttpError_Throws()
        {
            AssertAsync.ThrowsAsync<UHttpException>(() =>
            {
                return Task.Run(async () =>
                {
                    var transport = new MockTransport(HttpStatusCode.NotFound);
                    var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                    var downloader = new FileDownloader(client);
                    var dest = Path.Combine(_tempDir, "error.txt");

                    await downloader.DownloadFileAsync(
                        "https://test.com/missing", dest);
                });
            });
        }

        [Test]
        public void Download_CreatesDirectoryIfNotExists()
        {
            Task.Run(async () =>
            {
                var content = new byte[] { 0x42 };
                var transport = new MockTransport(HttpStatusCode.OK, body: content);
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                var downloader = new FileDownloader(client);
                var dest = Path.Combine(_tempDir, "sub", "dir", "file.bin");

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file", dest);

                Assert.IsTrue(File.Exists(dest));
                Assert.AreEqual(1, result.FileSize);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Constructor_NullClient_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new FileDownloader(null));
        }

        [Test]
        public void Download_416_RetriesFromScratch()
        {
            Task.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "range416.bin");
                var partial = new byte[] { 1, 2, 3 };
                File.WriteAllBytes(dest, partial);

                var fullContent = new byte[] { 10, 20, 30, 40, 50 };
                var callCount = 0;
                // First call returns 416 (Range Not Satisfiable), second returns 200
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        return Task.FromResult(new UHttpResponse(
                            (HttpStatusCode)416, new HttpHeaders(), Array.Empty<byte>(), ctx.Elapsed, req));
                    }
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK, new HttpHeaders(), fullContent, ctx.Elapsed, req));
                });
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                var downloader = new FileDownloader(client);

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file.bin", dest,
                    new DownloadOptions { EnableResume = true });

                Assert.IsFalse(result.WasResumed);
                Assert.AreEqual(5, result.FileSize);
                Assert.AreEqual(2, callCount);

                var final = File.ReadAllBytes(dest);
                Assert.AreEqual(fullContent, final);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Download_ContentRange_Mismatch_StartsFromScratch()
        {
            Task.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "rangemismatch.bin");
                var partial = new byte[] { 1, 2, 3, 4, 5 };
                File.WriteAllBytes(dest, partial);

                // Server returns 206 but with Content-Range starting at 0 (not 5)
                var fullBody = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
                var headers = new HttpHeaders();
                headers.Set("Content-Range", "bytes 0-9/10");
                headers.Set("Content-Length", "10");
                var transport = new MockTransport(
                    HttpStatusCode.PartialContent, headers, fullBody);
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                var downloader = new FileDownloader(client);

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file.bin", dest,
                    new DownloadOptions { EnableResume = true });

                // Should have detected mismatch and started fresh (Create mode)
                Assert.IsFalse(result.WasResumed);
                Assert.AreEqual(10, result.FileSize);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Download_Resume_WithStreamingChecksum_SeedsExistingFileHash()
        {
            Task.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "resume-checksum.bin");
                var prefix = Encoding.UTF8.GetBytes("hello ");
                var suffix = Encoding.UTF8.GetBytes("world");
                var full = Encoding.UTF8.GetBytes("hello world");
                File.WriteAllBytes(dest, prefix);

                var headers = new HttpHeaders();
                headers.Set("Content-Length", suffix.Length.ToString());
                headers.Set("Content-Range", "bytes 6-10/11");
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.PartialContent,
                        headers,
                        new MockResponseBodySource(
                            new[] { (ReadOnlyMemory<byte>)suffix },
                            length: suffix.Length,
                            trailers: HttpHeaders.Empty,
                            exposeBufferedData: false),
                        ctx).AsTask();
                });

                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                var downloader = new FileDownloader(client);

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file.bin",
                    dest,
                    new DownloadOptions
                    {
                        EnableResume = true,
                        VerifyChecksum = true,
                        ExpectedSha256 = ComputeSha256(full)
                    });

                Assert.IsTrue(result.WasResumed);
                Assert.AreEqual(full.Length, result.FileSize);
                Assert.AreEqual("bytes=6-", transport.LastRangeHeader);
                Assert.AreEqual("hello world", Encoding.UTF8.GetString(File.ReadAllBytes(dest)));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Download_BasePath_RejectsTraversalOutsideBaseDirectory()
        {
            AssertAsync.ThrowsAsync<ArgumentException>(() =>
            {
                return Task.Run(async () =>
                {
                    var transport = new MockTransport(HttpStatusCode.OK, body: new byte[] { 0x01 });
                    var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                    var downloader = new FileDownloader(client)
                    {
                        BasePath = _tempDir
                    };

                    var outsidePath = Path.Combine(_tempDir, "..", "outside.bin");
                    await downloader.DownloadFileAsync("https://test.com/file.bin", outsidePath);
                });
            });
        }

        [Test]
        public void Download_BasePath_MixedCaseBehavior_IsPlatformAware()
        {
            Task.Run(async () =>
            {
                var content = new byte[] { 0x7A };
                var transport = new MockTransport(HttpStatusCode.OK, body: content);
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
                var downloader = new FileDownloader(client)
                {
                    BasePath = _tempDir.ToUpperInvariant()
                };
                var destination = Path.Combine(_tempDir, "case-aware.bin");

                if (IsCaseInsensitiveFileSystem())
                {
                    var result = await downloader.DownloadFileAsync(
                        "https://test.com/file.bin", destination);
                    Assert.AreEqual(destination, result.FilePath);
                    Assert.IsTrue(File.Exists(destination));
                }
                else
                {
                    AssertAsync.ThrowsAsync<ArgumentException>(async () =>
                        await downloader.DownloadFileAsync("https://test.com/file.bin", destination));
                }
            }).GetAwaiter().GetResult();
        }

        // --- Helpers ---

        private static bool IsCaseInsensitiveFileSystem()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                   RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        }

        private static string ComputeMd5(byte[] data)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(data);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string ComputeSha256(byte[] data)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(data);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private sealed class CallbackTransport : IHttpTransport
        {
            private readonly Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> _dispatch;

            internal CallbackTransport(Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> dispatch)
            {
                _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            }

            internal string LastRangeHeader { get; private set; }

            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                LastRangeHeader = request?.Headers?.Get("Range");
                return _dispatch(request, handler, context, cancellationToken);
            }

            public ValueTask<UHttpResponse> SendAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
            }
        }
    }
}
