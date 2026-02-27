using System;

namespace TurboHTTP.Core
{
    public static class BackgroundRequestBuilderExtensions
    {
        /// <summary>
        /// Sets a deterministic key used by background replay/deferred work systems.
        /// Requests with the same key are treated as the same replay unit.
        /// </summary>
        public static UHttpRequest WithBackgroundReplayKey(
            this UHttpRequest request,
            string dedupeKey)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(dedupeKey))
                throw new ArgumentException("Replay key cannot be null or empty.", nameof(dedupeKey));

            return request.WithMetadata(RequestMetadataKeys.BackgroundReplayDedupeKey, dedupeKey);
        }
    }
}
