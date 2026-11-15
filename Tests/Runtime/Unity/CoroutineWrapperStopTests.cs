using System;
using System.Collections;
using System.Net;
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
    /// Unity never disposes a coroutine enumerator that it stops, so the coroutine wrappers own the
    /// request and its response outside the iterator: a stopped or suppressed coroutine cancels the
    /// request and disposes an undelivered response exactly once (#956).
    /// </summary>
    public class CoroutineWrapperStopTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

        [UnityTest]
        public IEnumerator SendCoroutine_DisposedAfterStop_CancelsTheRequest()
        {
            var requestStarted = 0;
            var requestToken = default(CancellationToken);
            var pending = new TaskCompletionSource<UHttpResponse>();
            var transport = new MockTransport((req, ctx, ct) =>
            {
                requestToken = ct;
                ct.Register(() => pending.TrySetCanceled());
                Volatile.Write(ref requestStarted, 1);
                return pending.Task;
            });

            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });

            var callbacks = 0;
            var routine = client
                .Get("https://example.test/stopped")
                .SendCoroutine(
                    onSuccess: response =>
                    {
                        callbacks++;
                        response.Dispose();
                    },
                    onError: _ => callbacks++);

            Assert.IsTrue(routine.MoveNext(), "the first step sends the request and keeps running");
            yield return WaitFor(() => Volatile.Read(ref requestStarted) != 0);
            Assert.IsFalse(requestToken.IsCancellationRequested);

            // StopCoroutine leaves the enumerator alone; the caller disposes it.
            var disposable = routine as IDisposable;
            Assert.IsNotNull(disposable, "the returned enumerator is disposable");
            disposable.Dispose();

            yield return WaitFor(() => requestToken.IsCancellationRequested);
            Assert.IsTrue(requestToken.IsCancellationRequested, "disposing the coroutine cancels the request");
            Assert.IsFalse(routine.MoveNext());
            Assert.AreEqual(0, callbacks);
        }

        [UnityTest]
        public IEnumerator StoppedCoroutine_OwnerDestroyedAfterCompletion_DisposesResponseOnce()
        {
            var owner = new GameObject("turbohttp-stop-owner");
            var harness = new Harness(callbackOwner: owner);

            Assert.IsTrue(harness.Operation.MoveNext());
            harness.Complete();
            // The coroutine is stopped here: nothing drives it any more.
            Assert.AreEqual(0, harness.Disposals);

            UnityEngine.Object.DestroyImmediate(owner);
            LifecycleCancellation.Poll();

            Assert.IsTrue(harness.RequestToken.IsCancellationRequested);
            Assert.IsTrue(WaitUntil(() => harness.Disposals > 0), "the late response is disposed");
            harness.Operation.Dispose();
            Assert.IsFalse(harness.Operation.MoveNext());
            Assert.AreEqual(1, harness.Disposals);
            Assert.AreEqual(0, harness.Callbacks);
            yield return null;
        }

        [UnityTest]
        public IEnumerator StoppedCoroutine_OwnerDestroyedInFlight_CancelsRequestAndDisposesLateResponse()
        {
            var owner = new GameObject("turbohttp-stop-owner-inflight");
            var harness = new Harness(callbackOwner: owner);

            Assert.IsTrue(harness.Operation.MoveNext());
            UnityEngine.Object.DestroyImmediate(owner);
            LifecycleCancellation.Poll();
            Assert.IsTrue(harness.RequestToken.IsCancellationRequested, "a destroyed owner cancels the request");

            harness.Complete();
            Assert.IsTrue(WaitUntil(() => harness.Disposals > 0), "a response that completes anyway is disposed");
            Assert.IsFalse(harness.Operation.MoveNext());
            Assert.AreEqual(1, harness.Disposals);
            Assert.AreEqual(0, harness.Callbacks);
            yield return null;
        }

        [Test]
        public void TokenCanceledAfterCompletion_SuppressedResponseIsDisposedOnce()
        {
            using var cts = new CancellationTokenSource();
            var harness = new Harness(cancellationToken: cts.Token);

            Assert.IsTrue(harness.Operation.MoveNext());
            harness.Complete();
            cts.Cancel();

            Assert.IsTrue(WaitUntil(() => harness.Disposals > 0));
            Assert.IsFalse(harness.Operation.MoveNext());
            Assert.AreEqual(1, harness.Disposals);
            Assert.AreEqual(0, harness.Callbacks);
        }

        [Test]
        public void TokenCanceledBeforeTheCoroutineResumes_SuppressedResponseIsDisposedOnce()
        {
            using var cts = new CancellationTokenSource();
            var harness = new Harness(cancellationToken: cts.Token);

            Assert.IsTrue(harness.Operation.MoveNext());
            cts.Cancel();
            harness.Complete();

            Assert.IsFalse(harness.Operation.MoveNext());
            Assert.IsTrue(WaitUntil(() => harness.Disposals > 0));
            Assert.AreEqual(1, harness.Disposals);
            Assert.AreEqual(0, harness.Callbacks);
        }

        [Test]
        public void NullOnSuccess_ResponseIsDisposed()
        {
            var harness = new Harness(withOnSuccess: false);

            Assert.IsTrue(harness.Operation.MoveNext());
            harness.Complete();

            Assert.IsFalse(harness.Operation.MoveNext());
            Assert.AreEqual(1, harness.Disposals);
        }

        [Test]
        public void DeliveredResponse_IsOwnedByTheCallback()
        {
            var harness = new Harness();

            Assert.IsTrue(harness.Operation.MoveNext());
            harness.Complete();

            Assert.IsFalse(harness.Operation.MoveNext());
            Assert.AreEqual(1, harness.Callbacks);
            harness.Operation.Dispose();
            Assert.AreEqual(0, harness.Disposals, "the coroutine never disposes a delivered response");

            harness.Delivered.Dispose();
            Assert.AreEqual(1, harness.Disposals);
        }

        [Test]
        public void DisposedInFlight_CancelsRequestAndDisposesLateResponseOnce()
        {
            var harness = new Harness();

            Assert.IsTrue(harness.Operation.MoveNext());
            harness.Operation.Dispose();
            Assert.IsTrue(harness.RequestToken.IsCancellationRequested);

            harness.Complete();
            Assert.IsTrue(WaitUntil(() => harness.Disposals > 0));
            harness.Operation.Dispose();
            Assert.IsFalse(harness.Operation.MoveNext());
            Assert.AreEqual(1, harness.Disposals);
            Assert.AreEqual(0, harness.Callbacks);
        }

        [Test]
        public void DisposedBeforeTheFirstStep_NeverSends()
        {
            var harness = new Harness();

            harness.Operation.Dispose();

            Assert.IsFalse(harness.Operation.MoveNext());
            Assert.AreEqual(0, harness.Starts);
        }

        private static bool WaitUntil(Func<bool> condition)
        {
            // Completions run inline (ExecuteSynchronously); the bound only covers a scheduler that
            // chooses to queue them.
            return SpinWait.SpinUntil(condition, Bound);
        }

        private static IEnumerator WaitFor(Func<bool> condition)
        {
            var deadline = Time.realtimeSinceStartup + (float)Bound.TotalSeconds;
            while (!condition() && Time.realtimeSinceStartup < deadline)
                yield return null;
        }

        private sealed class Harness
        {
            private readonly TaskCompletionSource<UHttpResponse> _pending =
                new TaskCompletionSource<UHttpResponse>();

            private int _disposals;

            public Harness(
                CancellationToken cancellationToken = default,
                UnityEngine.Object callbackOwner = null,
                bool withOnSuccess = true)
            {
                Action<UHttpResponse> onSuccess = null;
                if (withOnSuccess)
                {
                    onSuccess = response =>
                    {
                        Callbacks++;
                        Delivered = response;
                    };
                }

                Operation = new CoroutineOperation<UHttpResponse>(
                    token =>
                    {
                        Starts++;
                        RequestToken = token;
                        return _pending.Task;
                    },
                    response => response.Dispose(),
                    onSuccess,
                    _ => Callbacks++,
                    cancellationToken,
                    callbackOwner,
                    cancelOnOwnerInactive: false);
            }

            public CoroutineOperation<UHttpResponse> Operation { get; }
            public CancellationToken RequestToken { get; private set; }
            public UHttpResponse Delivered { get; private set; }
            public int Starts { get; private set; }
            public int Callbacks { get; private set; }
            public int Disposals => Volatile.Read(ref _disposals);

            public void Complete()
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/coroutine"));
                var response = new UHttpResponse(
                    HttpStatusCode.OK,
                    new HttpHeaders(),
                    new byte[] { 1, 2, 3 },
                    TimeSpan.Zero,
                    request);
                response.AttachRequestRelease(() => Interlocked.Increment(ref _disposals));
                Assert.IsTrue(_pending.TrySetResult(response));
            }
        }
    }
}
