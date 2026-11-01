using System;

namespace TurboHTTP.Core
{
    /// <summary>
    /// JSON entry points that the optional TurboHTTP.JSON module registers with
    /// <see cref="ProjectJsonBridge.Register"/>, so modules that reference only TurboHTTP.Core can
    /// serialize JSON without reflection. Internal since #77, like <see cref="ProjectJsonBridge"/>.
    /// </summary>
    internal interface IJsonBridge
    {
        /// <summary>Serializes <paramref name="value"/> declared as <paramref name="type"/> to JSON text.</summary>
        string Serialize(object value, Type type);

        /// <summary>
        /// Deserializes <paramref name="json"/> to <paramref name="type"/>. Value types are returned boxed.
        /// </summary>
        object Deserialize(string json, Type type);
    }
}
