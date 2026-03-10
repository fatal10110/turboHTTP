namespace TurboHTTP.WebSocket
{
    /// <summary> Defines the state of the WebSocket connection. </summary>
    public enum WebSocketState
    {
        /// <summary> Connection has not yet started. </summary>
        None = 0,
        /// <summary> Connection is being established. </summary>
        Connecting = 1,
        /// <summary> Connection is open and ready to exchange messages. </summary>
        Open = 2,
        /// <summary> Connection is in the process of closing. </summary>
        Closing = 3,
        /// <summary> Connection is closed. </summary>
        Closed = 4
    }
}
