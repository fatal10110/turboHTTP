using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// A transitional utility class that bridges the execution of a <see cref="DispatchFunc"/> 
    /// and collects its result into a <see cref="UHttpResponse"/>.
    /// </summary>
    internal static class DispatchBridge
    {
        /// <summary>
        /// Executes the specified dispatch function and collects its result asynchronously.
        /// </summary>
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

            var collector = new ResponseCollectorHandler(request, context);
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

        /// <summary>
        /// Executes the transport's dispatch function and collects its result asynchronously.
        /// </summary>
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

        /// <summary>
        /// Attaches continuation logic to a dispatch task to manage the lifecycle of the passed <see cref="ResponseCollectorHandler"/>.
        /// </summary>
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

                    responseCollector.CompleteBufferedResponse();
                },
                collector,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
