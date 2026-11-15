using System;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Raises user event handlers so a throwing handler cannot break the caller's state machine (#1116):
    /// the exception is reported to <see cref="TurboDiagnostics"/> (type only, never the message) and
    /// swallowed. A multicast delegate runs as one call, so a throwing subscriber skips the subscribers
    /// after it, as it did for <c>OnMessage</c> before.
    /// </summary>
    internal static class WebSocketCallbacks
    {
        internal static void Invoke(Action handler, string failureMessage)
        {
            if (handler == null)
                return;

            try
            {
                handler();
            }
            catch (Exception ex)
            {
                TurboDiagnostics.Log(failureMessage, ex);
            }
        }

        internal static void Invoke<T>(Action<T> handler, T arg, string failureMessage)
        {
            if (handler == null)
                return;

            try
            {
                handler(arg);
            }
            catch (Exception ex)
            {
                TurboDiagnostics.Log(failureMessage, ex);
            }
        }

        internal static void Invoke<T1, T2>(Action<T1, T2> handler, T1 arg1, T2 arg2, string failureMessage)
        {
            if (handler == null)
                return;

            try
            {
                handler(arg1, arg2);
            }
            catch (Exception ex)
            {
                TurboDiagnostics.Log(failureMessage, ex);
            }
        }
    }
}
