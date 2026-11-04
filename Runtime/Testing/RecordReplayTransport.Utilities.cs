using System;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using TurboHTTP.Core;

namespace TurboHTTP.Testing
{
    public sealed partial class RecordReplayTransport
    {
        private static RecordingErrorDto ToErrorDto(UHttpError error)
        {
            if (error == null)
                return null;

            return new RecordingErrorDto
            {
                Type = error.Type.ToString(),
                Message = error.Message,
                StatusCode = error.StatusCode.HasValue ? (int?)error.StatusCode.Value : null
            };
        }

        private static UHttpError ToError(RecordingErrorDto error)
        {
            if (error == null)
                return null;

            if (!Enum.TryParse(error.Type, ignoreCase: true, out UHttpErrorType parsedType))
                parsedType = UHttpErrorType.Unknown;

            HttpStatusCode? statusCode = null;
            if (error.StatusCode.HasValue)
                statusCode = (HttpStatusCode)error.StatusCode.Value;

            return new UHttpError(parsedType, error.Message ?? "Unknown replay error", statusCode: statusCode);
        }

        private static bool TryDequeue(
            ConcurrentDictionary<string, ConcurrentQueue<ReplayNode>> source,
            string key,
            out RecordingEntryDto entry)
        {
            entry = null;
            if (!source.TryGetValue(key, out var queue))
                return false;

            while (queue.TryDequeue(out var node))
            {
                if (node.TryConsume())
                {
                    entry = node.Entry;
                    return true;
                }
            }

            return false;
        }

        // Sensitive header values in match keys are stored only as HMAC-SHA256 under the per-recording
        // salt, so the recording file never holds the raw credential (#93).
        private static string ComputeKeyedValueHash(string value, byte[] key)
        {
            using var hmac = new HMACSHA256(key);
            return "hmac-sha256:" + ToLowerHex(hmac.ComputeHash(Utf8.GetBytes(value ?? string.Empty)));
        }

        // Without a salt (recordings written before #93) the request body hash is plain SHA-256; with one
        // it is HMAC-SHA256, so a low-entropy body (a form with a password) cannot be guessed from it.
        private static string ComputeBodyHash(ReadOnlyMemory<byte> body, byte[] key)
        {
            if (body.IsEmpty)
                return "sha256:empty";

            byte[] bodyBytes;
            int bodyOffset;
            int bodyLength = body.Length;
            if (MemoryMarshal.TryGetArray(body, out var segment) && segment.Array != null)
            {
                bodyBytes = segment.Array;
                bodyOffset = segment.Offset;
                bodyLength = segment.Count;
            }
            else
            {
                bodyBytes = body.ToArray();
                bodyOffset = 0;
            }

            try
            {
                using HashAlgorithm algorithm = key != null ? new HMACSHA256(key) : SHA256.Create();
                if (algorithm == null)
                {
                    throw new InvalidOperationException(
                        "SHA-256 provider is unavailable. Preserve SHA256 types from stripping " +
                        "(see Runtime/Testing/link.xml) for IL2CPP builds.");
                }

                byte[] hash;
                if (bodyLength > LargeBodyThresholdBytes)
                {
                    var firstLength = Math.Min(BodyEdgeSliceBytes, bodyLength);
                    var lastLength = Math.Min(BodyEdgeSliceBytes, bodyLength - firstLength);
                    algorithm.TransformBlock(bodyBytes, bodyOffset, firstLength, null, 0);
                    if (lastLength > 0)
                    {
                        var lastOffset = bodyOffset + bodyLength - lastLength;
                        algorithm.TransformBlock(bodyBytes, lastOffset, lastLength, null, 0);
                    }

                    var lengthBytes = BitConverter.GetBytes((long)bodyLength);
                    if (!BitConverter.IsLittleEndian)
                        Array.Reverse(lengthBytes);
                    algorithm.TransformFinalBlock(lengthBytes, 0, lengthBytes.Length);
                    hash = algorithm.Hash;
                }
                else
                {
                    hash = algorithm.ComputeHash(bodyBytes, bodyOffset, bodyLength);
                }

                return (key != null ? "hmac-sha256:" : "sha256:") + ToLowerHex(hash);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Failed to compute request body hash with SHA-256. " +
                    "This usually means the hashing provider was stripped or unavailable on this platform. " +
                    "Add SHA256 preservation guidance from Runtime/Testing/link.xml.",
                    ex);
            }
        }

        private static string ToLowerHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            var chars = new char[bytes.Length * 2];
            int index = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                chars[index++] = ToHexNibble(b >> 4);
                chars[index++] = ToHexNibble(b & 0x0F);
            }
            return new string(chars);
        }

        private static char ToHexNibble(int value)
        {
            return (char)(value < 10 ? ('0' + value) : ('a' + (value - 10)));
        }
    }
}
