using System;
using TurboHTTP.Core;

namespace TurboHTTP.JSON
{
    /// <summary>
    /// Registers TurboHTTP.JSON as the <see cref="IJsonBridge"/> that Core-only modules (OAuth token
    /// parsing, mocks and record/replay, <c>GetJsonCoroutine&lt;T&gt;</c>, WebSocket JSON messages) use
    /// through <see cref="ProjectJsonBridge"/>.
    /// </summary>
    /// <remarks>
    /// TurboHTTP's startup bootstrap (TurboHTTP.Complete) calls <see cref="EnsureRegistered"/>, so no
    /// startup code is needed normally. Call it yourself when that assembly is excluded from the
    /// build (it is excluded on WebGL). The bridge forwards to <see cref="JsonSerializer"/>, so a
    /// serializer set with <see cref="JsonSerializer.SetDefault"/> is honored.
    /// </remarks>
    public static class JsonModuleRegistration
    {
        private static readonly IJsonBridge Bridge = new JsonSerializerBridge();

        /// <summary>
        /// Registers TurboHTTP.JSON with <see cref="ProjectJsonBridge"/>. Idempotent; replaces a bridge
        /// registered earlier.
        /// </summary>
        public static void EnsureRegistered()
        {
            ProjectJsonBridge.Register(Bridge);
        }

        private sealed class JsonSerializerBridge : IJsonBridge
        {
            public string Serialize(object value, Type type) => JsonSerializer.Serialize(value, type);

            public object Deserialize(string json, Type type) => JsonSerializer.Deserialize(json, type);
        }
    }
}
