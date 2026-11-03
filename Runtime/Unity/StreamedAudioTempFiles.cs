using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// One temp file that exactly one party releases: the download path (on failure, cancellation or
    /// an in-memory clip) or the streamed-clip tracker (after the clip is destroyed), whichever
    /// claims it first (#96).
    /// </summary>
    internal sealed class TempFileLease
    {
        private int _claimed;

        internal TempFileLease(string path)
        {
            Path = path;
        }

        internal string Path { get; }

        internal bool TryClaim()
        {
            return Interlocked.Exchange(ref _claimed, 1) == 0;
        }
    }

    /// <summary>
    /// Keeps the temp file of a streamed <see cref="AudioClip"/> until the clip is destroyed. Unity
    /// streams such a clip from the file while it plays, and an <see cref="AudioClip"/> has no destroy
    /// callback, so a main-thread coroutine on the dispatcher checks the tracked clips every
    /// <see cref="SweepInterval"/> and releases the file of each destroyed one to
    /// <see cref="UnityTempFileManager"/> (#96). The coroutine stops when nothing is tracked.
    /// </summary>
    /// <remarks>
    /// Main thread only: <see cref="Track"/> runs in the clip-loading coroutine, the sweep in its
    /// own coroutine. Files of clips still alive when the player exits are removed by the
    /// temp-file manager's startup sweep on the next launch.
    /// </remarks>
    internal static class StreamedAudioTempFiles
    {
        internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

        private static readonly object Gate = new object();
        private static readonly List<Entry> Entries = new List<Entry>();
        private static MainThreadDispatcher _sweepHost;

        internal static int TrackedCount
        {
            get
            {
                lock (Gate)
                {
                    return Entries.Count;
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            lock (Gate)
            {
                Entries.Clear();
                _sweepHost = null;
            }
        }

        /// <summary>Tracks <paramref name="clip"/> as the owner of the file at <paramref name="path"/>.</summary>
        internal static void Track(AudioClip clip, string path)
        {
            if (clip == null)
                throw new ArgumentNullException(nameof(clip));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));

            var host = MainThreadDispatcher.Instance;
            var startSweep = false;
            lock (Gate)
            {
                Entries.Add(new Entry(clip, path));
                if (_sweepHost == null)
                {
                    // Unity's null check: a destroyed dispatcher took its coroutine with it.
                    _sweepHost = host;
                    startSweep = true;
                }
            }

            if (startSweep)
                host.StartCoroutine(SweepLoop(host));
        }

        /// <summary>The temp file tracked for <paramref name="clip"/>, for diagnostics and tests.</summary>
        internal static bool TryGetTrackedPath(AudioClip clip, out string path)
        {
            lock (Gate)
            {
                for (var i = 0; i < Entries.Count; i++)
                {
                    if (ReferenceEquals(Entries[i].Clip, clip))
                    {
                        path = Entries[i].Path;
                        return true;
                    }
                }
            }

            path = null;
            return false;
        }

        /// <summary>
        /// Releases the file of every destroyed clip and returns how many clips are still tracked.
        /// Main thread only.
        /// </summary>
        internal static int SweepNow()
        {
            List<string> released = null;
            int remaining;
            lock (Gate)
            {
                for (var i = Entries.Count - 1; i >= 0; i--)
                {
                    if (Entries[i].Clip != null)
                        continue;

                    if (released == null)
                        released = new List<string>();
                    released.Add(Entries[i].Path);
                    Entries.RemoveAt(i);
                }

                remaining = Entries.Count;
            }

            if (released != null)
            {
                for (var i = 0; i < released.Count; i++)
                    UnityTempFileManager.Shared.ReleaseAndScheduleDelete(released[i]);
            }

            return remaining;
        }

        private static IEnumerator SweepLoop(MainThreadDispatcher host)
        {
            var wait = new WaitForSecondsRealtime((float)SweepInterval.TotalSeconds);
            while (true)
            {
                yield return wait;

                SweepNow();
                if (StopIfIdleOrReplaced(host))
                    yield break;
            }
        }

        private static bool StopIfIdleOrReplaced(MainThreadDispatcher host)
        {
            lock (Gate)
            {
                if (!ReferenceEquals(_sweepHost, host))
                    return true;
                if (Entries.Count != 0)
                    return false;

                if (ReferenceEquals(_sweepHost, host))
                    _sweepHost = null;
                return true;
            }
        }

        private readonly struct Entry
        {
            internal Entry(AudioClip clip, string path)
            {
                Clip = clip;
                Path = path;
            }

            internal AudioClip Clip { get; }

            internal string Path { get; }
        }
    }
}
