using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Unity.Mobile;
using UnityEngine;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Unity-specific convenience APIs for client setup and file download targets.
    /// </summary>
    public static class UnityExtensions
    {
        /// <summary>
        /// Downloads content into Application.persistentDataPath using a validated relative path.
        /// </summary>
        public static Task<string> DownloadToPersistentDataAsync(
            this UHttpClient client,
            string url,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            return DownloadToUnityPathAsync(
                client,
                url,
                Application.persistentDataPath,
                relativePath,
                cancellationToken);
        }

        /// <summary>
        /// Downloads content into Application.temporaryCachePath using a validated relative path.
        /// </summary>
        public static Task<string> DownloadToTempCacheAsync(
            this UHttpClient client,
            string url,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            return DownloadToUnityPathAsync(
                client,
                url,
                Application.temporaryCachePath,
                relativePath,
                cancellationToken);
        }

        /// <summary>
        /// Creates a UHttpClient with Unity-friendly defaults and optional overrides.
        /// </summary>
        public static UHttpClient CreateUnityClient(Action<UHttpClientOptions> configure = null)
        {
            var options = new UHttpClientOptions();
            options.DefaultHeaders.Set("User-Agent", BuildDefaultUserAgent());

            configure?.Invoke(options);

            if (options.BackgroundNetworkingPolicy != null &&
                options.BackgroundNetworkingPolicy.Enable &&
                options.BackgroundExecutionBridge == null)
            {
                options.BackgroundExecutionBridge = BackgroundExecutionBridgeFactory.CreateDefault();
            }

            return new UHttpClient(options);
        }

        private static async Task<string> DownloadToUnityPathAsync(
            UHttpClient client,
            string url,
            string rootPath,
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL cannot be null or empty.", nameof(url));
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Path cannot be null or empty.", nameof(relativePath));
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new InvalidOperationException("Unity data path is unavailable.");

            var resolvedPath = ResolvePathWithinRoot(rootPath, relativePath, nameof(relativePath));
            EnsureDirectoryForFile(resolvedPath);

            var response = await client.Get(url).SendAsync(cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            cancellationToken.ThrowIfCancellationRequested();

            using (var file = new FileStream(resolvedPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (!response.Body.IsEmpty)
                {
                    await WriteBodyToFileAsync(file, response.Body, cancellationToken).ConfigureAwait(false);
                }
            }

            return resolvedPath;
        }

        private static string ResolvePathWithinRoot(
            string rootPath,
            string relativePath,
            string parameterName)
        {
            if (Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException(
                    "Path must be relative to the selected Unity data directory.",
                    parameterName);
            }

            var canonicalRoot = Path.GetFullPath(rootPath);
            var combined = Path.Combine(canonicalRoot, relativePath);
            var canonicalTarget = Path.GetFullPath(combined);

            if (!IsPathWithinRoot(canonicalRoot, canonicalTarget))
            {
                throw new ArgumentException(
                    $"Path escapes root directory. Root: {canonicalRoot}, Target: {canonicalTarget}",
                    parameterName);
            }

            return canonicalTarget;
        }

        private static void EnsureDirectoryForFile(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException(
                    $"Cannot determine destination directory for path '{filePath}'.");
            }

            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);
        }

        private static bool IsPathWithinRoot(string canonicalRoot, string canonicalTarget)
        {
            var relative = Path.GetRelativePath(canonicalRoot, canonicalTarget);
            if (string.IsNullOrEmpty(relative) ||
                string.Equals(relative, ".", StringComparison.Ordinal))
            {
                return true;
            }

            if (Path.IsPathRooted(relative))
            {
                return false;
            }

            if (string.Equals(relative, "..", StringComparison.Ordinal))
                return false;

            return !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                   !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }

        private static async Task WriteBodyToFileAsync(
            FileStream file,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
        {
            if (MemoryMarshal.TryGetArray(body, out var segment) && segment.Array != null)
            {
                await file.WriteAsync(
                        segment.Array,
                        segment.Offset,
                        segment.Count,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var bodyArray = body.ToArray();
            await file.WriteAsync(bodyArray, 0, bodyArray.Length, cancellationToken).ConfigureAwait(false);
        }

        private static string BuildDefaultUserAgent()
        {
            return "TurboHTTP/1.0 Unity/" + Application.unityVersion + " " + Application.platform;
        }
    }
}
