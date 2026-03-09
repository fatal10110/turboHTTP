using System;
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
    }
}
