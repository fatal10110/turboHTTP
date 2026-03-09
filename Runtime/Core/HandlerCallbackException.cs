using System;

namespace TurboHTTP.Core
{
    internal sealed class HandlerCallbackException : Exception
    {
        public HandlerCallbackException(Exception innerException)
            : base(innerException?.Message ?? "Handler callback failed.", innerException)
        {
        }
    }
}
