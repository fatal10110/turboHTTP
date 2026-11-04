using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// A Unity object created on the main thread for a caller whose token is cancelled meanwhile is
    /// destroyed, not leaked (#94). Cancellation is triggered from inside the creating work item, so
    /// it always lands while the object is being created.
    /// </summary>
    public class UnityObjectLeakOnCancelTests
    {
        [UnityTest]
        public IEnumerator CancelDuringTextureCreation_DestroysTheTexture()
        {
            Assert.IsNotNull(MainThreadDispatcher.Instance);
            var name = NewName();
            var cts = new CancellationTokenSource();
            Texture2D created = null;

            var task = Task.Run(() => MainThreadDispatcher.ExecuteAsync(
                () =>
                {
                    created = new Texture2D(2, 2) { name = name };
                    cts.Cancel();
                    return created;
                },
                UnityObjects.Destroy,
                cts.Token));

            yield return new WaitUntil(() => task.IsCompleted);
            yield return null;

            Assert.IsTrue(task.IsCanceled);
            Assert.IsFalse(ReferenceEquals(created, null), "the work item ran");
            Assert.IsTrue(created == null, "the abandoned texture is destroyed");
            Assert.AreEqual(0, CountLive<Texture2D>(name));
            cts.Dispose();
        }

        [UnityTest]
        public IEnumerator CancelDuringAudioClipCreation_DestroysTheClip()
        {
            Assert.IsNotNull(MainThreadDispatcher.Instance);
            var name = NewName();
            var cts = new CancellationTokenSource();
            AudioClip created = null;

            var task = Task.Run(() => MainThreadDispatcher.ExecuteAsync(
                () =>
                {
                    created = AudioClip.Create(name, 64, 1, 8000, stream: false);
                    cts.Cancel();
                    return created;
                },
                UnityObjects.Destroy,
                cts.Token));

            yield return new WaitUntil(() => task.IsCompleted);
            yield return null;

            Assert.IsTrue(task.IsCanceled);
            Assert.IsTrue(created == null, "the abandoned clip is destroyed");
            Assert.AreEqual(0, CountLive<AudioClip>(name));
            cts.Dispose();
        }

        [UnityTest]
        public IEnumerator CancelDuringSpriteCreation_DestroysSpriteAndTexture()
        {
            Assert.IsNotNull(MainThreadDispatcher.Instance);
            var name = NewName();
            var texture = new Texture2D(2, 2) { name = name };
            var cts = new CancellationTokenSource();
            Sprite created = null;

            var task = Task.Run(() => Texture2DHandler.CreateSpriteOwningTextureAsync(
                texture,
                t =>
                {
                    created = Sprite.Create(t, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));
                    created.name = name;
                    cts.Cancel();
                    return created;
                },
                cts.Token));

            yield return new WaitUntil(() => task.IsCompleted);
            yield return null;

            Assert.IsTrue(task.IsCanceled);
            Assert.IsTrue(created == null, "the abandoned sprite is destroyed");
            Assert.IsTrue(texture == null, "the texture nobody received is destroyed");
            Assert.AreEqual(0, CountLive<Sprite>(name));
            Assert.AreEqual(0, CountLive<Texture2D>(name));
            cts.Dispose();
        }

        [UnityTest]
        public IEnumerator CancelBeforeSpriteCreation_DestroysTheTexture()
        {
            Assert.IsNotNull(MainThreadDispatcher.Instance);
            var name = NewName();
            var texture = new Texture2D(2, 2) { name = name };
            var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = Task.Run(() => Texture2DHandler.CreateSpriteOwningTextureAsync(
                texture,
                t => Sprite.Create(t, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f)),
                cts.Token));

            yield return new WaitUntil(() => task.IsCompleted);
            yield return null;

            Assert.IsTrue(task.IsCanceled);
            Assert.IsTrue(texture == null);
            Assert.AreEqual(0, CountLive<Texture2D>(name));
            cts.Dispose();
        }

        [UnityTest]
        public IEnumerator PublicExecuteAsync_CancelledResult_IsNotDestroyed()
        {
            // The public API cannot know whether the caller's func created or only looked up the
            // object (for example Camera.main), so it never destroys it.
            Assert.IsNotNull(MainThreadDispatcher.Instance);
            var existing = new Texture2D(2, 2) { name = NewName() };
            var cts = new CancellationTokenSource();

            var task = Task.Run(() => MainThreadDispatcher.ExecuteAsync(
                () =>
                {
                    cts.Cancel();
                    return existing;
                },
                cts.Token));

            yield return new WaitUntil(() => task.IsCompleted);
            yield return null;

            Assert.IsTrue(task.IsCanceled);
            Assert.IsTrue(existing != null);
            UnityEngine.Object.Destroy(existing);
            cts.Dispose();
        }

        private static string NewName()
        {
            return "turbohttp-leak-" + Guid.NewGuid().ToString("N");
        }

        private static int CountLive<T>(string name) where T : UnityEngine.Object
        {
            var count = 0;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<T>())
            {
                if (candidate != null && candidate.name == name)
                    count++;
            }

            return count;
        }
    }
}
