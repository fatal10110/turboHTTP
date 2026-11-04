using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace TurboHTTP.Tests
{
    /// <summary>#321: test files go to a writable per-platform temp root.</summary>
    [TestFixture]
    public class TestTempPathTests
    {
        [Test]
        public void Root_IsTheEditorTempPathOrThePlayerCache()
        {
            var expected = Application.isEditor ? Path.GetTempPath() : Application.temporaryCachePath;

            Assert.AreEqual(
                Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(TestTempPath.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        [Test]
        public void NewDirectory_IsUniqueAndWritable()
        {
            var first = TestTempPath.NewDirectory("turbohttp_temp_path_");
            var second = TestTempPath.NewDirectory("turbohttp_temp_path_");
            try
            {
                Assert.AreNotEqual(first, second);
                StringAssert.StartsWith(TestTempPath.Root, first);

                var file = Path.Combine(first, "probe.bin");
                File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
            }
            finally
            {
                Directory.Delete(first, true);
                Directory.Delete(second, true);
            }
        }
    }
}
