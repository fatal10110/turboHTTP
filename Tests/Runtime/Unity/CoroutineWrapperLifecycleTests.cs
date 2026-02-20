using System;
using System.Collections;
using System.Net;
using System.Text;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    public class CoroutineWrapperLifecycleTests
    {
        [UnityTest]
        public IEnumerator SendCoroutine_OwnerDestroyed_SuppressesCallbacks()
        {
            var transport = new MockTransport();
            transport.EnqueueResponse(
                HttpStatusCode.OK,
                body: Encoding.UTF8.GetBytes("ok"),
                delay: TimeSpan.FromMilliseconds(100));

            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });

            var owner = new GameObject("turbohttp-owner");
            var successCalled = false;
            var errorCalled = false;

            var routine = client
                .Get("https://example.test/owner-destroy")
                .SendCoroutine(
                    onSuccess: _ => successCalled = true,
                    onError: _ => errorCalled = true,
                    callbackOwner: owner);

            yield return null;
            UnityEngine.Object.Destroy(owner);

            while (routine.MoveNext())
                yield return routine.Current;

            Assert.IsFalse(successCalled);
            Assert.IsFalse(errorCalled);
        }

        [UnityTest]
        public IEnumerator SendCoroutine_Failure_InvokesErrorExactlyOnce()
        {
            var transport = new MockTransport(HttpStatusCode.OK);
            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });

            var builder = client.Get("https://example.test/error");
            // Intentional: force disposed-client failure path, then verify onError is still exactly-once.
            client.Dispose();

            var errorCount = 0;
            var successCalled = false;

            yield return builder.SendCoroutine(
                onSuccess: _ => successCalled = true,
                onError: _ => errorCount++);

            Assert.IsFalse(successCalled);
            Assert.AreEqual(1, errorCount);
        }

        [UnityTest]
        public IEnumerator SendCoroutine_CancelRace_SuppressesTerminalCallbacks()
        {
            var transport = new MockTransport();
            transport.EnqueueResponse(
                HttpStatusCode.InternalServerError,
                body: Encoding.UTF8.GetBytes("fail"),
                delay: TimeSpan.FromMilliseconds(80));

            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(10);

            var successCalled = false;
            var errorCalled = false;

            yield return client
                .Get("https://example.test/cancel-race")
                .SendCoroutine(
                    onSuccess: _ => successCalled = true,
                    onError: _ => errorCalled = true,
                    cancellationToken: cts.Token);

            Assert.IsFalse(successCalled);
            Assert.IsFalse(errorCalled);
        }

        [UnityTest]
        public IEnumerator LifecycleCancellation_BindOffThread_StillCancelsOnOwnerDestroy()
        {
            var owner = new GameObject("turbohttp-offthread-owner");
            LifecycleCancellationBinding binding = null;

            var bindTask = System.Threading.Tasks.Task.Run(() =>
            {
                binding = LifecycleCancellation.Bind(owner);
            });

            yield return new WaitUntil(() => bindTask.IsCompleted);
            Assert.AreEqual(System.Threading.Tasks.TaskStatus.RanToCompletion, bindTask.Status);
            Assert.IsNotNull(binding);
            Assert.IsFalse(binding.IsCancellationRequested);

            UnityEngine.Object.Destroy(owner);
            yield return new WaitUntil(() => binding.IsCancellationRequested);

            Assert.IsTrue(binding.IsCancellationRequested);
            Assert.AreEqual(LifecycleCancellationReason.OwnerDestroyed, binding.Reason);
            binding.Dispose();
        }
    }
}
