using System;
using System.Threading;

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

        public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
        {
            InvokeNonTerminal(() => _inner.OnResponseStart(statusCode, headers, context ?? _context));
        }

        public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
        {
            if (Volatile.Read(ref _terminated) != 0)
                return;

            try
            {
                _inner.OnResponseData(chunk, context ?? _context);
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        }

        public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
        {
            InvokeTerminal(() => _inner.OnResponseEnd(trailers, context ?? _context));
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

        private void InvokeTerminal(Action callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));
            if (Volatile.Read(ref _terminated) != 0)
                return;

            try
            {
                callback();
                Interlocked.Exchange(ref _terminated, 1);
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
    }
}
