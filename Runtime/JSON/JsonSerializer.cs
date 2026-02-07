using System;
using TurboHTTP.JSON.Lite;

namespace TurboHTTP.JSON
{
    /// <summary>
    /// Static facade and registry for JSON serialization.
    /// Uses LiteJsonSerializer by default, but users can register custom serializers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Thread Safety:</b> Reading the current serializer is lock-free.
    /// Registration uses a lock but is expected to happen once at startup.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Use default LiteJson
    /// var json = JsonSerializer.Serialize(myObject);
    /// 
    /// // Register Newtonsoft at startup
    /// JsonSerializer.SetDefault(new NewtonsoftJsonSerializer());
    /// 
    /// // Now all serialization uses Newtonsoft
    /// var json = JsonSerializer.Serialize(myComplexObject);
    /// </code>
    /// </example>
    public static class JsonSerializer
    {
        private static readonly object _lock = new object();
        private static volatile IJsonSerializer _default = LiteJsonSerializer.Instance;

        /// <summary>
        /// Get or set the default JSON serializer.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="LiteJsonSerializer"/>.
        /// Set this at application startup before any serialization occurs.
        /// </remarks>
        public static IJsonSerializer Default
        {
            get => _default;
            set
            {
                lock (_lock)
                {
                    _default = value ?? LiteJsonSerializer.Instance;
                }
            }
        }

        /// <summary>
        /// Set the default serializer. Convenience method equivalent to setting Default property.
        /// </summary>
        /// <param name="serializer">Serializer implementation to use</param>
        /// <exception cref="ArgumentNullException">Serializer is null</exception>
        public static void SetDefault(IJsonSerializer serializer)
        {
            Default = serializer ?? throw new ArgumentNullException(nameof(serializer));
        }

        /// <summary>
        /// Reset to the built-in LiteJsonSerializer.
        /// </summary>
        public static void ResetToDefault()
        {
            Default = LiteJsonSerializer.Instance;
        }

        /// <summary>
        /// Serialize an object to JSON using the default serializer.
        /// </summary>
        /// <typeparam name="T">Type of object to serialize</typeparam>
        /// <param name="value">Object to serialize</param>
        /// <returns>JSON string</returns>
        /// <exception cref="JsonSerializationException">Serialization failed</exception>
        public static string Serialize<T>(T value)
        {
            return _default.Serialize(value);
        }

        /// <summary>
        /// Serialize an object to JSON using the default serializer (non-generic).
        /// </summary>
        /// <param name="value">Object to serialize</param>
        /// <param name="type">Type of the object</param>
        /// <returns>JSON string</returns>
        /// <exception cref="JsonSerializationException">Serialization failed</exception>
        public static string Serialize(object value, Type type)
        {
            return _default.Serialize(value, type);
        }

        /// <summary>
        /// Deserialize JSON to type T using the default serializer.
        /// </summary>
        /// <typeparam name="T">Target type</typeparam>
        /// <param name="json">JSON string to parse</param>
        /// <returns>Deserialized object</returns>
        /// <exception cref="JsonSerializationException">Deserialization failed</exception>
        public static T Deserialize<T>(string json)
        {
            return _default.Deserialize<T>(json);
        }

        /// <summary>
        /// Deserialize JSON to object using the default serializer (non-generic).
        /// </summary>
        /// <param name="json">JSON string to parse</param>
        /// <param name="type">Target type</param>
        /// <returns>Deserialized object</returns>
        /// <exception cref="JsonSerializationException">Deserialization failed</exception>
        public static object Deserialize(string json, Type type)
        {
            return _default.Deserialize(json, type);
        }

        /// <summary>
        /// Check if a custom serializer is registered.
        /// </summary>
        /// <returns>True if a non-default serializer is registered</returns>
        public static bool HasCustomSerializer()
        {
            return _default != LiteJsonSerializer.Instance;
        }
    }
}
