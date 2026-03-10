using System;
using System.Collections.Generic;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Represents a composed chain of HTTP interceptors terminating at an HTTP transport.
    /// </summary>
    public sealed class InterceptorPipeline
    {
        private readonly DispatchFunc _pipeline;

        /// <summary>
        /// Initializes a new instance of the <see cref="InterceptorPipeline"/> class, composing
        /// the provided interceptors around the terminal transport dispatch function.
        /// </summary>
        /// <param name="interceptors">The list of interceptors to compose.</param>
        /// <param name="transport">The terminal transport used to send the request.</param>
        public InterceptorPipeline(IReadOnlyList<IHttpInterceptor> interceptors, IHttpTransport transport)
        {
            if (interceptors == null)
                throw new ArgumentNullException(nameof(interceptors));
            if (transport == null)
                throw new ArgumentNullException(nameof(transport));

            DispatchFunc terminal = transport.DispatchAsync;
            for (int i = interceptors.Count - 1; i >= 0; i--)
            {
                var interceptor = interceptors[i];
                if (interceptor == null)
                    throw new ArgumentException("Interceptor list cannot contain null elements.", nameof(interceptors));

                var next = terminal;
                terminal = interceptor.Wrap(next);
            }

            _pipeline = terminal;
        }

        /// <summary>
        /// Gets the composed dispatch function representing the entire pipeline.
        /// </summary>
        public DispatchFunc Pipeline => _pipeline;
    }
}
