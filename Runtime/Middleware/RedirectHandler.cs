using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    internal sealed class RedirectHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly DispatchFunc _dispatch;
        private readonly RedirectInterceptor.RedirectOptions _options;
        private readonly RequestContext _context;
        private readonly CancellationToken _cancellationToken;
        private readonly TimeSpan _totalTimeoutBudget;
        private readonly HashSet<string> _visitedTargets = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _redirectChain = new List<string>();
        private readonly TaskCompletionSource<object> _completion =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        private UHttpRequest _currentRequest;
        private int _redirectCount;
        private bool _willRedirect;
        private bool _committed;
        private int _statusCode;
        private HttpHeaders _headers;

        internal RedirectHandler(
            IHttpHandler inner,
            DispatchFunc dispatch,
            UHttpRequest initialRequest,
            RedirectInterceptor.RedirectOptions options,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            _currentRequest = initialRequest ?? throw new ArgumentNullException(nameof(initialRequest));
            _options = options;
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _cancellationToken = cancellationToken;
            _totalTimeoutBudget = initialRequest.Timeout;
            _redirectChain.Add(initialRequest.Uri.AbsoluteUri);
        }

        internal Task Completion => _completion.Task;

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            _inner.OnRequestStart(request, context);
        }

        public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
        {
            _statusCode = statusCode;
            _headers = headers;

            if (!RedirectInterceptor.TryResolveRedirectTarget(_currentRequest.Uri, statusCode, headers, out _))
            {
                _committed = true;
                _inner.OnResponseStart(statusCode, headers, context);
                return;
            }

            _willRedirect = true;
        }

        public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
        {
            if (_committed)
                _inner.OnResponseData(chunk, context);
        }

        public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
        {
            if (_committed)
            {
                _inner.OnResponseEnd(trailers, context);
                _completion.TrySetResult(null);
                return;
            }

            if (!_willRedirect)
            {
                _completion.TrySetResult(null);
                return;
            }

            _willRedirect = false;

            if (_redirectCount >= _options.MaxRedirects)
            {
                _inner.OnResponseError(
                    RedirectInterceptor.CreateRedirectError(
                        UHttpErrorType.InvalidRequest,
                        $"Redirect limit exceeded ({_options.MaxRedirects})."),
                    context);
                _completion.TrySetResult(null);
                return;
            }

            try
            {
                if (!RedirectInterceptor.TryResolveRedirectTarget(_currentRequest.Uri, _statusCode, _headers, out var targetUri))
                {
                    _inner.OnResponseEnd(trailers, context);
                    _completion.TrySetResult(null);
                    return;
                }

                if (RedirectInterceptor.IsHttpsToHttpDowngrade(_currentRequest.Uri, targetUri) &&
                    !_options.AllowHttpsToHttpDowngrade)
                {
                    _inner.OnResponseError(
                        RedirectInterceptor.CreateRedirectError(
                            UHttpErrorType.InvalidRequest,
                            $"Blocked insecure redirect downgrade from '{_currentRequest.Uri}' to '{targetUri}'."), context);
                    _completion.TrySetResult(null);
                    return;
                }

                var loopKey = RedirectInterceptor.BuildLoopKey(targetUri);
                if (!_visitedTargets.Add(loopKey))
                {
                    _inner.OnResponseError(
                        RedirectInterceptor.CreateRedirectError(
                            UHttpErrorType.InvalidRequest,
                            $"Redirect loop detected for target '{targetUri}'."), context);
                    _completion.TrySetResult(null);
                    return;
                }

                var crossOrigin = RedirectInterceptor.IsCrossOrigin(_currentRequest.Uri, targetUri);
                var newRequest = RedirectInterceptor.BuildRedirectRequest(
                    _currentRequest,
                    targetUri,
                    (HttpStatusCode)_statusCode,
                    crossOrigin);

                if (_options.EnforceRedirectTotalTimeout)
                    newRequest = RedirectInterceptor.ApplyTotalRedirectTimeoutBudget(newRequest, context, _totalTimeoutBudget);

                _redirectCount++;
                _redirectChain.Add(targetUri.AbsoluteUri);
                _context.SetState("RedirectChain", _redirectChain.ToArray());
                _context.RecordEvent("Redirect", new Dictionary<string, object>
                {
                    { "from", _currentRequest.Uri.AbsoluteUri },
                    { "to", targetUri.AbsoluteUri },
                    { "status", _statusCode },
                    { "hop", _redirectCount }
                });
                _context.UpdateRequest(newRequest);
                _currentRequest = newRequest;
                _committed = false;
                _statusCode = 0;
                _headers = null;

                Task redirectTask;
                try
                {
                    redirectTask = _dispatch(newRequest, this, _context, _cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _completion.TrySetCanceled();
                    return;
                }
                catch (Exception ex)
                {
                    _completion.TrySetException(ex);
                    return;
                }

                BridgeDispatchCompletion(redirectTask);
            }
            catch (UHttpException ex)
            {
                _inner.OnResponseError(ex, context);
                _completion.TrySetResult(null);
            }
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            _inner.OnResponseError(error, context);
            _completion.TrySetResult(null);
        }

        private void BridgeDispatchCompletion(Task redirectTask)
        {
            _ = redirectTask.ContinueWith(t =>
            {
                try
                {
                    if (t.IsFaulted)
                    {
                        _completion.TrySetException(t.Exception.GetBaseException());
                        return;
                    }

                    if (t.IsCanceled)
                    {
                        _completion.TrySetCanceled();
                        return;
                    }

                    if (!_completion.Task.IsCompleted)
                    {
                        _completion.TrySetException(new InvalidOperationException(
                            "Redirect pipeline completed without delivering a terminal callback."));
                    }
                }
                catch (Exception bridgeError)
                {
                    _completion.TrySetException(bridgeError);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        }
    }
}
