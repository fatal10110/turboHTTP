using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    public delegate Task DispatchFunc(
        UHttpRequest request,
        IHttpHandler handler,
        RequestContext context,
        CancellationToken cancellationToken);
}
