namespace TurboHTTP.Core
{
    /// <summary>Whether a request body can be sent again, for a redirect that preserves it or a retry.</summary>
    public enum RequestBodyReplayability
    {
        /// <summary>The same body can be read again (in-memory data or a seekable stream).</summary>
        Replayable,
        /// <summary>A factory recreates the body for each attempt.</summary>
        ReplayableViaFactory,
        /// <summary>The body can be sent once; a redirect or retry that needs it again fails or is not attempted.</summary>
        NonReplayable
    }
}
