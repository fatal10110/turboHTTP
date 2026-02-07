using System;

namespace TurboHTTP.JSON
{
    /// <summary>
    /// Abstraction over JSON serialization implementation.
    /// Allows switching between LiteJson, Newtonsoft, System.Text.Json,
    /// or any custom JSON library without changing calling code.
    /// </summary>
    public interface IJsonSerializer
    {
        /// <summary>
        /// Serialize an object to a JSON string.
        /// </summary>
        /// <typeparam name="T">Type of object to serialize</typeparam>
        /// <param name="value">Object to serialize</param>
        /// <returns>JSON string representation</returns>
        /// <exception cref="JsonSerializationException">
        /// Serialization failed due to unsupported type or circular reference
        /// </exception>
        string Serialize<T>(T value);

        /// <summary>
        /// Deserialize a JSON string to an object.
        /// </summary>
        /// <typeparam name="T">Target type to deserialize to</typeparam>
        /// <param name="json">JSON string to parse</param>
        /// <returns>Deserialized object instance</returns>
        /// <exception cref="JsonSerializationException">
        /// Deserialization failed due to invalid JSON or type mismatch
        /// </exception>
        T Deserialize<T>(string json);

        /// <summary>
        /// Serialize an object to a JSON string (non-generic).
        /// </summary>
        /// <param name="value">Object to serialize</param>
        /// <param name="type">Type of the object</param>
        /// <returns>JSON string representation</returns>
        string Serialize(object value, Type type);

        /// <summary>
        /// Deserialize a JSON string to an object (non-generic).
        /// </summary>
        /// <param name="json">JSON string to parse</param>
        /// <param name="type">Target type to deserialize to</param>
        /// <returns>Deserialized object instance</returns>
        object Deserialize(string json, Type type);
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
