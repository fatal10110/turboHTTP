using System;
using System.IO;
using UnityEngine;

namespace TurboHTTP.Tests
{
    /// <summary>
    /// Root for the files tests write (#321). In the Editor it is <see cref="Path.GetTempPath"/>;
    /// in a player it is <see cref="Application.temporaryCachePath"/>, because an IL2CPP Android
    /// player's <see cref="Path.GetTempPath"/> is <c>/data/local/tmp</c>, which an app cannot write.
    /// Use it instead of <see cref="Path.GetTempPath"/> in every runtime test.
    /// </summary>
    /// <remarks>
    /// <see cref="Application.temporaryCachePath"/> may only be read on the Unity main thread. A
    /// player reads it once at startup (<see cref="Prime"/>); a first read from a worker thread
    /// before that falls back to <see cref="Path.GetTempPath"/>, which the device runner points at
    /// the same cache directory (<c>TMPDIR</c>).
    /// </remarks>
    internal static class TestTempPath
    {
        private static string _root;

        /// <summary>The directory test files go under; it exists.</summary>
        public static string Root
        {
            get
            {
                var root = System.Threading.Volatile.Read(ref _root);
                if (root != null)
                    return root;

                root = Resolve();
                System.Threading.Interlocked.CompareExchange(ref _root, root, null);
                return System.Threading.Volatile.Read(ref _root);
            }
        }

        /// <summary>Creates and returns a new, empty, uniquely named directory under <see cref="Root"/>.</summary>
        public static string NewDirectory(string prefix)
        {
            var path = Path.Combine(Root, prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

#if !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Prime()
        {
            _ = Root;
        }
#endif

        private static string Resolve()
        {
#if UNITY_EDITOR
            return Path.GetTempPath();
#else
            string cache = null;
            try
            {
                cache = Application.temporaryCachePath;
            }
            catch (Exception)
            {
                // Read off the main thread before Prime ran: use the process temp directory.
            }

            if (string.IsNullOrEmpty(cache))
                return Path.GetTempPath();

            Directory.CreateDirectory(cache);
            return cache;
#endif
        }
    }
}
