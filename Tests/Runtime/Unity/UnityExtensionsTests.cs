using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Unity;
using UnityEngine;

namespace TurboHTTP.Tests.UnityModule
{
    public class UnityExtensionsTests
    {
        [Test]
        public async Task DownloadToPersistentDataAsync_WritesFileInsidePersistentDataPath()
        {
            var payload = Encoding.UTF8.GetBytes("phase11");
            var transport = new MockTransport(HttpStatusCode.OK, body: payload);
            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });

            var relativePath = Path.Combine("turbohttp-tests", Guid.NewGuid().ToString("N"), "payload.bin");
            var writtenPath = await client.DownloadToPersistentDataAsync(
                "https://example.test/resource",
                relativePath);

            try
            {
                Assert.IsTrue(File.Exists(writtenPath));
                CollectionAssert.AreEqual(payload, File.ReadAllBytes(writtenPath));

                var persistentRoot = EnsureTrailingSeparator(Path.GetFullPath(Application.persistentDataPath));
                var fullWrittenPath = Path.GetFullPath(writtenPath);
                Assert.IsTrue(
                    fullWrittenPath.StartsWith(persistentRoot, StringComparison.OrdinalIgnoreCase),
                    $"Expected path '{fullWrittenPath}' to be inside '{persistentRoot}'.");
            }
            finally
            {
                TryDeleteFileAndParentDirectory(writtenPath);
            }
        }

        [Test]
        public void DownloadToPersistentDataAsync_RejectsPathTraversal()
        {
            var transport = new MockTransport(HttpStatusCode.OK, body: new byte[] { 1, 2, 3 });
            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });

            AssertAsync.ThrowsAsync<ArgumentException>(() =>
                client.DownloadToPersistentDataAsync(
                    "https://example.test/resource",
                    Path.Combine("..", "escape.bin")));
        }

        [Test]
        public async Task CreateUnityClient_AppliesDefaultUserAgent_AndAllowsOverride()
        {
            var defaultTransport = new MockTransport(HttpStatusCode.OK);
            using (var defaultClient = UnityExtensions.CreateUnityClient(options =>
                   {
                       options.Transport = defaultTransport;
                       options.DisposeTransport = true;
                   }))
            {
                await defaultClient.Get("https://example.test/default").SendAsync();
                var userAgent = defaultTransport.LastRequest.Headers.Get("User-Agent");
                StringAssert.Contains("TurboHTTP", userAgent);
                StringAssert.Contains("Unity/", userAgent);
            }

            var overrideTransport = new MockTransport(HttpStatusCode.OK);
            using (var overrideClient = UnityExtensions.CreateUnityClient(options =>
                   {
                       options.DefaultHeaders.Set("User-Agent", "Custom-UA");
                       options.Transport = overrideTransport;
                       options.DisposeTransport = true;
                   }))
            {
                await overrideClient.Get("https://example.test/override").SendAsync();
                Assert.AreEqual("Custom-UA", overrideTransport.LastRequest.Headers.Get("User-Agent"));
            }
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            if (path[path.Length - 1] == Path.DirectorySeparatorChar ||
                path[path.Length - 1] == Path.AltDirectorySeparatorChar)
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }

        private static void TryDeleteFileAndParentDirectory(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch
            {
                // Best-effort cleanup in tests.
            }
        }
    }
}
