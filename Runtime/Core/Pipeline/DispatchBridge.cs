using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    internal static class DispatchBridge
    {
        internal static Task<UHttpResponse> CollectResponseAsync(
            DispatchFunc dispatch,
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            if (dispatch == null)
                throw new ArgumentNullException(nameof(dispatch));

            var collector = new ResponseCollectorHandler(request, context);
            Task dispatchTask;
            try
            {
                dispatchTask = dispatch(request, collector, context, cancellationToken);
            }
            catch (Exception ex)
            {
                collector.Fail(ex);
                return collector.ResponseTask;
            }

            AttachCompletion(dispatchTask, collector);
            return collector.ResponseTask;
        }

        internal static Task<UHttpResponse> CollectResponseAsync(
            IHttpTransport transport,
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            if (transport == null)
                throw new ArgumentNullException(nameof(transport));

            return CollectResponseAsync(transport.DispatchAsync, request, context, cancellationToken);
        }

        internal static void AttachCompletion(Task dispatchTask, ResponseCollectorHandler collector)
        {
            if (dispatchTask == null)
                throw new ArgumentNullException(nameof(dispatchTask));
            if (collector == null)
                throw new ArgumentNullException(nameof(collector));

            _ = dispatchTask.ContinueWith(
                static (task, state) =>
                {
                    var responseCollector = (ResponseCollectorHandler)state;
                    if (task.IsFaulted)
                    {
                        responseCollector.Fail(task.Exception.GetBaseException());
                        return;
                    }

                    if (task.IsCanceled)
                    {
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

        internal static void DeliverResponse(
            UHttpResponse response,
            IHttpHandler handler,
            RequestContext context,
            UHttpRequest fallbackRequest)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var request = response.Request ?? context.Request ?? fallbackRequest;
            if (request != null)
                context.UpdateRequest(request);

            context.SetResponseError(response.Error);
            handler.OnResponseStart((int)response.StatusCode, response.Headers, context);

            var body = response.Body;
            if (!body.IsEmpty)
            {
                var enumerator = body.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    ReadOnlyMemory<byte> segment = enumerator.Current;
                    if (!segment.IsEmpty)
                        handler.OnResponseData(segment.Span, context);
                }
            }

            handler.OnResponseEnd(HttpHeaders.Empty, context);
        }
    }
}
