using System;
using System.Buffers;
using System.Globalization;
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
        private const int MaxValidatorLength = 1024;

        /// <summary>Suffix of the in-progress file written next to the destination.</summary>
        internal const string PartFileSuffix = ".part";

        /// <summary>Suffix of the sidecar holding the partial's strong validator (sent as If-Range).</summary>
        internal const string ValidatorFileSuffix = ".part.validator";

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
        /// <para>Bytes are written to <c>destinationPath + ".part"</c>; the destination is only
        /// created (or atomically replaced) after the body completed and any checksum matched, so a
        /// failed, cancelled or corrupt download never leaves a truncated file at the destination
        /// and never touches an existing complete file there.</para>
        /// <para>With <see cref="DownloadOptions.EnableResume"/>, the strong validator of the
        /// response (strong <c>ETag</c>, or a strong <c>Last-Modified</c>) is kept in
        /// <c>destinationPath + ".part.validator"</c>, and a later call resumes the partial with
        /// <c>Range</c> + <c>If-Range</c> and <c>Accept-Encoding: identity</c>. A changed resource
        /// (200), a 416, or a 206 whose <c>Content-Range</c> does not continue the partial restarts
        /// the download from scratch. Error responses (4xx/5xx) throw and leave every file
        /// untouched. Partials without a strong validator are not resumed.</para>
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
            var partPath = destinationPath + PartFileSuffix;
            var validatorPath = destinationPath + ValidatorFileSuffix;
            long existingSize = 0;
            string ifRange = null;

            // Resume only a partial file whose strong validator was recorded when it was started
            // (RFC 9110 section 13.1.5: If-Range needs a strong validator). The destination path
            // itself always holds a complete file and is never appended to.
            if (options.EnableResume && File.Exists(partPath))
            {
                ifRange = TryReadValidator(validatorPath);
                if (ifRange != null)
                    existingSize = new FileInfo(partPath).Length;
                if (existingSize <= 0)
                {
                    existingSize = 0;
                    ifRange = null;
                }
            }

            UHttpStreamingResponse response = await SendDownloadRequestAsync(
                    url,
                    existingSize,
                    ifRange,
                    options.EnableResume,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (existingSize > 0)
                {
                    // 416: the partial no longer fits the representation. A 206 whose Content-Range
                    // does not continue exactly where the partial ends cannot be appended (RFC 9110
                    // section 14.4). Both mean the partial is unusable: discard it and start over.
                    bool restart = (int)response.StatusCode == 416 ||
                                   (response.StatusCode == HttpStatusCode.PartialContent &&
                                    !IsResumableContinuation(response.Headers, existingSize, ifRange));
                    if (restart)
                    {
                        existingSize = 0;
                        DeletePartFiles(partPath, validatorPath);

                        await response.DisposeAsync().ConfigureAwait(false);
                        response = null;
                        response = await SendDownloadRequestAsync(
                                url,
                                0,
                                null,
                                options.EnableResume,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                // Errors throw before any file is touched: the partial and any complete file at
                // the destination survive a 4xx/5xx (or a 416 on a non-range request).
                ThrowIfNotSuccessful(response);

                if (response.StatusCode == HttpStatusCode.PartialContent && existingSize == 0)
                {
                    // 206 is only valid as an answer to a Range request (RFC 9110 section 15.3.7).
                    throw new UHttpException(new UHttpError(
                        UHttpErrorType.HttpError,
                        "Server sent 206 Partial Content to a request without Range.",
                        statusCode: response.StatusCode));
                }

                // Any other 2xx on a resume attempt (200 when the server ignored Range or the
                // If-Range validator no longer matches) carries the full representation: restart.
                if (response.StatusCode != HttpStatusCode.PartialContent)
                    existingSize = 0;

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

                if (existingSize == 0)
                {
                    // Record the new representation's strong validator before writing its bytes,
                    // so an interrupted download can be resumed (and only resumed against the same
                    // representation). No strong validator: the partial will not be resumed.
                    WriteValidator(
                        validatorPath,
                        options.EnableResume ? SelectStrongValidator(response.Headers) : null);
                }

                bool bodyComplete = false;
                try
                {
                    using (var hashState = CreateHashState(options))
                    {
                        if (hashState.IsEnabled && existingSize > 0)
                        {
                            await SeedHashStateFromExistingFileAsync(
                                    partPath,
                                    hashState,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        long downloadedThisSession = 0;
                        var fileMode = existingSize > 0 ? FileMode.Append : FileMode.Create;
                        using (var fileStream = new FileStream(
                            partPath,
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

                        bodyComplete = true;
                        try
                        {
                            VerifyHashes(options, hashState);
                        }
                        catch (ChecksumMismatchException)
                        {
                            // Corrupt bytes must not be resumed from or appended to later.
                            DeletePartFiles(partPath, validatorPath);
                            throw;
                        }
                    }
                }
                finally
                {
                    // Without resume an interrupted partial has no further use.
                    if (!bodyComplete && !options.EnableResume)
                        DeletePartFiles(partPath, validatorPath);
                }

                // The destination only ever receives a complete, verified file.
                CommitPartFile(partPath, destinationPath);
                DeleteFileIfExists(validatorPath);

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

        private async Task<UHttpStreamingResponse> SendDownloadRequestAsync(
            string url,
            long rangeStart,
            string ifRange,
            bool enableResume,
            CancellationToken cancellationToken)
        {
            var request = _client.Get(url);
            if (enableResume)
            {
                // Byte offsets are only meaningful against one fixed coding of the representation,
                // and a content-coded range cannot be decoded on its own: keep downloads identity.
                request.WithHeader("Accept-Encoding", "identity");
            }

            if (rangeStart > 0)
            {
                request.WithHeader("Range", "bytes=" + rangeStart.ToString(CultureInfo.InvariantCulture) + "-");
                if (ifRange != null)
                    request.WithHeader("If-Range", ifRange);
            }

            return await request.SendStreamingAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// True when a 206 response continues the partial file exactly: a single
        /// <c>Content-Range: bytes first-last/complete</c> whose first byte equals the partial's
        /// length (RFC 9110 section 14.4), and, when the response carries an <c>ETag</c> and the
        /// stored validator is an entity-tag, the same entity-tag.
        /// </summary>
        private static bool IsResumableContinuation(HttpHeaders headers, long existingSize, string storedValidator)
        {
            var contentRange = headers.Get("Content-Range");
            if (contentRange == null ||
                !TryParseContentRange(contentRange, out var first, out _) ||
                first != existingSize)
            {
                return false;
            }

            if (storedValidator != null && storedValidator.Length > 0 && storedValidator[0] == '"')
            {
                var etag = headers.Get("ETag");
                if (etag != null && !string.Equals(etag.Trim(), storedValidator, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Parses <c>bytes first-last/complete-length</c> or <c>bytes first-last/*</c>
        /// (RFC 9110 section 14.4). Returns false for any other unit or shape.
        /// </summary>
        internal static bool TryParseContentRange(string contentRange, out long first, out long last)
        {
            first = -1;
            last = -1;
            if (contentRange == null)
                return false;

            var value = contentRange.Trim();
            const string unit = "bytes ";
            if (!value.StartsWith(unit, StringComparison.OrdinalIgnoreCase))
                return false;

            var dashIndex = value.IndexOf('-', unit.Length);
            var slashIndex = dashIndex < 0 ? -1 : value.IndexOf('/', dashIndex + 1);
            if (dashIndex < 0 || slashIndex < 0)
                return false;

            if (!long.TryParse(
                    value.Substring(unit.Length, dashIndex - unit.Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out first) ||
                !long.TryParse(
                    value.Substring(dashIndex + 1, slashIndex - dashIndex - 1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out last) ||
                last < first)
            {
                first = -1;
                last = -1;
                return false;
            }

            var complete = value.Substring(slashIndex + 1);
            if (complete == "*")
                return true;

            if (long.TryParse(complete, NumberStyles.None, CultureInfo.InvariantCulture, out var completeLength) &&
                completeLength > last)
            {
                return true;
            }

            first = -1;
            last = -1;
            return false;
        }

        /// <summary>
        /// Picks a strong validator usable in <c>If-Range</c> (RFC 9110 sections 8.8.1, 8.8.2.2
        /// and 13.1.5): a strong <c>ETag</c>, else a <c>Last-Modified</c> at least one second
        /// before the response <c>Date</c>. Weak entity-tags are never used.
        /// </summary>
        internal static string SelectStrongValidator(HttpHeaders headers)
        {
            var etag = headers.Get("ETag");
            if (etag != null)
            {
                etag = etag.Trim();
                if (etag.Length >= 2 && etag[0] == '"' && etag[etag.Length - 1] == '"' && IsSafeValidator(etag))
                    return etag;

                // A weak or malformed entity-tag: fall through to Last-Modified only when no
                // entity-tag was sent at all (RFC 9110 section 13.1.5 prefers the entity-tag).
                return null;
            }

            var lastModified = headers.Get("Last-Modified");
            var date = headers.Get("Date");
            if (lastModified == null || date == null)
                return null;

            lastModified = lastModified.Trim();
            if (!TryParseHttpDate(lastModified, out var lastModifiedAt) ||
                !TryParseHttpDate(date.Trim(), out var dateAt) ||
                lastModifiedAt > dateAt.AddSeconds(-1) ||
                !IsSafeValidator(lastModified))
            {
                return null;
            }

            return lastModified;
        }

        private static bool TryParseHttpDate(string value, out DateTimeOffset result)
        {
            return DateTimeOffset.TryParseExact(
                value,
                "r",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out result);
        }

        private static bool IsSafeValidator(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxValidatorLength)
                return false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < 0x20 || c > 0x7E)
                    return false;
            }

            return true;
        }

        private static string TryReadValidator(string validatorPath)
        {
            try
            {
                var info = new FileInfo(validatorPath);
                if (!info.Exists || info.Length == 0 || info.Length > MaxValidatorLength)
                    return null;

                var value = File.ReadAllText(validatorPath).Trim();
                if (!IsSafeValidator(value))
                    return null;

                bool isEntityTag = value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"';
                return isEntityTag || TryParseHttpDate(value, out _) ? value : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void WriteValidator(string validatorPath, string validator)
        {
            if (validator == null)
            {
                DeleteFileIfExists(validatorPath);
                return;
            }

            File.WriteAllText(validatorPath, validator);
        }

        private static void CommitPartFile(string partPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
                File.Replace(partPath, destinationPath, null);
            else
                File.Move(partPath, destinationPath);
        }

        private static void DeletePartFiles(string partPath, string validatorPath)
        {
            DeleteFileIfExists(partPath);
            DeleteFileIfExists(validatorPath);
        }

        private static void DeleteFileIfExists(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
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
