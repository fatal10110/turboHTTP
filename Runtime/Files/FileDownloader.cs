using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Files
{
    /// <summary>
    /// Progress information for file downloads.
    /// <para><b>Note:</b> Progress is reported incrementally as response bytes are written to disk.
    /// When the server does not send <c>Content-Length</c>, <see cref="TotalBytes"/> remains 0
    /// until completion.</para>
    /// </summary>
    public class DownloadProgress
    {
        public long BytesDownloaded { get; set; }
        public long TotalBytes { get; set; }
        public float Percentage => TotalBytes > 0 ? (float)BytesDownloaded / TotalBytes * 100f : 0f;
        public TimeSpan Elapsed { get; set; }
        public double SpeedBytesPerSecond { get; set; }
    }

    /// <summary>
    /// Options for file downloads.
    /// </summary>
    public class DownloadOptions
    {
        public bool EnableResume { get; set; } = true;
        public bool VerifyChecksum { get; set; }
        public string ExpectedMd5 { get; set; }
        public string ExpectedSha256 { get; set; }
        public IProgress<DownloadProgress> Progress { get; set; }
    }

    /// <summary>
    /// Result of a file download operation.
    /// </summary>
    public class DownloadResult
    {
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public bool WasResumed { get; set; }
    }

    /// <summary>
    /// Thrown when a downloaded file's checksum does not match the expected value.
    /// </summary>
    public class ChecksumMismatchException : Exception
    {
        public string Algorithm { get; }
        public string Expected { get; }
        public string Actual { get; }

        public ChecksumMismatchException(string algorithm, string expected, string actual)
            : base($"{algorithm} checksum mismatch. Expected: {expected}, Actual: {actual}")
        {
            Algorithm = algorithm;
            Expected = expected;
            Actual = actual;
        }
    }

    /// <summary>
    /// File downloader with resume support, progress tracking, and checksum verification.
    /// <para><b>Memory Note:</b> Response bodies are streamed directly to disk using a pooled
    /// transfer buffer. Large downloads no longer require the full payload to be buffered in
    /// managed memory.</para>
    /// <para><b>Security Note:</b> If <c>destinationPath</c> is constructed from untrusted
    /// input (e.g., server-provided filenames), use <see cref="BasePath"/> to restrict writes
    /// to a specific directory. Without <c>BasePath</c>, the caller is responsible for
    /// validating the destination path.</para>
    /// </summary>
    public class FileDownloader
    {
        private const int DownloadBufferSize = 32 * 1024;

        private readonly UHttpClient _client;
        private static readonly StringComparison PathComparison =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        /// <summary>
        /// Optional base directory to restrict file writes. When set, all destination paths
        /// are validated to be within this directory after canonicalization.
        /// Prevents path traversal attacks when paths are constructed from untrusted input.
        /// </summary>
        public string BasePath { get; set; }

        public FileDownloader(UHttpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>
        /// Download a file to the specified path.
        /// Supports resume if the server supports Range requests (HTTP 206).
        /// Handles HTTP 416 (Range Not Satisfiable) by retrying from scratch.
        /// </summary>
        /// <exception cref="UHttpException">HTTP request failed</exception>
        /// <exception cref="ChecksumMismatchException">Checksum verification failed</exception>
        public async Task<DownloadResult> DownloadFileAsync(
            string url,
            string destinationPath,
            DownloadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (url == null) throw new ArgumentNullException(nameof(url));
            if (destinationPath == null) throw new ArgumentNullException(nameof(destinationPath));
            if (options == null) options = new DownloadOptions();

            // Path traversal protection: canonicalize and validate against BasePath
            destinationPath = Path.GetFullPath(destinationPath);
            if (BasePath != null)
            {
                // NOTE: Path.GetFullPath does not resolve symbolic links. Symlink-aware
                // canonicalization is deferred to a future hardening phase.
                var canonicalBase = EnsureTrailingDirectorySeparator(Path.GetFullPath(BasePath));
                if (!destinationPath.StartsWith(canonicalBase, PathComparison))
                    throw new ArgumentException(
                        $"Destination path escapes the allowed base directory. " +
                        $"Base: {canonicalBase}, Resolved: {destinationPath}",
                        nameof(destinationPath));
            }

            var startTime = DateTime.UtcNow;
            long existingSize = 0;

            // Check for resumable partial file
            if (options.EnableResume && File.Exists(destinationPath))
            {
                existingSize = new FileInfo(destinationPath).Length;
            }

            // Build request with optional Range header
            var requestBuilder = _client.Get(url);
            if (existingSize > 0)
            {
                requestBuilder.WithHeader("Range", $"bytes={existingSize}-");
            }

            UHttpStreamingResponse response = await requestBuilder.SendStreamingAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Handle 416 Range Not Satisfiable: delete partial file and retry from scratch
                if (existingSize > 0 && (int)response.StatusCode == 416)
                {
                    existingSize = 0;
                    if (File.Exists(destinationPath))
                        File.Delete(destinationPath);

                    await response.DisposeAsync().ConfigureAwait(false);
                    response = await _client.Get(url).SendStreamingAsync(cancellationToken).ConfigureAwait(false);
                }

                // If server doesn't support resume (200 instead of 206), start from scratch
                if (existingSize > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    existingSize = 0;
                    if (File.Exists(destinationPath))
                        File.Delete(destinationPath);
                }

                // Validate Content-Range on 206 responses
                if (existingSize > 0 && response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var contentRange = response.Headers.Get("Content-Range");
                    if (contentRange != null)
                    {
                        // Expected format: "bytes <start>-<end>/<total>"
                        var rangeStart = ParseContentRangeStart(contentRange);
                        if (rangeStart >= 0 && rangeStart != existingSize)
                        {
                            // Server returned a different range than we requested — start fresh
                            existingSize = 0;
                            if (File.Exists(destinationPath))
                                File.Delete(destinationPath);
                        }
                    }
                }

                // Non-success and non-partial = error
                if (((int)response.StatusCode < 200 || (int)response.StatusCode >= 300) &&
                    response.StatusCode != HttpStatusCode.PartialContent)
                {
                    ThrowIfNotSuccessful(response);
                }

                // Determine total size from Content-Length
                long totalSize = 0;
                var contentLengthHeader = response.Headers.Get("Content-Length");
                if (!string.IsNullOrEmpty(contentLengthHeader) && long.TryParse(contentLengthHeader, out var cl))
                {
                    totalSize = existingSize + cl;
                }

                // Ensure directory exists
                var dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using (var hashState = CreateHashState(options))
                {
                    if (hashState.IsEnabled && existingSize > 0)
                    {
                        await SeedHashStateFromExistingFileAsync(
                                destinationPath,
                                hashState,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    long downloadedThisSession = 0;
                    var fileMode = existingSize > 0 ? FileMode.Append : FileMode.Create;
                    using (var fileStream = new FileStream(
                        destinationPath,
                        fileMode,
                        FileAccess.Write,
                        FileShare.None,
                        DownloadBufferSize,
                        FileOptions.Asynchronous))
                    {
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferSize);
                        try
                        {
                            while (true)
                            {
                                int bytesRead = await response.Body.ReadAsync(
                                        buffer.AsMemory(0, buffer.Length),
                                        cancellationToken)
                                    .ConfigureAwait(false);
                                if (bytesRead == 0)
                                    break;

                                await fileStream.WriteAsync(
                                        new ReadOnlyMemory<byte>(buffer, 0, bytesRead),
                                        cancellationToken)
                                    .ConfigureAwait(false);

                                downloadedThisSession += bytesRead;
                                hashState.Append(buffer, bytesRead);
                                ReportProgress(
                                    options.Progress,
                                    existingSize + downloadedThisSession,
                                    downloadedThisSession,
                                    totalSize,
                                    startTime);
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }

                        await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    VerifyHashes(options, hashState);
                }

                return new DownloadResult
                {
                    FilePath = destinationPath,
                    FileSize = new FileInfo(destinationPath).Length,
                    ElapsedTime = DateTime.UtcNow - startTime,
                    WasResumed = existingSize > 0
                };
            }
            finally
            {
                if (response != null)
                    await response.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Parse the start byte from a Content-Range header value.
        /// Expected format: "bytes start-end/total" or "bytes start-end/*".
        /// Returns -1 if parsing fails.
        /// </summary>
        private static long ParseContentRangeStart(string contentRange)
        {
            // "bytes 500-999/1234"
            var spaceIndex = contentRange.IndexOf(' ');
            if (spaceIndex < 0) return -1;

            var dashIndex = contentRange.IndexOf('-', spaceIndex + 1);
            if (dashIndex < 0) return -1;

            var startStr = contentRange.Substring(spaceIndex + 1, dashIndex - spaceIndex - 1).Trim();
            if (long.TryParse(startStr, out var start))
                return start;

            return -1;
        }

        private static string EnsureTrailingDirectorySeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            if (path[path.Length - 1] == Path.DirectorySeparatorChar ||
                path[path.Length - 1] == Path.AltDirectorySeparatorChar)
                return path;

            return path + Path.DirectorySeparatorChar;
        }

        private static void ThrowIfNotSuccessful(UHttpStreamingResponse response)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response));

            if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
                return;

            var errorMsg = $"HTTP request failed with status {(int)response.StatusCode} {response.StatusCode}";
            throw new UHttpException(new UHttpError(
                UHttpErrorType.HttpError,
                errorMsg,
                statusCode: response.StatusCode));
        }

        private static void ReportProgress(
            IProgress<DownloadProgress> progress,
            long bytesDownloaded,
            long bytesTransferredThisOperation,
            long totalBytes,
            DateTime startTimeUtc)
        {
            if (progress == null)
                return;

            var elapsed = DateTime.UtcNow - startTimeUtc;
            progress.Report(new DownloadProgress
            {
                BytesDownloaded = bytesDownloaded,
                TotalBytes = totalBytes,
                Elapsed = elapsed,
                SpeedBytesPerSecond = elapsed.TotalSeconds > 0
                    ? bytesTransferredThisOperation / elapsed.TotalSeconds
                    : 0
            });
        }

        private static DownloadHashState CreateHashState(DownloadOptions options)
        {
            return new DownloadHashState(
                options != null &&
                options.VerifyChecksum &&
                !string.IsNullOrEmpty(options.ExpectedMd5),
                options != null &&
                options.VerifyChecksum &&
                !string.IsNullOrEmpty(options.ExpectedSha256));
        }

        private static async Task SeedHashStateFromExistingFileAsync(
            string filePath,
            DownloadHashState hashState,
            CancellationToken cancellationToken)
        {
            if (hashState == null || !hashState.IsEnabled)
                return;

            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DownloadBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferSize);
            try
            {
                while (true)
                {
                    int bytesRead = await stream.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (bytesRead == 0)
                        break;

                    hashState.Append(buffer, bytesRead);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static void VerifyHashes(DownloadOptions options, DownloadHashState hashState)
        {
            if (options == null || !options.VerifyChecksum || hashState == null)
                return;

            if (!string.IsNullOrEmpty(options.ExpectedMd5))
            {
                var actualMd5 = hashState.GetMd5HexAndReset();
                if (!actualMd5.Equals(options.ExpectedMd5, StringComparison.OrdinalIgnoreCase))
                    throw new ChecksumMismatchException("MD5", options.ExpectedMd5, actualMd5);
            }

            if (!string.IsNullOrEmpty(options.ExpectedSha256))
            {
                var actualSha256 = hashState.GetSha256HexAndReset();
                if (!actualSha256.Equals(options.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new ChecksumMismatchException("SHA256", options.ExpectedSha256, actualSha256);
            }
        }

        private sealed class DownloadHashState : IDisposable
        {
            private readonly IncrementalHash _md5;
            private readonly IncrementalHash _sha256;

            internal DownloadHashState(bool useMd5, bool useSha256)
            {
                if (useMd5)
                    _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                if (useSha256)
                    _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            }

            internal bool IsEnabled => _md5 != null || _sha256 != null;

            internal void Append(byte[] buffer, int length)
            {
                if (buffer == null || length <= 0)
                    return;

                _md5?.AppendData(buffer, 0, length);
                _sha256?.AppendData(buffer, 0, length);
            }

            internal string GetMd5HexAndReset()
            {
                return ToHexLower(_md5.GetHashAndReset());
            }

            internal string GetSha256HexAndReset()
            {
                return ToHexLower(_sha256.GetHashAndReset());
            }

            public void Dispose()
            {
                _md5?.Dispose();
                _sha256?.Dispose();
            }

            private static string ToHexLower(byte[] hash)
            {
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
