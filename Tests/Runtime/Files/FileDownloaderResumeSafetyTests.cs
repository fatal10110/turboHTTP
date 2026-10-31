using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Files;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Files
{
    /// <summary>
    /// Error responses never destroy downloaded data (#50); resume only continues the same
    /// representation and the destination only appears after a verified completion (#66).
    /// </summary>
    public class FileDownloaderResumeSafetyTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "turbohttp_resume_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        [Test]
        public void Resume_ServerError_LeavesPartialAndValidatorUntouched()
        {
            AssertAsync.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "partial-503.bin");
                var partial = new byte[] { 1, 2, 3, 4, 5 };
                WritePartial(dest, partial, "\"v1\"");

                var transport = new MockTransport(HttpStatusCode.ServiceUnavailable);
                var downloader = CreateDownloader(transport);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(() => downloader.DownloadFileAsync(
                    "https://test.com/file.bin", dest, new DownloadOptions { EnableResume = true }));

                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.HttpError.StatusCode);
                Assert.AreEqual("bytes=5-", transport.LastRequest.Headers.Get("Range"));
                Assert.AreEqual(partial, File.ReadAllBytes(dest + ".part"));
                Assert.AreEqual("\"v1\"", File.ReadAllText(dest + ".part.validator"));
                Assert.IsFalse(File.Exists(dest));
                await Task.CompletedTask;
            });
        }

        [Test]
        public void ServerError_LeavesExistingCompleteFileUntouched()
        {
            AssertAsync.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "complete-503.bin");
                var complete = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1 };
                File.WriteAllBytes(dest, complete);

                var transport = new MockTransport(HttpStatusCode.ServiceUnavailable);
                var downloader = CreateDownloader(transport);

                AssertAsync.ThrowsAsync<UHttpException>(() => downloader.DownloadFileAsync(
                    "https://test.com/file.bin", dest, new DownloadOptions { EnableResume = true }));

                Assert.IsNull(transport.LastRequest.Headers.Get("Range"));
                Assert.AreEqual(complete, File.ReadAllBytes(dest));
                Assert.IsFalse(File.Exists(dest + ".part"));
                await Task.CompletedTask;
            });
        }

        [Test]
        public void Resume_ServerIgnoresRange_200_RestartsFromScratch()
        {
            AssertAsync.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "partial-200.bin");
                WritePartial(dest, new byte[] { 1, 2, 3 }, "\"v1\"");
                var full = new byte[] { 10, 20, 30, 40, 50 };

                var transport = new MockTransport(HttpStatusCode.OK, body: full);
                var downloader = CreateDownloader(transport);

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file.bin", dest, new DownloadOptions { EnableResume = true });

                Assert.IsFalse(result.WasResumed);
                Assert.AreEqual(full, File.ReadAllBytes(dest));
                Assert.IsFalse(File.Exists(dest + ".part"));
                Assert.IsFalse(File.Exists(dest + ".part.validator"));
            });
        }

        [Test]
        public void Resume_SendsIfRangeAndIdentityCoding_AndAppendsMatchingRange()
        {
            AssertAsync.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "resume-ok.bin");
                WritePartial(dest, new byte[] { 1, 2, 3, 4, 5 }, "\"v1\"");
                var server = new RangeServer(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, "\"v1\"");
                var downloader = CreateDownloader(server.Transport);

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file.bin", dest, new DownloadOptions { EnableResume = true });

                Assert.IsTrue(result.WasResumed);
                Assert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, File.ReadAllBytes(dest));
                Assert.AreEqual(1, server.CallCount);
                Assert.AreEqual("bytes=5-", server.LastRange);
                Assert.AreEqual("\"v1\"", server.LastIfRange);
                Assert.AreEqual("identity", server.LastAcceptEncoding);
                Assert.IsFalse(File.Exists(dest + ".part"));
                Assert.IsFalse(File.Exists(dest + ".part.validator"));
            });
        }

        [Test]
        public void Resume_ChangedResource_RestartsInsteadOfSplicing()
        {
            AssertAsync.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "resume-changed.bin");
                WritePartial(dest, new byte[] { 1, 2, 3, 4, 5 }, "\"v1\"");
                var changed = new byte[] { 50, 51, 52, 53, 54, 55, 56 };
                var server = new RangeServer(changed, "\"v2\"");
                var downloader = CreateDownloader(server.Transport);

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file.bin", dest, new DownloadOptions { EnableResume = true });

                Assert.IsFalse(result.WasResumed);
                Assert.AreEqual("\"v1\"", server.LastIfRange);
                Assert.AreEqual(changed, File.ReadAllBytes(dest));
            });
        }

        [Test]
        public void Resume_206WithoutContentRange_Restarts()
        {
            AssertAsync.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "resume-no-content-range.bin");
                WritePartial(dest, new byte[] { 1, 2, 3, 4, 5 }, "\"v1\"");
                var full = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                int callCount = 0;
                string secondRange = "unset";
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    int call = Interlocked.Increment(ref callCount);
                    if (call == 1)
                    {
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.PartialContent, new HttpHeaders(), new byte[] { 6, 7, 8 }, ctx.Elapsed, req));
                    }

                    secondRange = req.Headers.Get("Range");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK, new HttpHeaders(), full, ctx.Elapsed, req));
                });
                var downloader = CreateDownloader(transport);

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file.bin", dest, new DownloadOptions { EnableResume = true });

                Assert.IsFalse(result.WasResumed);
                Assert.AreEqual(2, callCount);
                Assert.IsNull(secondRange);
                Assert.AreEqual(full, File.ReadAllBytes(dest));
            });
        }

        [Test]
        public void Resume_206WithDifferentEntityTag_Restarts()
        {
            AssertAsync.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "resume-etag-mismatch.bin");
                WritePartial(dest, new byte[] { 1, 2, 3 }, "\"v1\"");
                var full = new byte[] { 7, 7, 7, 7, 7 };
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    int call = Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    headers.Set("ETag", "\"v2\"");
                    if (call == 1)
                    {
                        headers.Set("Content-Range", "bytes 3-4/5");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.PartialContent, headers, new byte[] { 7, 7 }, ctx.Elapsed, req));
                    }

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK, headers, full, ctx.Elapsed, req));
                });
                var downloader = CreateDownloader(transport);

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file.bin", dest, new DownloadOptions { EnableResume = true });

                Assert.IsFalse(result.WasResumed);
                Assert.AreEqual(2, callCount);
                Assert.AreEqual(full, File.ReadAllBytes(dest));
            });
        }

        [Test]
        public void PartialWithoutValidator_IsNotResumed()
        {
            AssertAsync.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "no-validator.bin");
                File.WriteAllBytes(dest + ".part", new byte[] { 1, 2, 3 });
                var full = new byte[] { 4, 5, 6, 7 };
                var transport = new MockTransport(HttpStatusCode.OK, body: full);
                var downloader = CreateDownloader(transport);

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file.bin", dest, new DownloadOptions { EnableResume = true });

                Assert.IsFalse(result.WasResumed);
                Assert.IsNull(transport.LastRequest.Headers.Get("Range"));
                Assert.AreEqual(full, File.ReadAllBytes(dest));
            });
        }

        [Test]
        public void InterruptedDownload_LeavesNoFinalFile_AndKeepsResumablePartial()
        {
            AssertAsync.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "interrupted.bin");
                var headers = new HttpHeaders();
                headers.Set("ETag", "\"abc\"");
                var transport = CreateChunkedTransport(headers, "abc", "def");
                var downloader = CreateDownloader(transport);

                AssertAsync.ThrowsAsync<InvalidOperationException>(() => downloader.DownloadFileAsync(
                    "https://test.com/file.bin",
                    dest,
                    new DownloadOptions { EnableResume = true, Progress = new ThrowOnFirstReport() }));

                Assert.IsFalse(File.Exists(dest));
                Assert.AreEqual("abc", File.ReadAllText(dest + ".part"));
                Assert.AreEqual("\"abc\"", File.ReadAllText(dest + ".part.validator"));
                await Task.CompletedTask;
            });
        }

        [Test]
        public void InterruptedDownload_WeakEntityTag_IsNotRecordedAsValidator()
        {
            AssertAsync.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "interrupted-weak.bin");
                var headers = new HttpHeaders();
                headers.Set("ETag", "W/\"abc\"");
                var transport = CreateChunkedTransport(headers, "abc", "def");
                var downloader = CreateDownloader(transport);

                AssertAsync.ThrowsAsync<InvalidOperationException>(() => downloader.DownloadFileAsync(
                    "https://test.com/file.bin",
                    dest,
                    new DownloadOptions { EnableResume = true, Progress = new ThrowOnFirstReport() }));

                Assert.IsFalse(File.Exists(dest));
                Assert.IsFalse(File.Exists(dest + ".part.validator"));
                await Task.CompletedTask;
            });
        }

        [Test]
        public void InterruptedDownload_WithoutResume_RemovesPartial()
        {
            AssertAsync.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "interrupted-noresume.bin");
                var transport = CreateChunkedTransport(new HttpHeaders(), "abc", "def");
                var downloader = CreateDownloader(transport);

                AssertAsync.ThrowsAsync<InvalidOperationException>(() => downloader.DownloadFileAsync(
                    "https://test.com/file.bin",
                    dest,
                    new DownloadOptions { EnableResume = false, Progress = new ThrowOnFirstReport() }));

                Assert.IsFalse(File.Exists(dest));
                Assert.IsFalse(File.Exists(dest + ".part"));
                await Task.CompletedTask;
            });
        }

        [Test]
        public void ChecksumMismatch_DiscardsPartial_AndKeepsPreviousFile()
        {
            AssertAsync.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "checksum.bin");
                var previous = Encoding.UTF8.GetBytes("previous");
                File.WriteAllBytes(dest, previous);
                var headers = new HttpHeaders();
                headers.Set("ETag", "\"v1\"");
                var transport = new MockTransport(HttpStatusCode.OK, headers, Encoding.UTF8.GetBytes("corrupt"));
                var downloader = CreateDownloader(transport);

                AssertAsync.ThrowsAsync<ChecksumMismatchException>(() => downloader.DownloadFileAsync(
                    "https://test.com/file.bin",
                    dest,
                    new DownloadOptions
                    {
                        EnableResume = true,
                        VerifyChecksum = true,
                        ExpectedSha256 = Sha256Hex(Encoding.UTF8.GetBytes("expected"))
                    }));

                Assert.AreEqual(previous, File.ReadAllBytes(dest));
                Assert.IsFalse(File.Exists(dest + ".part"));
                Assert.IsFalse(File.Exists(dest + ".part.validator"));
                await Task.CompletedTask;
            });
        }

        [Test]
        public void SuccessfulDownload_ReplacesExistingCompleteFile()
        {
            AssertAsync.Run(async () =>
            {
                var dest = Path.Combine(_tempDir, "replace.bin");
                File.WriteAllBytes(dest, new byte[] { 1, 1, 1, 1, 1, 1, 1, 1 });
                var fresh = new byte[] { 2, 2, 2 };
                var transport = new MockTransport(HttpStatusCode.OK, body: fresh);
                var downloader = CreateDownloader(transport);

                var result = await downloader.DownloadFileAsync(
                    "https://test.com/file.bin", dest, new DownloadOptions { EnableResume = true });

                Assert.AreEqual(3, result.FileSize);
                Assert.AreEqual(fresh, File.ReadAllBytes(dest));
                Assert.IsFalse(File.Exists(dest + ".part"));
            });
        }

        [TestCase("bytes 5-9/10", true, 5L, 9L)]
        [TestCase("bytes 5-9/*", true, 5L, 9L)]
        [TestCase("BYTES 0-0/1", true, 0L, 0L)]
        [TestCase("bytes 5-9/9", false, -1L, -1L)]
        [TestCase("bytes 9-5/10", false, -1L, -1L)]
        [TestCase("bytes */10", false, -1L, -1L)]
        [TestCase("items 5-9/10", false, -1L, -1L)]
        [TestCase("bytes -5-9/10", false, -1L, -1L)]
        [TestCase("bytes 5-9", false, -1L, -1L)]
        public void TryParseContentRange_FollowsRfc9110(string value, bool expected, long first, long last)
        {
            Assert.AreEqual(expected, FileDownloader.TryParseContentRange(value, out var parsedFirst, out var parsedLast));
            Assert.AreEqual(first, parsedFirst);
            Assert.AreEqual(last, parsedLast);
        }

        [Test]
        public void SelectStrongValidator_UsesStrongEntityTagOrStrongLastModifiedOnly()
        {
            var strong = new HttpHeaders();
            strong.Set("ETag", "\"x1\"");
            Assert.AreEqual("\"x1\"", FileDownloader.SelectStrongValidator(strong));

            var weak = new HttpHeaders();
            weak.Set("ETag", "W/\"x1\"");
            weak.Set("Last-Modified", "Sun, 06 Nov 1994 08:49:37 GMT");
            weak.Set("Date", "Mon, 07 Nov 1994 08:49:37 GMT");
            Assert.IsNull(FileDownloader.SelectStrongValidator(weak));

            var strongDate = new HttpHeaders();
            strongDate.Set("Last-Modified", "Sun, 06 Nov 1994 08:49:37 GMT");
            strongDate.Set("Date", "Mon, 07 Nov 1994 08:49:37 GMT");
            Assert.AreEqual("Sun, 06 Nov 1994 08:49:37 GMT", FileDownloader.SelectStrongValidator(strongDate));

            var weakDate = new HttpHeaders();
            weakDate.Set("Last-Modified", "Sun, 06 Nov 1994 08:49:37 GMT");
            weakDate.Set("Date", "Sun, 06 Nov 1994 08:49:37 GMT");
            Assert.IsNull(FileDownloader.SelectStrongValidator(weakDate));

            var noDate = new HttpHeaders();
            noDate.Set("Last-Modified", "Sun, 06 Nov 1994 08:49:37 GMT");
            Assert.IsNull(FileDownloader.SelectStrongValidator(noDate));
        }

        private static FileDownloader CreateDownloader(IHttpTransport transport)
        {
            return new FileDownloader(new UHttpClient(new UHttpClientOptions { Transport = transport }));
        }

        private static void WritePartial(string destination, byte[] partial, string validator)
        {
            File.WriteAllBytes(destination + ".part", partial);
            File.WriteAllText(destination + ".part.validator", validator);
        }

        private static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        private static IHttpTransport CreateChunkedTransport(HttpHeaders headers, params string[] chunks)
        {
            var memory = new ReadOnlyMemory<byte>[chunks.Length];
            long length = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                memory[i] = Encoding.UTF8.GetBytes(chunks[i]);
                length += memory[i].Length;
            }

            return new DispatchTransport((req, handler, ctx, ct) =>
            {
                handler.OnRequestStart(req, ctx);
                return handler.OnResponseStartAsync(
                    (int)HttpStatusCode.OK,
                    headers,
                    new MockResponseBodySource(memory, length, HttpHeaders.Empty, exposeBufferedData: false),
                    ctx).AsTask();
            });
        }

        /// <summary>Synchronous progress sink that aborts the download after the first chunk.</summary>
        private sealed class ThrowOnFirstReport : IProgress<DownloadProgress>
        {
            public void Report(DownloadProgress value)
            {
                throw new InvalidOperationException("interrupted after first chunk");
            }
        }

        /// <summary>Origin that honours Range + If-Range against one current representation.</summary>
        private sealed class RangeServer
        {
            private readonly byte[] _content;
            private readonly string _etag;
            private int _callCount;

            internal RangeServer(byte[] content, string etag)
            {
                _content = content;
                _etag = etag;
                Transport = new MockTransport((req, ctx, ct) =>
                {
                    Interlocked.Increment(ref _callCount);
                    LastRange = req.Headers.Get("Range");
                    LastIfRange = req.Headers.Get("If-Range");
                    LastAcceptEncoding = req.Headers.Get("Accept-Encoding");

                    var headers = new HttpHeaders();
                    headers.Set("ETag", _etag);
                    if (LastRange != null && LastRange.StartsWith("bytes=", StringComparison.Ordinal) &&
                        (LastIfRange == null || LastIfRange == _etag))
                    {
                        long start = long.Parse(LastRange.Substring(6, LastRange.Length - 7));
                        var slice = new byte[_content.Length - start];
                        Array.Copy(_content, start, slice, 0, slice.Length);
                        headers.Set("Content-Range", $"bytes {start}-{_content.Length - 1}/{_content.Length}");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.PartialContent, headers, slice, ctx.Elapsed, req));
                    }

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK, headers, _content, ctx.Elapsed, req));
                });
            }

            internal MockTransport Transport { get; }
            internal int CallCount => Volatile.Read(ref _callCount);
            internal string LastRange { get; private set; }
            internal string LastIfRange { get; private set; }
            internal string LastAcceptEncoding { get; private set; }
        }

        private sealed class DispatchTransport : IHttpTransport
        {
            private readonly Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> _dispatch;

            internal DispatchTransport(Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> dispatch)
            {
                _dispatch = dispatch;
            }

            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
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
