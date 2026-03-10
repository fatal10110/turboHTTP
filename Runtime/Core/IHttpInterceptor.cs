namespace TurboHTTP.Core
{
    /// <summary>
    /// Wraps a downstream dispatch function.
    /// </summary>
    public interface IHttpInterceptor
    {
        /// <summary>
        /// Wraps the next link in the interceptor chain.
        /// </summary>
        /// <param name="next">The next dispatch function in the pipeline.</param>
        /// <returns>A new dispatch function that wraps the <paramref name="next"/> dispatch.</returns>
        DispatchFunc Wrap(DispatchFunc next);
    }
}
