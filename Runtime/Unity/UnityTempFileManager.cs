using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Runtime policy for the Unity temp file manager.
    /// </summary>
    public sealed class UnityTempFileManagerOptions
    {
        public int ShardCount { get; set; } = 32;
        public int MaxActiveFiles { get; set; } = 128;
        public int MaxConcurrentIo { get; set; } = 2;
        public int CleanupRetryCount { get; set; } = 3;
        public TimeSpan CleanupRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

        public UnityTempFileManagerOptions Clone()
        {
            return new UnityTempFileManagerOptions
            {
                ShardCount = ShardCount,
                MaxActiveFiles = MaxActiveFiles,
                MaxConcurrentIo = MaxConcurrentIo,
                CleanupRetryCount = CleanupRetryCount,
                CleanupRetryDelay = CleanupRetryDelay
            };
        }

        public void Validate()
        {
            if (ShardCount < 1)
                throw new ArgumentOutOfRangeException(nameof(ShardCount));
            if (MaxActiveFiles < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxActiveFiles));
            if (MaxConcurrentIo < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxConcurrentIo));
            if (CleanupRetryCount < 0)
                throw new ArgumentOutOfRangeException(nameof(CleanupRetryCount));
            if (CleanupRetryDelay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(CleanupRetryDelay));
        }
    }

    /// <summary>
    /// Snapshot metrics for temp-file lifecycle behavior.
    /// </summary>
    public readonly struct UnityTempFileManagerMetrics
    {
        public UnityTempFileManagerMetrics(
            int activeFiles,
            int pendingDeleteQueueDepth,
            long created,
            long deleted,
            long startupSweepDeleted,
            long cleanupRetries,
            long cleanupFailures)
        {
            ActiveFiles = activeFiles;
            PendingDeleteQueueDepth = pendingDeleteQueueDepth;
            Created = created;
            Deleted = deleted;
            StartupSweepDeleted = startupSweepDeleted;
            CleanupRetries = cleanupRetries;
            CleanupFailures = cleanupFailures;
        }

        public int ActiveFiles { get; }
        public int PendingDeleteQueueDepth { get; }
        public long Created { get; }
        public long Deleted { get; }
        public long StartupSweepDeleted { get; }
        public long CleanupRetries { get; }
        public long CleanupFailures { get; }
    }

    /// <summary>
    /// Centralized temp-file lifecycle manager for Unity content handlers.
    /// </summary>
    public sealed class UnityTempFileManager
    {
        private const string RootFolderName = "TurboHTTP";
        private const string AudioFolderName = "audio";
        private const string FilePrefix = "turbohttp-audio-";

        private readonly object _gate = new object();
        private readonly HashSet<string> _activeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<DeleteWorkItem> _deleteQueue = new Queue<DeleteWorkItem>();

        private UnityTempFileManagerOptions _options = new UnityTempFileManagerOptions();
        private SemaphoreSlim _ioLimiter;

        private bool _deleteWorkerRunning;
        private int _startupSweepCompleted;

        private long _created;
        private long _deleted;
        private long _startupSweepDeleted;
        private long _cleanupRetries;
        private long _cleanupFailures;

        public static UnityTempFileManager Shared { get; } = new UnityTempFileManager();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            var shared = Shared;
            lock (shared._gate)
            {
                shared._activeFiles.Clear();
                shared._deleteQueue.Clear();
                shared._options = new UnityTempFileManagerOptions();
                shared._deleteWorkerRunning = false;
                Interlocked.Exchange(ref shared._startupSweepCompleted, 0);
                Interlocked.Exchange(ref shared._created, 0);
                Interlocked.Exchange(ref shared._deleted, 0);
                Interlocked.Exchange(ref shared._startupSweepDeleted, 0);
                Interlocked.Exchange(ref shared._cleanupRetries, 0);
                Interlocked.Exchange(ref shared._cleanupFailures, 0);

                var oldLimiter = shared._ioLimiter;
                shared._ioLimiter = new SemaphoreSlim(
                    shared._options.MaxConcurrentIo,
                    shared._options.MaxConcurrentIo);
                oldLimiter?.Dispose();
            }
        }

        private UnityTempFileManager()
        {
            _ioLimiter = new SemaphoreSlim(_options.MaxConcurrentIo, _options.MaxConcurrentIo);
        }

        public UnityTempFileManagerOptions GetOptions()
        {
            lock (_gate)
            {
                return _options.Clone();
            }
        }

        public void Configure(UnityTempFileManagerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var clone = options.Clone();
            clone.Validate();

            lock (_gate)
            {
                var currentIo = _options.MaxConcurrentIo;
                _options = clone;

                if (clone.MaxConcurrentIo != currentIo)
                {
                    var oldLimiter = _ioLimiter;
                    _ioLimiter = new SemaphoreSlim(clone.MaxConcurrentIo, clone.MaxConcurrentIo);
                    oldLimiter?.Dispose();
                }
            }
        }

        public void EnsureStartupSweep()
        {
            if (Interlocked.Exchange(ref _startupSweepCompleted, 1) != 0)
                return;

            var root = GetRootPath();
            if (!Directory.Exists(root))
                return;

            try
            {
                var files = Directory.GetFiles(root, FilePrefix + "*", SearchOption.AllDirectories);
                for (var i = 0; i < files.Length; i++)
                {
                    if (TryDeleteNow(files[i]))
                    {
                        Interlocked.Increment(ref _startupSweepDeleted);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[TurboHTTP] Temp-file startup sweep encountered an error: " + ex.Message);
            }
        }

        public bool TryReservePath(string extension, out string path)
        {
            EnsureStartupSweep();

            if (string.IsNullOrWhiteSpace(extension))
                extension = ".bin";
            if (!extension.StartsWith(".", StringComparison.Ordinal))
                extension = "." + extension;

            lock (_gate)
            {
                if (_activeFiles.Count >= _options.MaxActiveFiles)
                {
                    path = null;
                    return false;
                }

                var token = Guid.NewGuid().ToString("N");
                var shard = ComputeShard(token, _options.ShardCount);
                var directory = Path.Combine(GetRootPath(), shard);
                path = Path.Combine(directory, FilePrefix + token + extension);

                _activeFiles.Add(path);
                _created++;
                return true;
            }
        }

        public async Task WriteBytesAsync(
            string path,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));

            await _ioLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
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
            finally
            {
                _ioLimiter.Release();
            }
        }

        public void ReleaseAndScheduleDelete(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            lock (_gate)
            {
                _activeFiles.Remove(path);
                _deleteQueue.Enqueue(new DeleteWorkItem(path, 0));

                if (_deleteWorkerRunning)
                    return;

                _deleteWorkerRunning = true;
                _ = Task.Run(ProcessDeleteQueueAsync);
            }
        }

        public UnityTempFileManagerMetrics GetMetrics()
        {
            lock (_gate)
            {
                return new UnityTempFileManagerMetrics(
                    activeFiles: _activeFiles.Count,
                    pendingDeleteQueueDepth: _deleteQueue.Count,
                    created: _created,
                    deleted: _deleted,
                    startupSweepDeleted: _startupSweepDeleted,
                    cleanupRetries: _cleanupRetries,
                    cleanupFailures: _cleanupFailures);
            }
        }

        private async Task ProcessDeleteQueueAsync()
        {
            while (true)
            {
                DeleteWorkItem workItem;
                UnityTempFileManagerOptions options;

                lock (_gate)
                {
                    if (_deleteQueue.Count == 0)
                    {
                        _deleteWorkerRunning = false;
                        return;
                    }

                    workItem = _deleteQueue.Dequeue();
                    options = _options.Clone();
                }

                await _ioLimiter.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (TryDeleteNow(workItem.Path))
                    {
                        lock (_gate)
                        {
                            _deleted++;
                        }

                        continue;
                    }
                }
                finally
                {
                    _ioLimiter.Release();
                }

                if (workItem.Attempt >= options.CleanupRetryCount)
                {
                    lock (_gate)
                    {
                        _cleanupFailures++;
                    }

                    Debug.LogWarning(
                        "[TurboHTTP] Temp-file cleanup permanently failed for '" +
                        workItem.Path +
                        "'. It will be retried on next startup sweep.");

                    continue;
                }

                lock (_gate)
                {
                    _cleanupRetries++;

                    if (_deleteQueue.Count < _options.MaxActiveFiles * 2)
                    {
                        _deleteQueue.Enqueue(new DeleteWorkItem(workItem.Path, workItem.Attempt + 1));
                    }
                    else
                    {
                        _cleanupFailures++;
                        Debug.LogWarning(
                            "[TurboHTTP] Delete queue depth limit reached. " +
                            "Abandoning retry for temp file.");
                    }
                }

                if (options.CleanupRetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(options.CleanupRetryDelay).ConfigureAwait(false);
                }
            }
        }

        private static bool TryDeleteNow(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return true;

                File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ComputeShard(string token, int shardCount)
        {
            if (string.IsNullOrEmpty(token) || shardCount <= 1)
                return "00";

            unchecked
            {
                var hash = 17;
                for (var i = 0; i < token.Length; i++)
                {
                    hash = (hash * 31) + token[i];
                }

                var shard = (hash & int.MaxValue) % shardCount;
                return shard.ToString("D2");
            }
        }

        private static string GetRootPath()
        {
            return Path.Combine(Application.temporaryCachePath, RootFolderName, AudioFolderName);
        }

        private readonly struct DeleteWorkItem
        {
            public DeleteWorkItem(string path, int attempt)
            {
                Path = path;
                Attempt = attempt;
            }

            public string Path { get; }
            public int Attempt { get; }
        }
    }
}
