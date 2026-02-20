using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Unity;
using UnityEngine;

namespace TurboHTTP.Tests.UnityModule
{
    public class UnityPathSafetyTests
    {
        [Test]
        public void ResolvePathWithinRoot_BlocksTraversal()
        {
            var root = Path.Combine(Application.temporaryCachePath, "turbohttp-path-test");
            Directory.CreateDirectory(root);

            Assert.Throws<ArgumentException>(() =>
                PathSafety.ResolvePathWithinRoot(root, Path.Combine("..", "escape.bin"), "relativePath"));
        }

        [Test]
        public void ResolvePathWithinRoot_BlocksEncodedTraversal()
        {
            var root = Path.Combine(Application.temporaryCachePath, "turbohttp-path-test");
            Directory.CreateDirectory(root);

            Assert.Throws<ArgumentException>(() =>
                PathSafety.ResolvePathWithinRoot(root, "%2e%2e/escape.bin", "relativePath"));
        }

        [Test]
        public void WriteAtomicAsync_ChecksumMismatch_DoesNotOverwriteExistingFile()
        {
            var root = Path.Combine(Application.temporaryCachePath, "turbohttp-atomic", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var path = Path.Combine(root, "payload.bin");
            File.WriteAllText(path, "old", Encoding.UTF8);

            var options = new UnityAtomicWriteOptions
            {
                ExpectedSha256Hex = "00"
            };

            try
            {
                AssertAsync.ThrowsAsync<InvalidOperationException>(() =>
                    PathSafety.WriteAtomicAsync(path, new byte[] { 1, 2, 3 }, options, default));

                Assert.AreEqual("old", File.ReadAllText(path, Encoding.UTF8));
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        [Test]
        public void WriteAtomicAsync_IntegrityMatch_PublishesFile()
        {
            var root = Path.Combine(Application.temporaryCachePath, "turbohttp-atomic", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var path = Path.Combine(root, "payload.bin");
            var payload = Encoding.UTF8.GetBytes("phase15");

            var options = new UnityAtomicWriteOptions
            {
                ExpectedSha256Hex = PathSafety.ComputeSha256Hex(payload)
            };

            try
            {
                PathSafety.WriteAtomicAsync(path, payload, options, default).GetAwaiter().GetResult();
                CollectionAssert.AreEqual(payload, File.ReadAllBytes(path));
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
