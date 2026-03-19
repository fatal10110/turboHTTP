using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    internal static class BufferedDispatchBridge
    {
        internal static Task<UHttpResponse> CollectResponseAsync(
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

            context.SetState(Internal.TransportBehaviorFlags.StreamingResponseRequested, false);

            var collector = new BufferedResponseCollectorHandler(request, context, cancellationToken);
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

        internal static void AttachCompletion(Task dispatchTask, BufferedResponseCollectorHandler collector)
        {
            if (dispatchTask == null)
                throw new ArgumentNullException(nameof(dispatchTask));
            if (collector == null)
                throw new ArgumentNullException(nameof(collector));

            _ = dispatchTask.ContinueWith(
                static (task, state) =>
                {
                    var responseCollector = (BufferedResponseCollectorHandler)state;
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
