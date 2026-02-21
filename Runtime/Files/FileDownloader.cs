using System;
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
    /// <para><b>Note:</b> With the current transport design, progress is reported once
    /// after the entire response body is received (not incrementally). Incremental progress
    /// requires streaming transport support (planned for Phase 10).</para>
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
    /// <para><b>Memory Note:</b> The entire response body is buffered in memory before
    /// being written to disk. For files larger than ~50MB on mobile, monitor available
    /// memory. Streaming transport is planned for Phase 10.</para>
    /// <para><b>Security Note:</b> If <c>destinationPath</c> is constructed from untrusted
    /// input (e.g., server-provided filenames), use <see cref="BasePath"/> to restrict writes
    /// to a specific directory. Without <c>BasePath</c>, the caller is responsible for
    /// validating the destination path.</para>
    /// </summary>
    public class FileDownloader
    {
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

            UHttpResponse response = await requestBuilder.SendAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Handle 416 Range Not Satisfiable: delete partial file and retry from scratch
                if (existingSize > 0 && (int)response.StatusCode == 416)
                {
                    existingSize = 0;
                    if (File.Exists(destinationPath))
                        File.Delete(destinationPath);

                    response.Dispose();
                    response = await _client.Get(url).SendAsync(cancellationToken).ConfigureAwait(false);
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
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    response.EnsureSuccessStatusCode(); // throws
                }

                // Determine total size from Content-Length
                long totalSize = existingSize;
                var contentLengthHeader = response.Headers.Get("Content-Length");
                if (!string.IsNullOrEmpty(contentLengthHeader) && long.TryParse(contentLengthHeader, out var cl))
                {
                    totalSize += cl;
                }

                // Write body to file
                var fileMode = existingSize > 0 ? FileMode.Append : FileMode.Create;

                // Ensure directory exists
                var dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using (var fileStream = new FileStream(
                    destinationPath, fileMode, FileAccess.Write, FileShare.None))
                {
                    var body = response.Body;
                    if (!body.IsEmpty)
                    {
                        await fileStream.WriteAsync(body, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                // Report progress (single report after download completes — see DownloadProgress doc)
                var bytesWritten = response.Body.Length;
                var elapsed = DateTime.UtcNow - startTime;
                options.Progress?.Report(new DownloadProgress
                {
                    BytesDownloaded = existingSize + bytesWritten,
                    TotalBytes = totalSize,
                    Elapsed = elapsed,
                    SpeedBytesPerSecond = elapsed.TotalSeconds > 0
                        ? bytesWritten / elapsed.TotalSeconds
                        : 0
                });

                // Verify checksum if requested
                if (options.VerifyChecksum)
                {
                    if (!string.IsNullOrEmpty(options.ExpectedMd5))
                    {
                        var actual = ComputeHash(destinationPath, MD5.Create());
                        if (!actual.Equals(options.ExpectedMd5, StringComparison.OrdinalIgnoreCase))
                            throw new ChecksumMismatchException("MD5", options.ExpectedMd5, actual);
                    }

                    if (!string.IsNullOrEmpty(options.ExpectedSha256))
                    {
                        var actual = ComputeHash(destinationPath, SHA256.Create());
                        if (!actual.Equals(options.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                            throw new ChecksumMismatchException("SHA256", options.ExpectedSha256, actual);
                    }
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
                response?.Dispose();
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

        /// <summary>
        /// Compute hash of a file. Takes ownership of the algorithm and disposes it.
        /// </summary>
        private static string ComputeHash(string filePath, HashAlgorithm algorithm)
        {
            using (algorithm)
            using (var stream = File.OpenRead(filePath))
            {
                var hash = algorithm.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
