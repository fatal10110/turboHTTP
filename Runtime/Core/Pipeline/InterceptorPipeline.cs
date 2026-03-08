using System;
using System.Collections.Generic;

namespace TurboHTTP.Core
{
    public sealed class InterceptorPipeline
    {
        private readonly DispatchFunc _pipeline;

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

        public DispatchFunc Pipeline => _pipeline;
    }
}
