namespace TurboHTTP.Core.Internal
{
    internal static class TransportBehaviorFlags
    {
        internal const string SelfDrainsResponseBody = "transport.self_drains_response_body";
        internal const string StreamingResponseRequested = "transport.streaming_response_requested";
        internal const string RequestBodyBytesSent = "transport.request_body_bytes_sent";
    }
}
