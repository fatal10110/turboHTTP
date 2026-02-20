using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Intercepts request and response execution around the middleware + transport pipeline.
    /// </summary>
    public interface IHttpInterceptor
    {
        ValueTask<InterceptorRequestResult> OnRequestAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken);

        ValueTask<InterceptorResponseResult> OnResponseAsync(
            UHttpRequest request,
            UHttpResponse response,
            RequestContext context,
            CancellationToken cancellationToken);
    }

    public enum InterceptorRequestAction
    {
        Continue,
        ShortCircuit,
        Fail
    }

    public readonly struct InterceptorRequestResult
    {
        public InterceptorRequestAction Action { get; }
        public UHttpRequest Request { get; }
        public UHttpResponse Response { get; }
        public Exception Exception { get; }

        private InterceptorRequestResult(
            InterceptorRequestAction action,
            UHttpRequest request,
            UHttpResponse response,
            Exception exception)
        {
            Action = action;
            Request = request;
            Response = response;
            Exception = exception;
        }

        public static InterceptorRequestResult Continue(UHttpRequest request = null)
        {
            return new InterceptorRequestResult(InterceptorRequestAction.Continue, request, null, null);
        }

        public static InterceptorRequestResult ShortCircuit(UHttpResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            return new InterceptorRequestResult(InterceptorRequestAction.ShortCircuit, null, response, null);
        }

        public static InterceptorRequestResult Fail(Exception exception)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));
            return new InterceptorRequestResult(InterceptorRequestAction.Fail, null, null, exception);
        }
    }

    public enum InterceptorResponseAction
    {
        Continue,
        Replace,
        Fail
    }

    public readonly struct InterceptorResponseResult
    {
        public InterceptorResponseAction Action { get; }
        public UHttpResponse Response { get; }
        public Exception Exception { get; }

        private InterceptorResponseResult(
            InterceptorResponseAction action,
            UHttpResponse response,
            Exception exception)
        {
            Action = action;
            Response = response;
            Exception = exception;
        }

        public static InterceptorResponseResult Continue(UHttpResponse response = null)
        {
            if (response != null)
            {
                throw new ArgumentException(
                    "Continue(response) is ambiguous. Use Replace(response) for response replacement.",
                    nameof(response));
            }

            return new InterceptorResponseResult(InterceptorResponseAction.Continue, null, null);
        }

        public static InterceptorResponseResult Replace(UHttpResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            return new InterceptorResponseResult(InterceptorResponseAction.Replace, response, null);
        }

        public static InterceptorResponseResult Fail(Exception exception)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));
            return new InterceptorResponseResult(InterceptorResponseAction.Fail, null, exception);
        }
    }
}
