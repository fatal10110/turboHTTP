using System;

namespace TurboHTTP.Transport.Internal
{
    /// <summary>
    /// Thrown by <see cref="HttpFieldValidator"/> for a request header or trailer field that may not
    /// be sent. It is an <see cref="ArgumentException"/>, so the serializer contract is unchanged,
    /// and its own type lets the transport report it as <see cref="TurboHTTP.Core.UHttpErrorType.InvalidRequest"/>
    /// without reclassifying unrelated <see cref="ArgumentException"/>s (#243). The message names
    /// the field and a character code, never the value.
    /// </summary>
    internal sealed class HttpFieldValidationException : ArgumentException
    {
        internal HttpFieldValidationException(string message)
            : base(message)
        {
        }
    }
}
