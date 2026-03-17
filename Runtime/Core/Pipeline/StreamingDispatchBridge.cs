using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    internal static class StreamingDispatchBridge
    {
        internal static Task<UHttpStreamingResponse> CollectResponseAsync(
            DispatchFunc dispatch,
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            if (dispatch == null)
                throw new ArgumentNullException(nameof(dispatch));
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var collector = new StreamingResponseCaptureHandler(request, context);
            var safeCollector = HandlerCallbackSafetyWrapper.Wrap(collector, context);
            Task dispatchTask;
            try
            {
                dispatchTask = dispatch(request, safeCollector, context, cancellationToken);
            }
            catch (Exception ex)
            {
                collector.Fail(ex);
                return collector.ResponseTask;
            }

            AttachCompletion(dispatchTask, collector);
            return collector.ResponseTask;
        }

        internal static Task<UHttpStreamingResponse> CollectResponseAsync(
            IHttpTransport transport,
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            if (transport == null)
                throw new ArgumentNullException(nameof(transport));

            return CollectResponseAsync(transport.DispatchAsync, request, context, cancellationToken);
        }

        private static void AttachCompletion(Task dispatchTask, StreamingResponseCaptureHandler collector)
        {
            _ = dispatchTask.ContinueWith(
                static (task, state) =>
                {
                    var responseCollector = (StreamingResponseCaptureHandler)state;
                    if (responseCollector.ResponseTask.IsCompleted)
                        return;

                    if (task.IsFaulted)
                    {
                        responseCollector.Fail(task.Exception.GetBaseException());
                        return;
                    }

                    if (task.IsCanceled)
                    {
                        try
                        {
                            task.GetAwaiter().GetResult();
                        }
                        catch (TaskCanceledException)
                        {
                            responseCollector.Cancel();
                            return;
                        }
                        catch (OperationCanceledException ex) when (ex.GetType() == typeof(OperationCanceledException))
                        {
                            responseCollector.Cancel();
                            return;
                        }
                        catch (OperationCanceledException ex)
                        {
                            responseCollector.Fail(ex);
                            return;
                        }

                        responseCollector.Cancel();
                        return;
                    }

                    responseCollector.EnsureCompleted();
                },
                collector,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private sealed class StreamingResponseCaptureHandler : IHttpHandler
        {
            private readonly TaskCompletionSource<UHttpStreamingResponse> _tcs =
                new TaskCompletionSource<UHttpStreamingResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

            private UHttpRequest _request;

            internal StreamingResponseCaptureHandler(UHttpRequest request, RequestContext context)
            {
                _request = request;
                _ = context ?? throw new ArgumentNullException(nameof(context));
            }

            internal Task<UHttpStreamingResponse> ResponseTask => _tcs.Task;

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                if (request != null)
                    _request = request;
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                if (body == null)
                    throw new ArgumentNullException(nameof(body));

                try
                {
                    UHttpStreamingResponse response = null;
                    bool retainedRequest = false;
                    bool attachedRequestRelease = false;
                    try
                    {
                        response = new UHttpStreamingResponse(
                            (HttpStatusCode)statusCode,
                            headers,
                            body);

                        if (_request.IsPooled)
                        {
                            _request.RetainForResponse();
                            retainedRequest = true;
                            response.AttachRequestRelease(_request.ReleaseResponseHold);
                            attachedRequestRelease = true;
                        }

                        if (!_tcs.TrySetResult(response))
                            response.Dispose();

                        response = null;
                    }
                    finally
                    {
                        if (response != null)
                            response.Dispose();
                        if (retainedRequest && !attachedRequestRelease)
                            _request.ReleaseResponseHold();
                    }
                }
                catch
                {
                    body.Abort();
                    throw;
                }

                return default;
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                var responseError = error ?? new UHttpException(
                    new UHttpError(
                        UHttpErrorType.Unknown,
                        "IHttpHandler.OnResponseError received a null error."));
                _tcs.TrySetException(responseError);
            }

            internal void Fail(Exception ex)
            {
                if (ex is HandlerCallbackException handlerCallback && handlerCallback.InnerException != null)
                    ex = handlerCallback.InnerException;

                if (ex is OperationCanceledException operationCanceledException)
                {
                    _tcs.TrySetException(operationCanceledException);
                    return;
                }

                var dispatchError = ex as UHttpException
                    ?? new UHttpException(new UHttpError(UHttpErrorType.Unknown, ex?.Message ?? "Dispatch failed.", ex));
                _tcs.TrySetException(dispatchError);
            }

            internal void Cancel()
            {
                _tcs.TrySetCanceled();
            }

            internal void EnsureCompleted()
            {
                if (_tcs.Task.IsCompleted)
                    return;

                _tcs.TrySetException(new InvalidOperationException(
                    "Pipeline completed without delivering a response."));
            }
        }
    }
}
