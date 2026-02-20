using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Unity.Decoders;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Global configuration for <see cref="TextureDecodeScheduler"/>.
    /// </summary>
    public sealed class TextureDecodeSchedulerOptions
    {
        public int MaxConcurrentDecodes { get; set; } = 2;
        public int MaxQueuedDecodes { get; set; } = 128;

        public TextureDecodeSchedulerOptions Clone()
        {
            return new TextureDecodeSchedulerOptions
            {
                MaxConcurrentDecodes = MaxConcurrentDecodes,
                MaxQueuedDecodes = MaxQueuedDecodes
            };
        }

        public void Validate()
        {
            if (MaxConcurrentDecodes < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxConcurrentDecodes));
            if (MaxQueuedDecodes < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxQueuedDecodes));
        }
    }

    /// <summary>
    /// Scheduler metrics for texture decode queues.
    /// </summary>
    public readonly struct TextureDecodeSchedulerMetrics
    {
        public TextureDecodeSchedulerMetrics(
            int queueDepth,
            int activeWorkers,
            long scheduled,
            long completed,
            long dropped,
            long rejected)
        {
            QueueDepth = queueDepth;
            ActiveWorkers = activeWorkers;
            Scheduled = scheduled;
            Completed = completed;
            Dropped = dropped;
            Rejected = rejected;
        }

        public int QueueDepth { get; }
        public int ActiveWorkers { get; }
        public long Scheduled { get; }
        public long Completed { get; }
        public long Dropped { get; }
        public long Rejected { get; }
    }

    internal readonly struct ScheduledTextureDecodeResult
    {
        public ScheduledTextureDecodeResult(Texture2D texture, TimeSpan queueLatency)
        {
            Texture = texture;
            QueueLatency = queueLatency;
        }

        public Texture2D Texture { get; }
        public TimeSpan QueueLatency { get; }
    }

    /// <summary>
    /// Bounded scheduler for texture decode/finalization work.
    /// </summary>
    public sealed class TextureDecodeScheduler
    {
        public static TextureDecodeScheduler Shared { get; } = new TextureDecodeScheduler();

        private readonly object _gate = new object();
        private readonly Queue<PendingDecode> _pending = new Queue<PendingDecode>();

        private TextureDecodeSchedulerOptions _options = new TextureDecodeSchedulerOptions();
        private int _activeWorkers;

        private long _scheduled;
        private long _completed;
        private long _dropped;
        private long _rejected;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            var shared = Shared;
            Application.lowMemory -= shared.OnLowMemory;

            lock (shared._gate)
            {
                // Cancel all pending decodes
                while (shared._pending.Count > 0)
                {
                    var item = shared._pending.Dequeue();
                    item.Registration.Dispose();
                    item.Task.TrySetCanceled();
                }

                shared._options = new TextureDecodeSchedulerOptions();
                shared._activeWorkers = 0;
                shared._scheduled = 0;
                shared._completed = 0;
                shared._dropped = 0;
                shared._rejected = 0;
            }

            Application.lowMemory += shared.OnLowMemory;
        }

        private TextureDecodeScheduler()
        {
            Application.lowMemory -= OnLowMemory;
            Application.lowMemory += OnLowMemory;
        }

        public TextureDecodeSchedulerOptions GetOptions()
        {
            lock (_gate)
            {
                return _options.Clone();
            }
        }

        public void Configure(TextureDecodeSchedulerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var clone = options.Clone();
            clone.Validate();

            lock (_gate)
            {
                _options = clone;
                PumpWorkers_NoLock();
            }
        }

        internal Task<ScheduledTextureDecodeResult> ScheduleAsync(
            Func<CancellationToken, Task<Texture2D>> decodeAsync,
            CancellationToken cancellationToken)
        {
            if (decodeAsync == null) throw new ArgumentNullException(nameof(decodeAsync));

            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<ScheduledTextureDecodeResult>(cancellationToken);

            var tcs = new TaskCompletionSource<ScheduledTextureDecodeResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken))
                : default;

            lock (_gate)
            {
                var config = _options;

                if (_pending.Count >= config.MaxQueuedDecodes)
                {
                    _rejected++;
                    registration.Dispose();
                    return Task.FromException<ScheduledTextureDecodeResult>(
                        new InvalidOperationException(
                            "Texture decode scheduler queue is full."));
                }

                _pending.Enqueue(new PendingDecode(
                    decodeAsync,
                    tcs,
                    registration,
                    cancellationToken,
                    Stopwatch.GetTimestamp()));
                _scheduled++;

                PumpWorkers_NoLock();
            }

            return tcs.Task;
        }

        public TextureDecodeSchedulerMetrics GetMetrics()
        {
            lock (_gate)
            {
                return new TextureDecodeSchedulerMetrics(
                    queueDepth: _pending.Count,
                    activeWorkers: _activeWorkers,
                    scheduled: _scheduled,
                    completed: _completed,
                    dropped: _dropped,
                    rejected: _rejected);
            }
        }

        public Task WarmupAsync(CancellationToken cancellationToken = default)
        {
            return DecoderRegistry.WarmupImageDecodersAsync(cancellationToken);
        }

        private void PumpWorkers_NoLock()
        {
            while (_activeWorkers < _options.MaxConcurrentDecodes && _pending.Count > 0)
            {
                var item = _pending.Dequeue();
                _activeWorkers++;
                _ = RunDecodeAsync(item);
            }
        }

        private async Task RunDecodeAsync(PendingDecode item)
        {
            try
            {
                if (item.Task.Task.IsCompleted || item.CancellationToken.IsCancellationRequested)
                {
                    item.Registration.Dispose();
                    item.Task.TrySetCanceled(item.CancellationToken);
                    return;
                }

                var queueLatencyTicks = Stopwatch.GetTimestamp() - item.EnqueueTimestamp;
                var queueLatency = TimeSpan.FromSeconds(queueLatencyTicks / (double)Stopwatch.Frequency);

                var texture = await item.DecodeAsync(item.CancellationToken).ConfigureAwait(false);
                item.Task.TrySetResult(new ScheduledTextureDecodeResult(texture, queueLatency));
            }
            catch (OperationCanceledException)
            {
                item.Task.TrySetCanceled(item.CancellationToken);
            }
            catch (Exception ex)
            {
                item.Task.TrySetException(ex);
            }
            finally
            {
                item.Registration.Dispose();

                lock (_gate)
                {
                    _completed++;
                    _activeWorkers = Math.Max(0, _activeWorkers - 1);
                    PumpWorkers_NoLock();
                }
            }
        }

        private void OnLowMemory()
        {
            List<PendingDecode> dropped = null;

            lock (_gate)
            {
                if (_pending.Count == 0)
                    return;

                var toDrop = Math.Max(1, _pending.Count / 2);
                dropped = new List<PendingDecode>(toDrop);
                for (var i = 0; i < toDrop && _pending.Count > 0; i++)
                {
                    dropped.Add(_pending.Dequeue());
                    _dropped++;
                }
            }

            for (var i = 0; i < dropped.Count; i++)
            {
                dropped[i].Registration.Dispose();
                dropped[i].Task.TrySetException(new OperationCanceledException(
                    "Texture decode queue item dropped due to low-memory pressure."));
            }

            Debug.LogWarning(
                "[TurboHTTP] Texture decode scheduler dropped " +
                dropped.Count +
                " queued decode item(s) due to low-memory pressure.");
        }

        private readonly struct PendingDecode
        {
            public PendingDecode(
                Func<CancellationToken, Task<Texture2D>> decodeAsync,
                TaskCompletionSource<ScheduledTextureDecodeResult> task,
                CancellationTokenRegistration registration,
                CancellationToken cancellationToken,
                long enqueueTimestamp)
            {
                DecodeAsync = decodeAsync;
                Task = task;
                Registration = registration;
                CancellationToken = cancellationToken;
                EnqueueTimestamp = enqueueTimestamp;
            }

            public Func<CancellationToken, Task<Texture2D>> DecodeAsync { get; }
            public TaskCompletionSource<ScheduledTextureDecodeResult> Task { get; }
            public CancellationTokenRegistration Registration { get; }
            public CancellationToken CancellationToken { get; }
            public long EnqueueTimestamp { get; }
        }
    }
}
