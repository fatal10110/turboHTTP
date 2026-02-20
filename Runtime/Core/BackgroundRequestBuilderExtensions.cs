using System;

namespace TurboHTTP.Core
{
    public static class BackgroundRequestBuilderExtensions
    {
        /// <summary>
        /// Sets a deterministic key used by background replay/deferred work systems.
        /// Requests with the same key are treated as the same replay unit.
        /// </summary>
        public static UHttpRequestBuilder WithBackgroundReplayKey(
            this UHttpRequestBuilder builder,
            string dedupeKey)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (string.IsNullOrWhiteSpace(dedupeKey))
                throw new ArgumentException("Replay key cannot be null or empty.", nameof(dedupeKey));

            return builder.WithMetadata(RequestMetadataKeys.BackgroundReplayDedupeKey, dedupeKey);
        }
    }
}
