using System;
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
                writeOptions: null,
                cancellationToken);
        }

        /// <summary>
        /// Downloads content into Application.persistentDataPath using optional atomic-write options.
        /// </summary>
        public static Task<string> DownloadToPersistentDataAsync(
            this UHttpClient client,
            string url,
            string relativePath,
            UnityAtomicWriteOptions writeOptions,
            CancellationToken cancellationToken = default)
        {
            return DownloadToUnityPathAsync(
                client,
                url,
                Application.persistentDataPath,
                relativePath,
                writeOptions,
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
                writeOptions: null,
                cancellationToken);
        }

        /// <summary>
        /// Downloads content into Application.temporaryCachePath using optional atomic-write options.
        /// </summary>
        public static Task<string> DownloadToTempCacheAsync(
            this UHttpClient client,
            string url,
            string relativePath,
            UnityAtomicWriteOptions writeOptions,
            CancellationToken cancellationToken = default)
        {
            return DownloadToUnityPathAsync(
                client,
                url,
                Application.temporaryCachePath,
                relativePath,
                writeOptions,
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
            UnityAtomicWriteOptions writeOptions,
            CancellationToken cancellationToken)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL cannot be null or empty.", nameof(url));
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Path cannot be null or empty.", nameof(relativePath));
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new InvalidOperationException("Unity data path is unavailable.");

            var resolvedPath = PathSafety.ResolvePathWithinRoot(
                rootPath,
                relativePath,
                nameof(relativePath));

            var response = await client.Get(url).SendAsync(cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            cancellationToken.ThrowIfCancellationRequested();

            await PathSafety.WriteAtomicAsync(
                    resolvedPath,
                    response.Body,
                    writeOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            return resolvedPath;
        }

        private static string BuildDefaultUserAgent()
        {
            return "TurboHTTP/1.0 Unity/" + Application.unityVersion + " " + Application.platform;
        }
    }
}
