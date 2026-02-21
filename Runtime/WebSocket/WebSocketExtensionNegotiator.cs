using System;
using System.Collections.Generic;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Stateless extension negotiator for Sec-WebSocket-Extensions.
    /// </summary>
    public sealed class WebSocketExtensionNegotiator
    {
        private readonly IReadOnlyList<IWebSocketExtension> _clientExtensions;

        public WebSocketExtensionNegotiator(IReadOnlyList<IWebSocketExtension> clientExtensions)
        {
            _clientExtensions = clientExtensions ?? Array.Empty<IWebSocketExtension>();
        }

        public string BuildOffersHeader()
        {
            if (_clientExtensions.Count == 0)
                return string.Empty;

            var offers = new List<string>();
            for (int i = 0; i < _clientExtensions.Count; i++)
            {
                var extension = _clientExtensions[i];
                if (extension == null)
                    throw new InvalidOperationException("Extension list contains a null extension entry.");

                var extensionOffers = extension.BuildOffers();
                if (extensionOffers == null)
                    continue;

                for (int j = 0; j < extensionOffers.Count; j++)
                {
                    offers.Add(extensionOffers[j].ToHeaderValue());
                }
            }

            return string.Join(", ", offers);
        }

        public WebSocketExtensionNegotiationResult ProcessNegotiation(string serverExtensionsHeader)
        {
            if (string.IsNullOrWhiteSpace(serverExtensionsHeader))
            {
                return WebSocketExtensionNegotiationResult.Success(
                    Array.Empty<IWebSocketExtension>(),
                    allowedRsvMask: 0);
            }

            var parsedEntries = SplitHeaderEntries(serverExtensionsHeader);
            if (parsedEntries.Count == 0)
            {
                return WebSocketExtensionNegotiationResult.Success(
                    Array.Empty<IWebSocketExtension>(),
                    allowedRsvMask: 0);
            }

            var offeredByName = new Dictionary<string, IWebSocketExtension>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _clientExtensions.Count; i++)
            {
                var extension = _clientExtensions[i];
                if (extension == null)
                {
                    return WebSocketExtensionNegotiationResult.Failure(
                        "Extension list contains a null extension entry.");
                }

                if (string.IsNullOrWhiteSpace(extension.Name))
                {
                    return WebSocketExtensionNegotiationResult.Failure(
                        "Extension has an empty Name value.");
                }

                if (!offeredByName.ContainsKey(extension.Name))
                    offeredByName.Add(extension.Name, extension);
            }

            var accepted = new List<IWebSocketExtension>(parsedEntries.Count);
            byte allowedRsvMask = 0;
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < parsedEntries.Count; i++)
            {
                WebSocketExtensionParameters parameters;
                try
                {
                    parameters = WebSocketExtensionParameters.Parse(parsedEntries[i]);
                }
                catch (Exception ex)
                {
                    return WebSocketExtensionNegotiationResult.Failure(
                        "Failed to parse server extension response: " + ex.Message);
                }

                if (!offeredByName.TryGetValue(parameters.ExtensionToken, out var extension))
                {
                    return WebSocketExtensionNegotiationResult.Failure(
                        "Server negotiated unsupported extension: " + parameters.ExtensionToken);
                }

                if (!seenNames.Add(parameters.ExtensionToken))
                {
                    return WebSocketExtensionNegotiationResult.Failure(
                        "Server negotiated duplicate extension: " + parameters.ExtensionToken);
                }

                if (!extension.AcceptNegotiation(parameters))
                {
                    return WebSocketExtensionNegotiationResult.Failure(
                        "Extension negotiation rejected by client extension: " + parameters.ExtensionToken);
                }

                if ((extension.RsvBitMask & ~0x70) != 0)
                {
                    return WebSocketExtensionNegotiationResult.Failure(
                        "Extension claims invalid RSV bits: " + parameters.ExtensionToken);
                }

                if ((allowedRsvMask & extension.RsvBitMask) != 0)
                {
                    return WebSocketExtensionNegotiationResult.Failure(
                        "Multiple extensions claimed overlapping RSV bits.");
                }

                allowedRsvMask |= extension.RsvBitMask;
                accepted.Add(extension);
            }

            return WebSocketExtensionNegotiationResult.Success(accepted, allowedRsvMask);
        }

        private static IReadOnlyList<string> SplitHeaderEntries(string headerValue)
        {
            var entries = new List<string>();
            if (string.IsNullOrWhiteSpace(headerValue))
                return entries;

            bool inQuotes = false;
            bool escaped = false;
            int entryStart = 0;

            for (int i = 0; i < headerValue.Length; i++)
            {
                char c = headerValue[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inQuotes)
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (c != ',' || inQuotes)
                    continue;

                AddEntry(entries, headerValue, entryStart, i - entryStart);
                entryStart = i + 1;
            }

            AddEntry(entries, headerValue, entryStart, headerValue.Length - entryStart);
            return entries;
        }

        private static void AddEntry(List<string> target, string source, int start, int length)
        {
            if (length <= 0)
                return;

            string entry = source.Substring(start, length).Trim();
            if (entry.Length > 0)
                target.Add(entry);
        }
    }

    public sealed class WebSocketExtensionNegotiationResult
    {
        private WebSocketExtensionNegotiationResult(
            bool isSuccess,
            IReadOnlyList<IWebSocketExtension> activeExtensions,
            byte allowedRsvMask,
            string errorMessage)
        {
            IsSuccess = isSuccess;
            ActiveExtensions = activeExtensions ?? Array.Empty<IWebSocketExtension>();
            AllowedRsvMask = (byte)(allowedRsvMask & 0x70);
            ErrorMessage = errorMessage ?? string.Empty;
        }

        public bool IsSuccess { get; }

        public IReadOnlyList<IWebSocketExtension> ActiveExtensions { get; }

        public byte AllowedRsvMask { get; }

        public string ErrorMessage { get; }

        public static WebSocketExtensionNegotiationResult Success(
            IReadOnlyList<IWebSocketExtension> activeExtensions,
            byte allowedRsvMask)
        {
            return new WebSocketExtensionNegotiationResult(
                isSuccess: true,
                activeExtensions: activeExtensions,
                allowedRsvMask: allowedRsvMask,
                errorMessage: string.Empty);
        }

        public static WebSocketExtensionNegotiationResult Failure(string errorMessage)
        {
            return new WebSocketExtensionNegotiationResult(
                isSuccess: false,
                activeExtensions: Array.Empty<IWebSocketExtension>(),
                allowedRsvMask: 0,
                errorMessage: errorMessage ?? "Extension negotiation failed.");
        }
    }
}
