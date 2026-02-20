using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Optional integrity and write behavior controls for Unity file helpers.
    /// </summary>
    public sealed class UnityAtomicWriteOptions
    {
        /// <summary>
        /// Optional expected SHA-256 checksum (hex, case-insensitive).
        /// </summary>
        public string ExpectedSha256Hex { get; set; }

        /// <summary>
        /// Optional additional integrity predicate.
        /// Return false to reject the write.
        /// </summary>
        public Func<ReadOnlyMemory<byte>, bool> IntegrityValidator { get; set; }

        /// <summary>
        /// When true, fail instead of falling back to safe-copy replacement.
        /// </summary>
        public bool RequireAtomicReplace { get; set; }
    }

    /// <summary>
    /// Canonical path validation and atomic write helpers for Unity extension APIs.
    /// </summary>
    public static class PathSafety
    {
        /// <summary>
        /// Resolves <paramref name="relativePath"/> under <paramref name="rootPath"/> and blocks traversal.
        /// </summary>
        public static string ResolvePathWithinRoot(
            string rootPath,
            string relativePath,
            string parameterName)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new ArgumentException(
                    "Root path cannot be null or empty.",
                    nameof(rootPath));
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException(
                    "Path cannot be null or empty.",
                    parameterName);
            }

            if (Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException(
                    "Path must be relative to the selected Unity data directory.",
                    parameterName);
            }

            var decodedPath = Uri.UnescapeDataString(relativePath);
            if (ContainsTraversalSegment(decodedPath))
            {
                throw new ArgumentException(
                    "Path traversal is not allowed.",
                    parameterName);
            }

            var canonicalRoot = Path.GetFullPath(rootPath);
            var combined = Path.Combine(canonicalRoot, relativePath);
            var canonicalTarget = Path.GetFullPath(combined);

            if (!IsPathWithinRoot(canonicalRoot, canonicalTarget))
            {
                throw new ArgumentException(
                    "Path escapes the selected root directory.",
                    parameterName);
            }

            return canonicalTarget;
        }

        /// <summary>
        /// Ensures the destination directory for <paramref name="filePath"/> exists.
        /// </summary>
        public static void EnsureDirectoryForFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException(
                    "File path cannot be null or empty.",
                    nameof(filePath));
            }

            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException(
                    $"Cannot determine destination directory for path '{filePath}'.");
            }

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        /// <summary>
        /// Writes to a temporary sibling path and then promotes to final path.
        /// </summary>
        public static async Task WriteAtomicAsync(
            string finalPath,
            ReadOnlyMemory<byte> payload,
            UnityAtomicWriteOptions options,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(finalPath))
                throw new ArgumentException("Path cannot be null or empty.", nameof(finalPath));

            options ??= new UnityAtomicWriteOptions();
            EnsureDirectoryForFile(finalPath);

            var tempPath = CreateTempSiblingPath(finalPath);
            var promoted = false;

            try
            {
                await WriteBytesAsync(tempPath, payload, cancellationToken).ConfigureAwait(false);
                ValidateIntegrity(payload, options);
                PromoteTempFile(tempPath, finalPath, options.RequireAtomicReplace);
                promoted = true;
            }
            finally
            {
                if (!promoted)
                {
                    TryDeleteFile(tempPath);
                }
            }
        }

        /// <summary>
        /// Computes SHA-256 hex digest for payload.
        /// </summary>
        public static string ComputeSha256Hex(ReadOnlyMemory<byte> payload)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] hash;
                if (MemoryMarshal.TryGetArray(payload, out var segment) && segment.Array != null)
                {
                    hash = sha256.ComputeHash(segment.Array, segment.Offset, segment.Count);
                }
                else
                {
                    hash = sha256.ComputeHash(payload.ToArray());
                }

                return ToHex(hash);
            }
        }

        public static bool IsPathWithinRoot(string canonicalRoot, string canonicalTarget)
        {
            if (string.IsNullOrWhiteSpace(canonicalRoot) || string.IsNullOrWhiteSpace(canonicalTarget))
                return false;

            var relative = Path.GetRelativePath(canonicalRoot, canonicalTarget);
            if (string.IsNullOrEmpty(relative) ||
                string.Equals(relative, ".", StringComparison.Ordinal))
            {
                return true;
            }

            if (Path.IsPathRooted(relative))
                return false;

            if (string.Equals(relative, "..", StringComparison.Ordinal))
                return false;

            return !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                   !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }

        private static void ValidateIntegrity(
            ReadOnlyMemory<byte> payload,
            UnityAtomicWriteOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.ExpectedSha256Hex))
            {
                var actual = ComputeSha256Hex(payload);
                if (!string.Equals(
                    NormalizeHex(actual),
                    NormalizeHex(options.ExpectedSha256Hex),
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Integrity verification failed: SHA-256 checksum mismatch.");
                }
            }

            if (options.IntegrityValidator != null && !options.IntegrityValidator(payload))
            {
                throw new InvalidOperationException(
                    "Integrity verification failed: custom integrity validator rejected payload.");
            }
        }

        private static async Task WriteBytesAsync(
            string path,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken)
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!payload.IsEmpty)
                {
                    if (MemoryMarshal.TryGetArray(payload, out var segment) && segment.Array != null)
                    {
                        await file.WriteAsync(
                                segment.Array,
                                segment.Offset,
                                segment.Count,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        var copy = payload.ToArray();
                        await file.WriteAsync(copy, 0, copy.Length, cancellationToken).ConfigureAwait(false);
                    }
                }

                file.Flush();
            }
        }

        private static void PromoteTempFile(
            string tempPath,
            string finalPath,
            bool requireAtomicReplace)
        {
            try
            {
                if (!File.Exists(finalPath))
                {
                    File.Move(tempPath, finalPath);
                    return;
                }

                File.Replace(tempPath, finalPath, null);
            }
            catch (Exception ex) when (!requireAtomicReplace)
            {
                SafeCopyPromote(tempPath, finalPath, ex);
            }
        }

        private static void SafeCopyPromote(string tempPath, string finalPath, Exception rootCause)
        {
            var stagedPath = CreateTempSiblingPath(finalPath);
            try
            {
                using (var source = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var destination = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(destination);
                    destination.Flush();
                }

                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }

                File.Move(stagedPath, finalPath);
                File.Delete(tempPath);

                Debug.LogWarning(
                    "[TurboHTTP] Atomic replace fallback path used for '" +
                    finalPath +
                    "'. Cause: " +
                    rootCause.Message);
            }
            catch
            {
                TryDeleteFile(stagedPath);
                TryDeleteFile(tempPath);
                throw;
            }
        }

        private static string CreateTempSiblingPath(string path)
        {
            var directory = Path.GetDirectoryName(path);
            var fileName = Path.GetFileName(path);
            var tempName = fileName + ".tmp-" + Guid.NewGuid().ToString("N");
            return Path.Combine(directory ?? string.Empty, tempName);
        }

        private static bool ContainsTraversalSegment(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            var normalized = path
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            var segments = normalized.Split(Path.DirectorySeparatorChar);
            for (var i = 0; i < segments.Length; i++)
            {
                if (string.Equals(segments[i], "..", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeHex(string hex)
        {
            return hex.Trim().ToUpperInvariant();
        }

        private static string ToHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            var chars = new char[bytes.Length * 2];
            const string Alphabet = "0123456789ABCDEF";

            for (var i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                chars[i * 2] = Alphabet[b >> 4];
                chars[(i * 2) + 1] = Alphabet[b & 0xF];
            }

            return new string(chars);
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
