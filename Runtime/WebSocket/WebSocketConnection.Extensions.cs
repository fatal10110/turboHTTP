using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.WebSocket
{
    public sealed partial class WebSocketConnection
    {
        private async Task WriteMessageWithExtensionsAsync(
            Stream stream,
            WebSocketOpcode opcode,
            ReadOnlyMemory<byte> payload,
            CancellationToken ct)
        {
            ReadOnlyMemory<byte> transformedPayload = payload;
            IMemoryOwner<byte> currentOwner = null;
            byte rsvBits = 0;

            try
            {
                for (int i = 0; i < _activeExtensions.Count; i++)
                {
                    var extension = _activeExtensions[i];
                    if (extension == null)
                        continue;

                    var transformed = extension.TransformOutbound(transformedPayload, opcode, out byte extensionRsvBits);

                    if ((extensionRsvBits & ~extension.RsvBitMask) != 0)
                    {
                        throw new WebSocketException(
                            WebSocketError.ProtocolViolation,
                            "Extension attempted to set RSV bits outside its declared mask.");
                    }

                    if ((rsvBits & extensionRsvBits) != 0)
                    {
                        throw new WebSocketException(
                            WebSocketError.ProtocolViolation,
                            "Multiple extensions attempted to set overlapping RSV bits.");
                    }

                    rsvBits |= extensionRsvBits;

                    if (transformed == null)
                        continue;

                    currentOwner?.Dispose();
                    currentOwner = transformed;
                    transformedPayload = transformed.Memory;
                }

                await _frameWriter.WriteMessageAsync(stream, opcode, transformedPayload, ct, rsvBits)
                    .ConfigureAwait(false);

                CalculateMessageWireStats(
                    transformedPayload.Length,
                    _options.FragmentationThreshold,
                    out int frameCount,
                    out int byteCount);

                _metrics?.RecordFramesSent(frameCount, byteCount);
                _metrics?.RecordMessageSent();

                if (IsCompressionRsvBitSet(rsvBits))
                    _metrics?.RecordCompression(payload.Length, transformedPayload.Length);

                TryPublishMetricsUpdate();
            }
            finally
            {
                currentOwner?.Dispose();
            }
        }

        private IMemoryOwner<byte> ApplyInboundExtensions(WebSocketAssembledMessage assembledMessage)
        {
            if (assembledMessage == null)
                throw new ArgumentNullException(nameof(assembledMessage));

            if (_activeExtensions.Count == 0 || assembledMessage.RsvBits == 0)
                return null;

            ReadOnlyMemory<byte> payload = assembledMessage.Payload;
            IMemoryOwner<byte> currentOwner = null;
            byte remainingRsvBits = assembledMessage.RsvBits;

            for (int i = _activeExtensions.Count - 1; i >= 0; i--)
            {
                var extension = _activeExtensions[i];
                if (extension == null)
                    continue;

                if ((remainingRsvBits & extension.RsvBitMask) == 0)
                    continue;

                var transformed = extension.TransformInbound(payload, assembledMessage.Opcode, remainingRsvBits);
                remainingRsvBits = (byte)(remainingRsvBits & ~extension.RsvBitMask);

                if (transformed == null)
                    continue;

                currentOwner?.Dispose();
                currentOwner = transformed;
                payload = transformed.Memory;
            }

            if (remainingRsvBits != 0)
            {
                currentOwner?.Dispose();
                throw new WebSocketProtocolException(
                    WebSocketError.ProtocolViolation,
                    "Incoming frame set RSV bits without a matching negotiated extension.");
            }

            return currentOwner;
        }

        private byte[] AcquireMessagePayloadBuffer(
            WebSocketAssembledMessage assembledMessage,
            IMemoryOwner<byte> transformedInbound,
            out int payloadLength)
        {
            if (transformedInbound == null)
                return assembledMessage.DetachPayloadBuffer(out payloadLength);

            var transformedMemory = transformedInbound.Memory;
            payloadLength = transformedMemory.Length;

            if (payloadLength > _options.MaxMessageSize)
            {
                throw new WebSocketException(
                    WebSocketError.DecompressedMessageTooLarge,
                    "Decompressed payload exceeds configured MaxMessageSize.");
            }

            if (payloadLength == 0)
                return null;

            if (transformedInbound is ArrayPoolMemoryOwner<byte> poolOwner &&
                poolOwner.TryDetach(out var detachedBuffer, out int detachedLength))
            {
                payloadLength = detachedLength;
                return detachedBuffer;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(payloadLength);
            transformedMemory.CopyTo(new Memory<byte>(buffer, 0, payloadLength));
            return buffer;
        }

        private static void DisposeExtensions(IReadOnlyList<IWebSocketExtension> extensions)
        {
            if (extensions == null || extensions.Count == 0)
                return;

            for (int i = extensions.Count - 1; i >= 0; i--)
            {
                SafeDispose(extensions[i]);
            }
        }

        private static void DisposeUnselectedExtensions(
            IReadOnlyList<IWebSocketExtension> configuredExtensions,
            IReadOnlyList<IWebSocketExtension> selectedExtensions)
        {
            if (configuredExtensions == null || configuredExtensions.Count == 0)
                return;

            var selected = new HashSet<IWebSocketExtension>();
            if (selectedExtensions != null)
            {
                for (int i = 0; i < selectedExtensions.Count; i++)
                {
                    if (selectedExtensions[i] != null)
                        selected.Add(selectedExtensions[i]);
                }
            }

            for (int i = 0; i < configuredExtensions.Count; i++)
            {
                var extension = configuredExtensions[i];
                if (extension != null && !selected.Contains(extension))
                    SafeDispose(extension);
            }
        }

        private static List<IWebSocketExtension> CreateConnectionExtensions(WebSocketConnectionOptions options)
        {
            var result = new List<IWebSocketExtension>();
            if (options?.ExtensionFactories == null)
                return result;

            for (int i = 0; i < options.ExtensionFactories.Count; i++)
            {
                var factory = options.ExtensionFactories[i];
                if (factory == null)
                    continue;

                var extension = factory();
                if (extension == null)
                {
                    throw new InvalidOperationException(
                        "Extension factory at index " + i + " returned null.");
                }

                result.Add(extension);
            }

            return result;
        }

        private static IReadOnlyList<string> BuildRequestedExtensions(
            IReadOnlyList<string> rawExtensions,
            WebSocketExtensionNegotiator negotiator)
        {
            var result = new List<string>();

            if (negotiator != null)
            {
                string structuredOffers = negotiator.BuildOffersHeader();
                if (!string.IsNullOrWhiteSpace(structuredOffers))
                    result.Add(structuredOffers);
            }

            if (rawExtensions != null)
            {
                for (int i = 0; i < rawExtensions.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(rawExtensions[i]))
                        continue;

                    result.Add(rawExtensions[i]);
                }
            }

            return result;
        }

        private static IReadOnlyList<string> BuildNegotiatedExtensionNames(
            IReadOnlyList<IWebSocketExtension> activeExtensions)
        {
            if (activeExtensions == null || activeExtensions.Count == 0)
                return Array.Empty<string>();

            var names = new List<string>(activeExtensions.Count);
            for (int i = 0; i < activeExtensions.Count; i++)
            {
                var extension = activeExtensions[i];
                if (extension == null || string.IsNullOrWhiteSpace(extension.Name))
                    continue;

                names.Add(extension.Name);
            }

            return names;
        }

        private static string JoinHeaderValues(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
                return string.Empty;

            if (values.Count == 1)
                return values[0] ?? string.Empty;

            return string.Join(", ", values);
        }

        private bool IsCompressionRsvBitSet(byte rsvBits)
        {
            const byte rsv1Mask = 0x40;
            if ((rsvBits & rsv1Mask) == 0)
                return false;

            if (_activeExtensions == null || _activeExtensions.Count == 0)
                return false;

            for (int i = 0; i < _activeExtensions.Count; i++)
            {
                var extension = _activeExtensions[i];
                if (extension == null)
                    continue;

                if ((extension.RsvBitMask & rsv1Mask) == 0)
                    continue;

                if (string.Equals(extension.Name, "permessage-deflate", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
