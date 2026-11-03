using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// A streamed AudioClip keeps its temp file while it lives and the file is removed once the clip
    /// is destroyed; an in-memory clip releases its file as soon as it is loaded (#96).
    /// </summary>
    public class StreamedAudioTempFileTests
    {
        private AudioClipPipelineOptions _original;

        [SetUp]
        public void SetUp()
        {
            _original = AudioClipHandler.GetOptions();
        }

        [TearDown]
        public void TearDown()
        {
            AudioClipHandler.Configure(_original);
        }

        [UnityTest]
        public IEnumerator StreamedClip_KeepsItsTempFileUntilDestroyed()
        {
            Assert.IsNotNull(MainThreadDispatcher.Instance);
            AudioClipHandler.Configure(new AudioClipPipelineOptions
            {
                EnableThreadedDecode = false,
                EnableStreamingForLargeClips = true,
                StreamingThresholdBytes = 1
            });

            // Two seconds of PCM: larger than one streaming decode buffer.
            var response = CreateResponse(BuildPcm16Wav(sampleRate: 22050, sampleCount: 44100));
            var task = Task.Run(() => response.AsAudioClipAsync(AudioClipType.WAV, "streamed-" + Guid.NewGuid().ToString("N")));
            yield return new WaitUntil(() => task.IsCompleted);

            Assert.AreEqual(TaskStatus.RanToCompletion, task.Status, task.Exception?.ToString());
            var clip = task.Result;
            Assert.IsTrue(StreamedAudioTempFiles.TryGetTrackedPath(clip, out var path), "the clip owns its file");
            Assert.AreEqual(44100, clip.samples);

            for (var i = 0; i < 3; i++)
                yield return null;
            StreamedAudioTempFiles.SweepNow();
            Assert.IsTrue(File.Exists(path), "the file outlives loading while the clip is alive");

            UnityEngine.Object.Destroy(clip);
            yield return null;
            StreamedAudioTempFiles.SweepNow();
            Assert.IsFalse(StreamedAudioTempFiles.TryGetTrackedPath(clip, out _));

            var deadline = Time.realtimeSinceStartup + 10f;
            yield return new WaitUntil(() => !File.Exists(path) || Time.realtimeSinceStartup > deadline);
            Assert.IsFalse(File.Exists(path), "the file is deleted after the clip is destroyed");
        }

        [UnityTest]
        public IEnumerator StreamedClip_PlaysToTheEnd_WhileItsFileIsKept()
        {
            Assert.IsNotNull(MainThreadDispatcher.Instance);
            if (AudioSettings.outputSampleRate <= 0)
                TestRequirements.IgnoreEnvironment("no audio output in this player");

            AudioClipHandler.Configure(new AudioClipPipelineOptions
            {
                EnableThreadedDecode = false,
                EnableStreamingForLargeClips = true,
                StreamingThresholdBytes = 1
            });

            var response = CreateResponse(BuildPcm16Wav(sampleRate: 22050, sampleCount: 44100));
            var task = Task.Run(() => response.AsAudioClipAsync(AudioClipType.WAV, "streamed-play"));
            yield return new WaitUntil(() => task.IsCompleted);
            Assert.AreEqual(TaskStatus.RanToCompletion, task.Status, task.Exception?.ToString());
            var clip = task.Result;
            Assert.IsTrue(StreamedAudioTempFiles.TryGetTrackedPath(clip, out var path));

            var host = new GameObject("streamed-audio-test");
            var source = host.AddComponent<AudioSource>();
            source.volume = 0f;
            source.clip = clip;
            source.Play();

            var maxPosition = 0;
            var deadline = Time.realtimeSinceStartup + 8f;
            yield return new WaitUntil(() =>
            {
                if (source.isPlaying)
                    maxPosition = Math.Max(maxPosition, source.timeSamples);
                return !source.isPlaying || Time.realtimeSinceStartup > deadline;
            });

            var fileKept = File.Exists(path);
            UnityEngine.Object.Destroy(host);
            UnityEngine.Object.Destroy(clip);

            if (maxPosition == 0)
                TestRequirements.IgnoreEnvironment("the audio device did not advance");
            Assert.That(maxPosition, Is.GreaterThanOrEqualTo(clip.samples * 9 / 10), "the clip played to the end");
            Assert.IsTrue(fileKept, "the file was kept while the clip played");
        }

        [UnityTest]
        public IEnumerator InMemoryClip_ReleasesItsTempFileAfterLoading()
        {
            Assert.IsNotNull(MainThreadDispatcher.Instance);
            AudioClipHandler.Configure(new AudioClipPipelineOptions
            {
                EnableThreadedDecode = false,
                EnableStreamingForLargeClips = false
            });

            var activeBefore = AudioClipHandler.GetTempFileMetrics().ActiveFiles;
            var response = CreateResponse(BuildPcm16Wav(sampleRate: 8000, sampleCount: 800));
            var task = Task.Run(() => response.AsAudioClipAsync(AudioClipType.WAV, "in-memory"));
            yield return new WaitUntil(() => task.IsCompleted);

            Assert.AreEqual(TaskStatus.RanToCompletion, task.Status, task.Exception?.ToString());
            var clip = task.Result;
            Assert.IsFalse(StreamedAudioTempFiles.TryGetTrackedPath(clip, out _));
            Assert.AreEqual(activeBefore, AudioClipHandler.GetTempFileMetrics().ActiveFiles);
            UnityEngine.Object.Destroy(clip);
        }

        private static UHttpResponse CreateResponse(byte[] body)
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", "audio/wav");
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/clip.wav"));
            return new UHttpResponse(HttpStatusCode.OK, headers, body, TimeSpan.Zero, request);
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
