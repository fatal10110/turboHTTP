using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Pipeline
{
    /// <summary>
    /// #320: on IL2CPP players a caller's cancellation reached the bridges wrapped in a UHttpException
    /// ("A task was canceled." around a TaskCanceledException, or a nested OperationCanceledException).
    /// The bridges surface it as OperationCanceledException with the caller's token whenever that token
    /// fired, and keep the error when it did not (a transport deadline stays Timeout).
    /// </summary>
    [TestFixture]
    public class DispatchBridgeCancellationClassificationTests
    {
        public enum Shape
        {
            TaskCanceledInUHttp,
            NestedCancelInAggregateInUHttp,
            CancelInUHttpInUHttp,
        }

        private static UHttpException WrappedCancellation(Shape shape)
        {
            switch (shape)
            {
                case Shape.TaskCanceledInUHttp:
                    return new UHttpException(new UHttpError(
                        UHttpErrorType.Unknown, "A task was canceled.", new TaskCanceledException()));
                case Shape.NestedCancelInAggregateInUHttp:
                    return new UHttpException(new UHttpError(
                        UHttpErrorType.NetworkError,
                        "The operation was canceled.",
                        new AggregateException(new OperationCanceledException())));
                default:
                    return new UHttpException(new UHttpError(
                        UHttpErrorType.Unknown,
                        "The operation was canceled.",
                        new UHttpException(new UHttpError(
                            UHttpErrorType.NetworkError, "inner", new OperationCanceledException()))));
            }
        }

        private static UHttpRequest NewRequest() =>
            new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/cancel"));

        [Test]
        public void Buffered_DispatchFaultsWithWrappedCancel_AfterCallerCancel_ThrowsOperationCanceled(
            [Values] Shape shape)
        {
            AssertAsync.Run(async () =>
            {
                var request = NewRequest();
                using var cts = new CancellationTokenSource();
                var wrapped = WrappedCancellation(shape);

                var responseTask = TransportDispatchHelper.CollectResponseAsync(
                    async (req, handler, ctx, ct) =>
                    {
                        handler.OnRequestStart(req, ctx);
                        await Task.Yield();
                        cts.Cancel();
                        throw wrapped;
                    },
                    request,
                    new RequestContext(request),
                    cts.Token);

                var ex = AssertAsync.ThrowsAsync<OperationCanceledException>(async () => await responseTask);
                Assert.AreEqual(cts.Token, ex.CancellationToken);
                Assert.AreSame(wrapped, ex.InnerException);
            });
        }

        [Test]
        public void Buffered_ReportedWrappedCancel_AfterCallerCancel_ThrowsOperationCanceled([Values] Shape shape)
        {
            AssertAsync.Run(async () =>
            {
                var request = NewRequest();
                using var cts = new CancellationTokenSource();

                var responseTask = TransportDispatchHelper.CollectResponseAsync(
                    async (req, handler, ctx, ct) =>
                    {
                        handler.OnRequestStart(req, ctx);
                        await Task.Yield();
                        cts.Cancel();
                        handler.OnResponseError(WrappedCancellation(shape), ctx);
                    },
                    request,
                    new RequestContext(request),
                    cts.Token);

                var ex = AssertAsync.ThrowsAsync<OperationCanceledException>(async () => await responseTask);
                Assert.AreEqual(cts.Token, ex.CancellationToken);
            });
        }

        [Test]
        public void Buffered_WrappedCancelWithoutCallerCancel_KeepsTimeout()
        {
            AssertAsync.Run(async () =>
            {
                var request = NewRequest();
                using var cts = new CancellationTokenSource();
                var deadline = new UHttpException(new UHttpError(
                    UHttpErrorType.Timeout, "Request timed out after 1s", new TaskCanceledException()));

                var responseTask = TransportDispatchHelper.CollectResponseAsync(
                    async (req, handler, ctx, ct) =>
                    {
                        handler.OnRequestStart(req, ctx);
                        await Task.Yield();
                        throw deadline;
                    },
                    request,
                    new RequestContext(request),
                    cts.Token);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreSame(deadline, ex);
                Assert.AreEqual(UHttpErrorType.Timeout, ex.HttpError.Type);
            });
        }

        [Test]
        public void Buffered_CancellationSubtype_IsPreserved()
        {
            AssertAsync.Run(async () =>
            {
                var request = NewRequest();
                using var cts = new CancellationTokenSource();

                var responseTask = TransportDispatchHelper.CollectResponseAsync(
                    async (req, handler, ctx, ct) =>
                    {
                        handler.OnRequestStart(req, ctx);
                        await Task.Yield();
                        throw new QueuedCancellation();
                    },
                    request,
                    new RequestContext(request),
                    cts.Token);

                AssertAsync.ThrowsAsync<QueuedCancellation>(async () => await responseTask);
            });
        }

        [Test]
        public void Streaming_DispatchFaultsWithWrappedCancel_AfterCallerCancel_ThrowsOperationCanceled(
            [Values] Shape shape)
        {
            AssertAsync.Run(async () =>
            {
                var request = NewRequest();
                using var cts = new CancellationTokenSource();
                var wrapped = WrappedCancellation(shape);

                var responseTask = StreamingDispatchBridge.CollectResponseAsync(
                    async (req, handler, ctx, ct) =>
                    {
                        handler.OnRequestStart(req, ctx);
                        await Task.Yield();
                        cts.Cancel();
                        throw wrapped;
                    },
                    request,
                    new RequestContext(request),
                    cts.Token);

                var ex = AssertAsync.ThrowsAsync<OperationCanceledException>(async () => await responseTask);
                Assert.AreEqual(cts.Token, ex.CancellationToken);
                Assert.AreSame(wrapped, ex.InnerException);
            });
        }

        [Test]
        public void Streaming_ReportedWrappedCancel_AfterCallerCancel_ThrowsOperationCanceled([Values] Shape shape)
        {
            AssertAsync.Run(async () =>
            {
                var request = NewRequest();
                using var cts = new CancellationTokenSource();

                var responseTask = StreamingDispatchBridge.CollectResponseAsync(
                    async (req, handler, ctx, ct) =>
                    {
                        handler.OnRequestStart(req, ctx);
                        await Task.Yield();
                        cts.Cancel();
                        handler.OnResponseError(WrappedCancellation(shape), ctx);
                    },
                    request,
                    new RequestContext(request),
                    cts.Token);

                var ex = AssertAsync.ThrowsAsync<OperationCanceledException>(async () => await responseTask);
                Assert.AreEqual(cts.Token, ex.CancellationToken);
            });
        }

        [Test]
        public void Streaming_WrappedCancelWithoutCallerCancel_KeepsError()
        {
            AssertAsync.Run(async () =>
            {
                var request = NewRequest();
                using var cts = new CancellationTokenSource();
                var connectionClosed = WrappedCancellation(Shape.NestedCancelInAggregateInUHttp);

                var responseTask = StreamingDispatchBridge.CollectResponseAsync(
                    async (req, handler, ctx, ct) =>
                    {
                        handler.OnRequestStart(req, ctx);
                        await Task.Yield();
                        handler.OnResponseError(connectionClosed, ctx);
                    },
                    request,
                    new RequestContext(request),
                    cts.Token);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () => await responseTask);
                Assert.AreSame(connectionClosed, ex);
            });
        }

        private sealed class QueuedCancellation : OperationCanceledException
        {
            public QueuedCancellation()
                : base("queued")
            {
            }
        }
    }
}
