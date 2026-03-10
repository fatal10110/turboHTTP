using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// The fundamental unit of interception and execution in the HTTP pipeline.
    /// Represents an asynchronous operation that dispatches an HTTP request.
    /// </summary>
    /// <param name="request">The HTTP request to dispatch.</param>
    /// <param name="handler">The handler which will receive synchronous lifecycle callbacks as the response is received.</param>
    /// <param name="context">The context associated with the current request.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the dispatch operation.</param>
    /// <returns>A task that represents the asynchronous dispatch operation. The task must complete normally for both successful and error responses, unless cancelled.</returns>
    public delegate Task DispatchFunc(
        UHttpRequest request,
        IHttpHandler handler,
        RequestContext context,
        CancellationToken cancellationToken);
}
