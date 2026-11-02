using System;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Pipeline
{
    /// <summary>
    /// Pins <see cref="HandlerCallbackSafetyWrapper"/>'s response-start path after its synchronous
    /// GetResult fast path was removed (#181): a completed callback still completes synchronously and
    /// terminates the handler, a pending one terminates when it finishes, and a failing one aborts the
    /// body and reports one terminal error.
    /// </summary>
    [TestFixture]
    public class HandlerCallbackSafetyWrapperTests
    {
        [Test]
        public void OnResponseStartAsync_CompletedCallback_CompletesSynchronously_AndTerminates()
        {
            AssertAsync.Run(async () =>
            {
                var inner = new ScriptedHandler(() => default);
                var (wrapper, body) = Create(inner);

                var pending = wrapper.OnResponseStartAsync(200, new HttpHeaders(), body, null);

                Assert.IsTrue(pending.IsCompletedSuccessfully);
                await pending;
                wrapper.OnResponseError(NewError(), null);
                Assert.AreEqual(1, inner.ResponseStartCount);
                Assert.AreEqual(0, inner.ErrorCount, "No callback is delivered after a successful response start.");
                Assert.AreEqual(0, body.AbortCount);
            });
        }

        [Test]
        public void OnResponseStartAsync_PendingCallback_TerminatesWhenItCompletes()
        {
            AssertAsync.Run(async () =>
            {
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var inner = new ScriptedHandler(() => new ValueTask(gate.Task));
                var (wrapper, body) = Create(inner);

                var pending = wrapper.OnResponseStartAsync(200, new HttpHeaders(), body, null);
                Assert.IsFalse(pending.IsCompleted);

                gate.SetResult(true);
                await pending;
                wrapper.OnResponseError(NewError(), null);

                Assert.AreEqual(0, inner.ErrorCount);
                Assert.AreEqual(0, body.AbortCount);
            });
        }

        [Test]
        public void OnResponseStartAsync_CompletedFaultedCallback_AbortsBody_AndReportsOneError()
        {
            AssertAsync.Run(async () =>
            {
                var inner = new ScriptedHandler(
                    () => new ValueTask(Task.FromException(new InvalidOperationException("handler failed"))));
                var (wrapper, body) = Create(inner);

                await wrapper.OnResponseStartAsync(200, new HttpHeaders(), body, null);
                wrapper.OnResponseError(NewError(), null);

                Assert.AreEqual(1, body.AbortCount);
                Assert.AreEqual(1, inner.ErrorCount, "The failure is reported once; later errors are dropped.");
            });
        }

        [Test]
        public void OnResponseStartAsync_ThrowingCallback_AbortsBody_AndReportsOneError()
        {
            AssertAsync.Run(async () =>
            {
                var inner = new ScriptedHandler(() => throw new InvalidOperationException("handler threw"));
                var (wrapper, body) = Create(inner);

                var pending = wrapper.OnResponseStartAsync(200, new HttpHeaders(), body, null);

                Assert.IsTrue(pending.IsCompletedSuccessfully);
                await pending;
                Assert.AreEqual(1, body.AbortCount);
                Assert.AreEqual(1, inner.ErrorCount);
            });
        }

        private static (IHttpHandler wrapper, MockResponseBodySource body) Create(IHttpHandler inner)
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/wrapper"));
            var wrapper = HandlerCallbackSafetyWrapper.Wrap(inner, new RequestContext(request));
            return (wrapper, new MockResponseBodySource(Encoding.UTF8.GetBytes("body"), length: 4));
        }

        private static UHttpException NewError() =>
            new UHttpException(new UHttpError(UHttpErrorType.Unknown, "late error"));

        private sealed class ScriptedHandler : IHttpHandler
        {
            private readonly Func<ValueTask> _onResponseStart;

            public ScriptedHandler(Func<ValueTask> onResponseStart)
            {
                _onResponseStart = onResponseStart;
            }

            public int ResponseStartCount { get; private set; }

            public int ErrorCount { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                ResponseStartCount++;
                return _onResponseStart();
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                ErrorCount++;
            }
        }
    }
}
