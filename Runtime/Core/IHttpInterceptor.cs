namespace TurboHTTP.Core
{
    /// <summary>
    /// Wraps a downstream dispatch function.
    /// </summary>
    public interface IHttpInterceptor
    {
        DispatchFunc Wrap(DispatchFunc next);
    }
}
