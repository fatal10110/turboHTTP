using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// Phase 15.6 stress suite (#105): dispatcher bursts, texture bursts under a frame budget and audio
    /// temp-file churn with a zero-leak budget. Frame time is measured by the dispatcher's own drain
    /// high-water marks (<see cref="MainThreadDispatcher.GetDrainStats"/>), never slept; the budgets
    /// come from <see cref="ReliabilityBudgets.Current"/> and are printed as metric lines.
    /// </summary>
    public class UnityStressSuiteTests
    {
        private const int ItemsPerFrame = 4;
        private const double WorkTimeMs = 2d;

        [UnityTest]
        public IEnumerator MainThreadDispatcher_MetricsRemainBoundedUnderBurst()
        {
            var defaults = MainThreadDispatcher.GetSettings();
            MainThreadDispatcher.Configure(new MainThreadDispatcherSettings
            {
                UserQueueCapacity = 64,
                BackpressurePolicy = MainThreadBackpressurePolicy.Reject,
                MaxPendingWaiters = 32,
                WaitTimeout = TimeSpan.FromMilliseconds(10),
                MaxItemsPerFrame = 16,
                MaxWorkTimeMs = 0.5,
                LowMemoryDropCount = 8,
                AllowInlineExecutionOnMainThread = false
            });

            try
            {
                var _ = MainThreadDispatcher.Instance;
                var tasks = new Task[256];
                for (var i = 0; i < tasks.Length; i++)
                {
                    tasks[i] = Task.Run(() => MainThreadDispatcher.ExecuteAsync(() => { }));
                }

                var all = Task.WhenAll(tasks);
                yield return new WaitUntil(() => all.IsCompleted);

                var metrics = MainThreadDispatcher.GetMetrics();
                Assert.LessOrEqual(metrics.Queue.UserQueueDepth, 64);
            }
            finally
            {
                MainThreadDispatcher.Configure(defaults);
            }
        }

        [UnityTest]
        public IEnumerator TextureCreationBurst_HonoursTheDispatcherItemAndTimeCaps()
        {
            Assert.IsNotNull(MainThreadDispatcher.Instance);
            var budgets = ReliabilityBudgets.Current;
            var defaults = MainThreadDispatcher.GetSettings();
            MainThreadDispatcher.Configure(StressSettings(defaults));

            var png = CreatePngBytes(256, 256);
            const int burst = 64;
            var textures = new Texture2D[burst];
            var tasks = new Task[burst];
            try
            {
                MainThreadDispatcher.ResetDrainStats();
                var startFrame = Time.frameCount;
                for (var i = 0; i < burst; i++)
                {
                    var index = i;
                    tasks[i] = Task.Run(() => MainThreadDispatcher.ExecuteAsync(() =>
                    {
                        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        texture.LoadImage(png, markNonReadable: true);
                        textures[index] = texture;
                    }));
                }

                var all = Task.WhenAll(tasks);
                yield return new WaitUntil(() => all.IsCompleted);
                var frames = Time.frameCount - startFrame;
                var stats = MainThreadDispatcher.GetDrainStats();

                Assert.AreEqual(TaskStatus.RanToCompletion, all.Status, all.Exception?.ToString());
                for (var i = 0; i < burst; i++)
                    Assert.AreEqual(256, textures[i].width, "texture " + i + " decoded");

                budgets.AssertWithin("texture_burst.max_items_per_frame", stats.MaxItemsPerFrame, ItemsPerFrame, "items");
                budgets.AssertWithin("texture_burst.max_late_start_ms", stats.MaxLateStartMs, budgets.MaxLateStartMs, "ms");
                budgets.AssertWithin("texture_burst.max_frame_work_ms", stats.MaxDrainMs, budgets.MaxFrameWorkMs, "ms");
                budgets.Record("texture_burst.frames", frames, "frames");
                Assert.GreaterOrEqual(frames, burst / ItemsPerFrame, "the burst is spread over frames, not run in one");
            }
            finally
            {
                MainThreadDispatcher.Configure(defaults);
                for (var i = 0; i < burst; i++)
                    UnityObjects.Destroy(textures[i]);
            }
        }

        [UnityTest]
        public IEnumerator TexturePipelineBurst_StaysWithinTheFrameBudget()
        {
            Assert.IsNotNull(MainThreadDispatcher.Instance);
            var budgets = ReliabilityBudgets.Current;
            var defaults = MainThreadDispatcher.GetSettings();
            var schedulerDefaults = TextureDecodeScheduler.Shared.GetOptions();
            MainThreadDispatcher.Configure(StressSettings(defaults));
            TextureDecodeScheduler.Shared.Configure(new TextureDecodeSchedulerOptions
            {
                MaxConcurrentDecodes = 16,
                MaxQueuedDecodes = 128
            });

            var png = CreatePngBytes(256, 256);
            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = new MockTransport(request => CreateResponse(png, "image/png", request)),
                DisposeTransport = true
            });

            const int burst = 32;
            var tasks = new Task<Texture2D>[burst];
            try
            {
                MainThreadDispatcher.ResetDrainStats();
                for (var i = 0; i < burst; i++)
                {
                    var url = "https://example.test/texture/" + i;
                    tasks[i] = Task.Run(() => client.GetTextureAsync(url, new TextureOptions { MipMaps = false }));
                }

                var all = Task.WhenAll(tasks);
                yield return new WaitUntil(() => all.IsCompleted);
                var stats = MainThreadDispatcher.GetDrainStats();

                Assert.AreEqual(TaskStatus.RanToCompletion, all.Status, all.Exception?.ToString());
                for (var i = 0; i < burst; i++)
                    Assert.AreEqual(256, tasks[i].Result.width, "texture " + i + " decoded");

                budgets.AssertWithin("texture_pipeline.max_items_per_frame", stats.MaxItemsPerFrame, ItemsPerFrame, "items");
                budgets.AssertWithin("texture_pipeline.max_late_start_ms", stats.MaxLateStartMs, budgets.MaxLateStartMs, "ms");
                budgets.AssertWithin("texture_pipeline.max_frame_work_ms", stats.MaxDrainMs, budgets.MaxFrameWorkMs, "ms");
            }
            finally
            {
                MainThreadDispatcher.Configure(defaults);
                TextureDecodeScheduler.Shared.Configure(schedulerDefaults);
                client.Dispose();
                for (var i = 0; i < burst; i++)
                {
                    if (tasks[i] != null && tasks[i].Status == TaskStatus.RanToCompletion)
                        UnityObjects.Destroy(tasks[i].Result);
                }
            }
        }

        [UnityTest]
        public IEnumerator AudioTempFileChurn_LeavesNoTempFileBehind()
        {
            Assert.IsNotNull(MainThreadDispatcher.Instance);
            var budgets = ReliabilityBudgets.Current;
            var defaults = MainThreadDispatcher.GetSettings();
            var audioDefaults = AudioClipHandler.GetOptions();
            MainThreadDispatcher.Configure(StressSettings(defaults));
            // The Unity decode path writes every body to a temp file; bodies at or above the threshold
            // become streamed clips that keep their file until the clip is destroyed (#96).
            AudioClipHandler.Configure(new AudioClipPipelineOptions
            {
                EnableThreadedDecode = false,
                EnableStreamingForLargeClips = true,
                StreamingThresholdBytes = 32 * 1024,
                CleanupRetryDelay = TimeSpan.FromMilliseconds(10)
            });

            var small = BuildPcm16Wav(sampleRate: 8000, sampleCount: 800);
            var large = BuildPcm16Wav(sampleRate: 22050, sampleCount: 44100);
            const int rounds = 3;
            const int perRound = 12;
            var before = AudioClipHandler.GetTempFileMetrics();
            var sources = new CancellationTokenSource[rounds * perRound];
            var tasks = new Task<AudioClip>[rounds * perRound];
            try
            {
                MainThreadDispatcher.ResetDrainStats();
                for (var round = 0; round < rounds; round++)
                {
                    // Per round: 6 in-memory clips, 3 streamed clips, 3 loads cancelled while in flight.
                    for (var j = 0; j < perRound; j++)
                    {
                        var index = round * perRound + j;
                        var response = CreateResponse(j % 4 == 1 ? large : small, "audio/wav", null);
                        var source = new CancellationTokenSource();
                        sources[index] = source;
                        var name = "churn-" + index;
                        tasks[index] = Task.Run(() => response.AsAudioClipAsync(AudioClipType.WAV, name, source.Token));
                    }

                    yield return null;
                    for (var j = 3; j < perRound; j += 4)
                        sources[round * perRound + j].Cancel();

                    var roundStart = round * perRound;
                    yield return new WaitUntil(() => AllDone(tasks, roundStart, perRound));

                    for (var j = 0; j < perRound; j++)
                    {
                        var task = tasks[roundStart + j];
                        Assert.IsFalse(task.IsFaulted, "load " + (roundStart + j) + " faulted: " + task.Exception);
                        if (task.Status == TaskStatus.RanToCompletion)
                            UnityObjects.Destroy(task.Result);
                    }
                }

                var stats = MainThreadDispatcher.GetDrainStats();

                // Destroyed streamed clips release their files on the next sweep; deletion is async.
                yield return null;
                StreamedAudioTempFiles.SweepNow();
                var deadline = Time.realtimeSinceStartup + 15f;
                yield return new WaitUntil(() => Settled(before) || Time.realtimeSinceStartup > deadline);

                var after = AudioClipHandler.GetTempFileMetrics();
                var created = after.Created - before.Created;
                var deleted = after.Deleted - before.Deleted;
                Assert.Greater(created, 0, "the churn went through the temp-file path");
                budgets.Record("audio_churn.temp_files_created", created, "files");
                budgets.AssertWithin("audio_churn.leaked_temp_files", created - deleted, budgets.MaxLeakedTempFiles, "files");
                budgets.AssertWithin("audio_churn.active_temp_files_delta", after.ActiveFiles - before.ActiveFiles, 0, "files");
                budgets.AssertWithin("audio_churn.max_late_start_ms", stats.MaxLateStartMs, budgets.MaxLateStartMs, "ms");
                budgets.AssertWithin("audio_churn.max_frame_work_ms", stats.MaxDrainMs, budgets.MaxFrameWorkMs, "ms");
            }
            finally
            {
                MainThreadDispatcher.Configure(defaults);
                AudioClipHandler.Configure(audioDefaults);
                for (var i = 0; i < tasks.Length; i++)
                {
                    sources[i]?.Dispose();
                    if (tasks[i] != null && tasks[i].Status == TaskStatus.RanToCompletion)
                        UnityObjects.Destroy(tasks[i].Result);
                }
            }
        }

        private static MainThreadDispatcherSettings StressSettings(MainThreadDispatcherSettings defaults)
        {
            var settings = defaults.Clone();
            settings.MaxItemsPerFrame = ItemsPerFrame;
            settings.MaxWorkTimeMs = WorkTimeMs;
            settings.AllowInlineExecutionOnMainThread = false;
            return settings;
        }

        private static bool AllDone(Task[] tasks, int start, int count)
        {
            for (var i = start; i < start + count; i++)
            {
                if (!tasks[i].IsCompleted)
                    return false;
            }

            return true;
        }

        private static bool Settled(UnityTempFileManagerMetrics before)
        {
            var now = AudioClipHandler.GetTempFileMetrics();
            return now.ActiveFiles == before.ActiveFiles &&
                   now.PendingDeleteQueueDepth == 0 &&
                   now.Created - before.Created == (now.Deleted - before.Deleted) + (now.CleanupFailures - before.CleanupFailures);
        }

        private static UHttpResponse CreateResponse(byte[] body, string contentType, UHttpRequest request)
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", contentType);
            request ??= new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/clip.wav"));
            return new UHttpResponse(HttpStatusCode.OK, headers, body, TimeSpan.Zero, request);
        }

        private static byte[] CreatePngBytes(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                    texture.SetPixel(x, y, ((x ^ y) & 8) == 0 ? Color.white : Color.black);
            }

            texture.Apply();
            var png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            return png;
        }

        private static byte[] BuildPcm16Wav(int sampleRate, int sampleCount)
        {
            var dataSize = sampleCount * 2;
            using (var stream = new MemoryStream(44 + dataSize))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataSize);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataSize);
                for (var i = 0; i < sampleCount; i++)
                    writer.Write((short)((i % 100) * 300 - 15000));
                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
