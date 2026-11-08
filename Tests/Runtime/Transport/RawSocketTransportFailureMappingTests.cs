using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http2;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// #320: the dispatch-attempt failure mapping is one function instead of a ladder of filtered catch
    /// clauses (under IL2CPP an exception thrown by one clause of a filtered try was caught by the next,
    /// which lost the HTTP/2 replay marker and turned cancellations into Unknown errors), and the HTTP/2
    /// retry loop finds the replay marker inside wrappers.
    /// </summary>
    [TestFixture]
    public class RawSocketTransportFailureMappingTests
    {
        private static readonly UHttpRequest Get = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/"));

        private static Http2RetryableRequestException Refused() =>
            new Http2RetryableRequestException(
                new UHttpError(UHttpErrorType.NetworkError, "RST_STREAM: RefusedStream"),
                requestNotProcessed: true);

        private static Exception Map(Exception ex, bool callerCanceled, bool deadlineExpired, RequestContext context = null)
        {
            using var caller = new CancellationTokenSource();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
            if (callerCanceled)
                caller.Cancel();
            if (deadlineExpired)
                timeout.Cancel();

            var mapped = RawSocketTransport.MapDispatchAttemptFailure(
                ex, Get, context ?? new RequestContext(Get), timeout, caller.Token);
            if (mapped is OperationCanceledException canceled && callerCanceled)
                Assert.AreEqual(caller.Token, canceled.CancellationToken);
            return mapped;
        }

        [Test]
        public void RetryableHttp2Failure_PassesThroughUnchanged()
        {
            var refused = Refused();
            Assert.AreSame(refused, Map(refused, callerCanceled: false, deadlineExpired: false));
        }

        [Test]
        public void CallerCancellation_IsOperationCanceled_ForEveryCancellationShape()
        {
            Exception[] shapes =
            {
                new OperationCanceledException(),
                new TaskCanceledException(),
                new IOException("stream disposed by the cancel registration"),
                new SocketException((int)SocketError.OperationAborted),
                new UHttpException(new UHttpError(UHttpErrorType.NetworkError, "The operation was canceled.")),
            };

            foreach (var shape in shapes)
            {
                Assert.IsInstanceOf<OperationCanceledException>(
                    Map(shape, callerCanceled: true, deadlineExpired: false), shape.GetType().Name);
            }
        }

        [Test]
        public void DeadlineCancellation_IsTimeout_ForEveryCancellationShape()
        {
            Exception[] shapes =
            {
                new OperationCanceledException(),
                new TaskCanceledException(),
                new IOException("stream disposed by the deadline registration"),
                new SocketException((int)SocketError.OperationAborted),
                new UHttpException(new UHttpError(UHttpErrorType.NetworkError, "The operation was canceled.")),
            };

            foreach (var shape in shapes)
            {
                var mapped = Map(shape, callerCanceled: false, deadlineExpired: true);
                Assert.IsInstanceOf<UHttpException>(mapped, shape.GetType().Name);
                Assert.AreEqual(UHttpErrorType.Timeout, ((UHttpException)mapped).HttpError.Type, shape.GetType().Name);
            }
        }

        [Test]
        public void CancellationWithoutCallerOrDeadline_IsNetworkError()
        {
            // #409: an internal cancellation (connection or stream teardown) is not the caller's.
            foreach (var shape in new Exception[] { new OperationCanceledException(), new TaskCanceledException() })
            {
                var mapped = Map(shape, callerCanceled: false, deadlineExpired: false);
                Assert.IsInstanceOf<UHttpException>(mapped, shape.GetType().Name);
                Assert.AreEqual(UHttpErrorType.NetworkError, ((UHttpException)mapped).HttpError.Type, shape.GetType().Name);
                Assert.AreSame(shape, mapped.InnerException, shape.GetType().Name);
            }
        }

        [Test]
        public void UncanceledFailures_KeepTheirMapping()
        {
            AssertError(Map(new IOException("reset"), false, false), UHttpErrorType.NetworkError);
            AssertError(Map(new FormatException("bad chunk"), false, false), UHttpErrorType.NetworkError);
            AssertError(Map(new HttpFieldValidationException("bad header"), false, false), UHttpErrorType.InvalidRequest);
            AssertError(Map(new InvalidOperationException("boom"), false, false), UHttpErrorType.Unknown);

            var certificate = new UHttpException(new UHttpError(UHttpErrorType.CertificateError, "untrusted"));
            Assert.AreSame(certificate, Map(certificate, callerCanceled: true, deadlineExpired: false));
        }

        [Test]
        public void HandlerFault_ReturnsTheHandlerException()
        {
            var context = new RequestContext(Get);
            var handlerError = new InvalidOperationException("handler threw");

            var mapped = Map(new HandlerCallbackException(handlerError), false, false, context);

            Assert.AreSame(handlerError, mapped);
            Assert.IsTrue(context.Timeline.Any(e => e.Name == "RequestFailed"));
        }

        [Test]
        public void FindH2Retryable_UnwrapsUHttpAndAggregateWrappers()
        {
            var refused = Refused();
            var post = new UHttpRequest(HttpMethod.POST, new Uri("https://example.test/"), body: new byte[] { 1 });

            var wrappedOnce = new UHttpException(new UHttpError(UHttpErrorType.Unknown, refused.Message, refused));
            var wrappedTwice = new AggregateException(
                new UHttpException(new UHttpError(UHttpErrorType.Unknown, "wrapped", wrappedOnce)));

            Assert.AreSame(refused, RawSocketTransport.FindH2Retryable(refused));
            Assert.AreSame(refused, RawSocketTransport.FindH2Retryable(wrappedOnce));
            Assert.AreSame(refused, RawSocketTransport.FindH2Retryable(wrappedTwice));
            Assert.IsTrue(RawSocketTransport.ShouldRetryH2Request(
                RawSocketTransport.FindH2Retryable(wrappedOnce), post, 0, CancellationToken.None));

            var plain = new UHttpException(new UHttpError(UHttpErrorType.NetworkError, "reset"));
            var behindOther = new InvalidOperationException("other", refused);
            Assert.IsNull(RawSocketTransport.FindH2Retryable(plain));
            Assert.IsNull(RawSocketTransport.FindH2Retryable(behindOther));
            Assert.IsFalse(RawSocketTransport.ShouldRetryH2Request(null, post, 0, CancellationToken.None));
        }

        private static void AssertError(Exception mapped, UHttpErrorType expected)
        {
            Assert.IsInstanceOf<UHttpException>(mapped);
            Assert.AreEqual(expected, ((UHttpException)mapped).HttpError.Type);
        }
    }
}
