using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Middleware
{
    internal sealed class RedirectHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly DispatchFunc _dispatch;
        private readonly RedirectInterceptor.RedirectOptions _options;
        private readonly RequestContext _context;
        private readonly CancellationToken _cancellationToken;
        private readonly TimeSpan _responseDiscardTimeout;
        private readonly TimeSpan _totalTimeoutBudget;
        private readonly HashSet<string> _visitedTargets = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _redirectChain = new List<string>();
        private readonly TaskCompletionSource<object> _completion =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        private UHttpRequest _currentRequest;
        private int _redirectCount;
        private bool _sawInnerRequestStart;

        internal RedirectHandler(
            IHttpHandler inner,
            DispatchFunc dispatch,
            UHttpRequest initialRequest,
            RedirectInterceptor.RedirectOptions options,
            RequestContext context,
            CancellationToken cancellationToken,
            TimeSpan responseDiscardTimeout)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            _currentRequest = initialRequest ?? throw new ArgumentNullException(nameof(initialRequest));
            _options = options;
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _cancellationToken = cancellationToken;
            if (responseDiscardTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(responseDiscardTimeout));

            _responseDiscardTimeout = responseDiscardTimeout;
            _totalTimeoutBudget = initialRequest.Timeout;
            _redirectChain.Add(initialRequest.Uri.AbsoluteUri);
        }

        internal Task Completion => _completion.Task;

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            _sawInnerRequestStart = true;
            _inner.OnRequestStart(request, context);
        }

        public async ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            try
            {
                Uri targetUri;
                try
                {
                    if (!RedirectInterceptor.TryResolveRedirectTarget(_currentRequest.Uri, statusCode, headers, out targetUri))
                    {
                        try
                        {
                            await _inner.OnResponseStartAsync(statusCode, headers, body, context).ConfigureAwait(false);
                            _completion.TrySetResult(null);
                        }
                        catch (Exception ex)
                        {
                            _completion.TrySetException(ex);
                            throw;
                        }

                        return;
                    }
                }
                catch (UHttpException ex)
                {
                    await DiscardBodyAsync(body).ConfigureAwait(false);
                    CompleteWithError(ex, context);
                    return;
                }

                if (_redirectCount >= _options.MaxRedirects)
                {
                    await DiscardBodyAsync(body).ConfigureAwait(false);
                    CompleteWithError(
                        RedirectInterceptor.CreateRedirectError(
                            UHttpErrorType.InvalidRequest,
                            $"Redirect limit exceeded ({_options.MaxRedirects})."),
                        context);
                    return;
                }

                if (RedirectInterceptor.IsHttpsToHttpDowngrade(_currentRequest.Uri, targetUri) &&
                         !_options.AllowHttpsToHttpDowngrade)
                {
                    await DiscardBodyAsync(body).ConfigureAwait(false);
                    CompleteWithError(
                        RedirectInterceptor.CreateRedirectError(
                            UHttpErrorType.InvalidRequest,
                            $"Blocked insecure redirect downgrade from '{_currentRequest.Uri}' to '{targetUri}'."),
                        context);
                    return;
                }

                var loopKey = RedirectInterceptor.BuildLoopKey(targetUri);
                if (!_visitedTargets.Add(loopKey))
                {
                    await DiscardBodyAsync(body).ConfigureAwait(false);
                    CompleteWithError(
                        RedirectInterceptor.CreateRedirectError(
                            UHttpErrorType.InvalidRequest,
                            $"Redirect loop detected for target '{targetUri}'."),
                        context);
                    return;
                }

                var crossOrigin = RedirectInterceptor.IsCrossOrigin(_currentRequest.Uri, targetUri);
                UHttpRequest newRequest;
                try
                {
                    newRequest = RedirectInterceptor.BuildRedirectRequest(
                        _currentRequest,
                        targetUri,
                        (HttpStatusCode)statusCode,
                        crossOrigin);

                    if (_options.EnforceRedirectTotalTimeout)
                        newRequest = RedirectInterceptor.ApplyTotalRedirectTimeoutBudget(newRequest, context, _totalTimeoutBudget);
                }
                catch (UHttpException ex)
                {
                    await DiscardBodyAsync(body).ConfigureAwait(false);
                    CompleteWithError(ex, context);
                    return;
                }

                await DiscardBodyAsync(body).ConfigureAwait(false);

                _redirectCount++;
                _redirectChain.Add(targetUri.AbsoluteUri);
                _context.SetState("RedirectChain", _redirectChain.ToArray());
                _context.RecordEvent("Redirect", new Dictionary<string, object>
                {
                    { "from", _currentRequest.Uri.AbsoluteUri },
                    { "to", targetUri.AbsoluteUri },
                    { "status", statusCode },
                    { "hop", _redirectCount }
                });
                _context.UpdateRequest(newRequest);
                _currentRequest = newRequest;

                try
                {
                    await _dispatch(newRequest, this, _context, _cancellationToken).ConfigureAwait(false);
                    if (!_completion.Task.IsCompleted)
                    {
                        _completion.TrySetException(new InvalidOperationException(
                            "Redirect pipeline completed without delivering a terminal callback."));
                    }
                }
                catch (OperationCanceledException)
                {
                    _completion.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    CompleteWithDispatchException(ex);
                }
            }
            catch (OperationCanceledException)
            {
                _completion.TrySetCanceled();
            }
            catch (UHttpException ex)
            {
                CompleteWithError(ex, context);
            }
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            CompleteWithError(error, context);
        }

        private void CompleteWithError(UHttpException error, RequestContext context)
        {
            try
            {
                _inner.OnResponseError(error, context);
                _completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }

        private void CompleteWithDispatchException(Exception exception)
        {
            if (_completion.Task.IsCompleted)
                return;

            if (exception is HandlerCallbackException handlerCallback && handlerCallback.InnerException != null)
                exception = handlerCallback.InnerException;

            var mapped = exception as UHttpException
                ?? new UHttpException(new UHttpError(
                    UHttpErrorType.Unknown,
                    exception?.Message ?? "Redirect dispatch failed.",
                    exception));

            if (_sawInnerRequestStart)
            {
                CompleteWithError(mapped, _context);
                return;
            }

            _completion.TrySetException(exception ?? mapped);
        }

        private async ValueTask DiscardBodyAsync(IResponseBodySource body)
        {
            await ResponseBodyDiscardHelper
                .DiscardAsync(body, _cancellationToken, _responseDiscardTimeout)
                .ConfigureAwait(false);
        }
    }
}
