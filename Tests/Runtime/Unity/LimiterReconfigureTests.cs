using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// Reconfiguring the decode or temp-file I/O concurrency while loads hold or wait on the limiter
    /// must not dispose that limiter, and every operation releases the instance it waited on (#1006).
    /// </summary>
    public class LimiterReconfigureTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

        private AudioClipPipelineOptions _originalAudio;
        private UnityTempFileManagerOptions _originalTemp;

        [SetUp]
        public void SetUp()
        {
            _originalAudio = AudioClipHandler.GetOptions();
            _originalTemp = UnityTempFileManager.Shared.GetOptions();
        }

        [TearDown]
        public void TearDown()
        {
            AudioClipHandler.Configure(_originalAudio);
            UnityTempFileManager.Shared.Configure(_originalTemp);
        }

        [Test]
        public void DecodeLimiter_ConfigureWhileHeldAndAwaited_HolderAndWaiterStillComplete()
        {
            ConfigureDecodes(1);
            var held = AudioClipHandler.GetDecodeLimiter();
            Assert.IsTrue(held.Wait(0), "take the only decode slot, as an in-flight load does");
            var waiter = held.WaitAsync();
            Assert.IsFalse(waiter.IsCompleted);

            ConfigureDecodes(2);

            Assert.AreNotSame(held, AudioClipHandler.GetDecodeLimiter());
            Assert.DoesNotThrow(() => held.Release(), "the held limiter is not disposed");
            Assert.IsTrue(SpinWait.SpinUntil(() => waiter.IsCompleted, Bound), "the waiter gets the released slot");
            Assert.AreEqual(TaskStatus.RanToCompletion, waiter.Status);
            Assert.DoesNotThrow(() => held.Release());
            Assert.AreEqual(1, held.CurrentCount);
            Assert.AreEqual(2, AudioClipHandler.GetDecodeLimiter().CurrentCount);
        }

        [UnityTest]
        public IEnumerator AsAudioClipAsync_ConfigureWhileTheDecodeSlotIsHeld_ReturnsTheClip()
        {
            Assert.IsNotNull(MainThreadDispatcher.Instance);
            ConfigureDecodes(1);
            var held = AudioClipHandler.GetDecodeLimiter();
            Assert.IsTrue(held.Wait(0));

            var response = CreateWavResponse(sampleRate: 8000, sampleCount: 800);
            var load = Task.Run(() => response.AsAudioClipAsync(AudioClipType.WAV, "limiter-reconfigure"));
            yield return null;
            Assert.IsFalse(load.IsCompleted, "the load waits for a decode slot");

            ConfigureDecodes(2);
            Assert.DoesNotThrow(() => held.Release());

            var deadline = Time.realtimeSinceStartup + (float)Bound.TotalSeconds;
            yield return new WaitUntil(() => load.IsCompleted || Time.realtimeSinceStartup > deadline);

            Assert.AreEqual(TaskStatus.RanToCompletion, load.Status, load.Exception?.ToString());
            var clip = load.Result;
            Assert.IsNotNull(clip);
            UnityEngine.Object.Destroy(clip);
            response.Dispose();
        }

        [Test]
        public void TempFileWrite_ConfigureWhileIoIsHeld_WriteReleasesTheLimiterItWaitedOn()
        {
            var manager = UnityTempFileManager.Shared;
            ConfigureIo(manager, 1);
            var held = manager.GetIoLimiter();
            Assert.IsTrue(held.Wait(0), "take the only I/O slot, as an in-flight write does");

            var path = Path.Combine(Path.GetTempPath(), "turbohttp-limiter-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                var write = manager.WriteBytesAsync(path, new byte[] { 1, 2, 3 }, CancellationToken.None);
                Assert.IsFalse(write.IsCompleted, "the write waits for the I/O slot");

                ConfigureIo(manager, 2);

                var current = manager.GetIoLimiter();
                Assert.AreNotSame(held, current);
                Assert.DoesNotThrow(() => held.Release(), "the held limiter is not disposed");
                Assert.IsTrue(SpinWait.SpinUntil(() => write.IsCompleted, Bound));
                Assert.AreEqual(TaskStatus.RanToCompletion, write.Status, write.Exception?.ToString());
                Assert.AreEqual(1, held.CurrentCount, "the write released the limiter it waited on");
                Assert.AreEqual(2, current.CurrentCount, "the new limiter got no stray release");
                Assert.IsTrue(File.Exists(path));
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static void ConfigureDecodes(int maxConcurrentDecodes)
        {
            var options = AudioClipHandler.GetOptions();
            options.MaxConcurrentDecodes = maxConcurrentDecodes;
            AudioClipHandler.Configure(options);
        }

        private static void ConfigureIo(UnityTempFileManager manager, int maxConcurrentIo)
        {
            var options = manager.GetOptions();
            options.MaxConcurrentIo = maxConcurrentIo;
            manager.Configure(options);
        }

        private static UHttpResponse CreateWavResponse(int sampleRate, int sampleCount)
        {
            var dataSize = sampleCount * 2;
            var body = new byte[44 + dataSize];
            WriteAscii(body, 0, "RIFF");
            WriteInt32(body, 4, 36 + dataSize);
            WriteAscii(body, 8, "WAVEfmt ");
            WriteInt32(body, 16, 16);
            WriteInt16(body, 20, 1);
            WriteInt16(body, 22, 1);
            WriteInt32(body, 24, sampleRate);
            WriteInt32(body, 28, sampleRate * 2);
            WriteInt16(body, 32, 2);
            WriteInt16(body, 34, 16);
            WriteAscii(body, 36, "data");
            WriteInt32(body, 40, dataSize);
            for (var i = 0; i < sampleCount; i++)
                WriteInt16(body, 44 + i * 2, (short)((i % 100) * 300 - 15000));

            var headers = new HttpHeaders();
            headers.Set("Content-Type", "audio/wav");
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/limiter.wav"));
            return new UHttpResponse(HttpStatusCode.OK, headers, body, TimeSpan.Zero, request);
        }

        private static void WriteAscii(byte[] buffer, int offset, string text)
        {
            for (var i = 0; i < text.Length; i++)
                buffer[offset + i] = (byte)text[i];
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt16(byte[] buffer, int offset, short value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }
    }
}
