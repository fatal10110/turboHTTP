using System;
using TurboHTTP.Core;
using TurboHTTP.JSON.Lite;

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
    /// build (it is excluded on WebGL). User payloads (mock JSON responses, <c>GetJsonCoroutine&lt;T&gt;</c>,
    /// WebSocket JSON messages) go through <see cref="JsonSerializer"/>, so a serializer set with
    /// <see cref="JsonSerializer.SetDefault"/> is honored; record/replay files and OAuth token responses are
    /// parsed with <see cref="Lite.LiteJsonSerializer"/> whatever the default is. This call does not
    /// change <see cref="JsonSerializer.Default"/>: without TurboHTTP.Complete it stays the built-in
    /// <see cref="Lite.LiteJsonSerializer"/>, so register a serializer for custom types yourself.
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

        // Typed pair: user payloads follow JsonSerializer.Default (SetDefault is honored).
        // Document-model pair: always LiteJson, so internal tree walkers (record/replay, OAuth token
        // responses) get Dictionary<string, object> / List<object> whatever the default is (#1034).
        private sealed class JsonSerializerBridge : IJsonBridge, IJsonDomBridge
        {
            public string Serialize(object value, Type type) => JsonSerializer.Serialize(value, type);

            public object Deserialize(string json, Type type) => JsonSerializer.Deserialize(json, type);

            public object ParseDom(string json, int maxInputBytes)
            {
                var parser = maxInputBytes <= LiteJsonSerializer.DefaultMaxInputBytes
                    ? LiteJsonSerializer.Instance
                    : new LiteJsonSerializer(maxInputBytes);
                return parser.Deserialize(json, typeof(object));
            }

            public string WriteDom(object value) => LiteJsonSerializer.Instance.Serialize(value, typeof(object));
        }
    }
}
