using System;
using System.Buffers;

namespace TurboHTTP.JSON
{
    /// <summary>
    /// Abstraction over JSON serialization implementation.
    /// Allows switching between LiteJson, Newtonsoft, System.Text.Json,
    /// or any custom JSON library without changing calling code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations should write directly to the provided <see cref="IBufferWriter{T}"/>
    /// without intermediate byte-array allocations wherever possible.
    /// </para>
    /// <para>
    /// The buffer-writer and sequence overloads are the primary zero-alloc paths introduced
    /// in Phase 19a.2. The string-based overloads remain for convenience and for code
    /// that already has a JSON string in hand.
    /// </para>
    /// </remarks>
    public interface IJsonSerializer
    {
        // ── String-based API (convenience path) ─────────────────────────────────

        /// <summary>
        /// Serialize an object to a JSON string.
        /// </summary>
        /// <exception cref="JsonSerializationException">
        /// Serialization failed due to unsupported type or circular reference.
        /// </exception>
        string Serialize<T>(T value);

        /// <summary>
        /// Serialize an object to a JSON string (non-generic).
        /// </summary>
        string Serialize(object value, Type type);

        /// <summary>
        /// Deserialize a JSON string to type <typeparamref name="T"/>.
        /// </summary>
        /// <exception cref="JsonSerializationException">
        /// Deserialization failed due to invalid JSON or type mismatch.
        /// </exception>
        T Deserialize<T>(string json);

        /// <summary>
        /// Deserialize a JSON string to an object (non-generic).
        /// </summary>
        object Deserialize(string json, Type type);

        // ── Zero-alloc buffer-writer API (primary production path) ───────────────

        /// <summary>
        /// Serialize <paramref name="value"/> as UTF-8 JSON bytes directly into
        /// <paramref name="output"/> without allocating an intermediate string or byte array.
        /// </summary>
        /// <remarks>
        /// Implementations that do not natively support <see cref="IBufferWriter{T}"/>
        /// may bridge via the string path and encode the resulting string into the writer,
        /// but direct-write implementations are strongly preferred for zero-alloc paths.
        /// </remarks>
        /// <exception cref="JsonSerializationException">
        /// Serialization failed due to unsupported type or circular reference.
        /// </exception>
        void Serialize<T>(T value, IBufferWriter<byte> output);

        /// <summary>
        /// Deserialize <typeparamref name="T"/> from a <see cref="ReadOnlySequence{T}"/>
        /// of UTF-8 encoded JSON bytes.
        /// </summary>
        /// <remarks>
        /// The sequence may span multiple segments; implementations must handle both
        /// single-segment and multi-segment inputs correctly without flattening unless
        /// the underlying parser requires a contiguous span.
        /// </remarks>
        /// <exception cref="JsonSerializationException">
        /// Deserialization failed due to invalid JSON or type mismatch.
        /// </exception>
        T Deserialize<T>(ReadOnlySequence<byte> input);
    }

    /// <summary>
    /// Exception thrown when JSON serialization or deserialization fails.
    /// </summary>
    public class JsonSerializationException : Exception
    {
        public JsonSerializationException(string message)
            : base(message) { }

        public JsonSerializationException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
