using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Converts synchronous handler callback failures into a terminal <see cref="IHttpHandler.OnResponseError"/>
    /// notification so dispatch tasks do not fault on the callback path.
    /// </summary>
    internal sealed class HandlerCallbackSafetyWrapper : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly RequestContext _context;
        private int _terminated;

        private HandlerCallbackSafetyWrapper(IHttpHandler inner, RequestContext context)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        internal static IHttpHandler Wrap(IHttpHandler handler, RequestContext context)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            return handler is HandlerCallbackSafetyWrapper
                ? handler
                : new HandlerCallbackSafetyWrapper(handler, context);
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            InvokeNonTerminal(() => _inner.OnRequestStart(request, context ?? _context));
        }

        public ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            if (body == null)
                throw new ArgumentNullException(nameof(body));
            if (Volatile.Read(ref _terminated) != 0)
            {
                TryAbort(body);
                return default;
            }

            try
            {
                var pending = _inner.OnResponseStartAsync(statusCode, headers, body, context ?? _context);
                if (pending.IsCompletedSuccessfully)
                {
                    pending.GetAwaiter().GetResult();
                    Interlocked.Exchange(ref _terminated, 1);
                    return default;
                }

                return AwaitResponseStartAsync(pending, body);
            }
            catch (Exception ex)
            {
                TryAbort(body);
                ReportFailure(ex);
                return default;
            }
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            if (Interlocked.Exchange(ref _terminated, 1) != 0)
                return;

            _inner.OnResponseError(
                error ?? new UHttpException(
                    new UHttpError(
                        UHttpErrorType.Unknown,
                        "IHttpHandler.OnResponseError received a null error.")),
                context ?? _context);
        }

        private async ValueTask AwaitResponseStartAsync(ValueTask pending, IResponseBodySource body)
        {
            try
            {
                await pending.ConfigureAwait(false);
                Interlocked.Exchange(ref _terminated, 1);
            }
            catch (Exception ex)
            {
                TryAbort(body);
                ReportFailure(ex);
            }
        }

        private void InvokeNonTerminal(Action callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));
            if (Volatile.Read(ref _terminated) != 0)
                return;

            try
            {
                callback();
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        }

        private void ReportFailure(Exception exception)
        {
            if (Interlocked.Exchange(ref _terminated, 1) != 0)
                return;

            try
            {
                _inner.OnResponseError(ToHandlerError(exception), _context);
            }
            catch (Exception terminalException)
            {
                throw new HandlerCallbackException(terminalException);
            }
        }

        private static UHttpException ToHandlerError(Exception exception)
        {
            if (exception is UHttpException httpException)
                return httpException;

            return new UHttpException(
                new UHttpError(
                    UHttpErrorType.Unknown,
                    exception?.Message ?? "Handler callback failed.",
                    exception));
        }

        private static void TryAbort(IResponseBodySource body)
        {
            try
            {
                body?.Abort();
            }
            catch
            {
            }
        }
    }
}
