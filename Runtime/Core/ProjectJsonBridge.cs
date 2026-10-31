using System;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Registration point for the JSON implementation that optional modules (Auth, Testing, Unity,
    /// WebSocket) use without referencing TurboHTTP.JSON. TurboHTTP.JSON registers itself from
    /// TurboHTTP's startup bootstrap (TurboHTTP.Complete). Projects that exclude that assembly, and
    /// WebGL builds (which exclude it too), call <c>TurboHTTP.JSON.JsonModuleRegistration.EnsureRegistered()</c>
    /// at startup or register their own <see cref="IJsonBridge"/>. Nothing here looks types up by
    /// name, so managed code stripping cannot remove what the bridge calls.
    /// </summary>
    public static class ProjectJsonBridge
    {
        // Single owner: one volatile reference; every call reads one snapshot.
        private static volatile IJsonBridge _bridge;

        /// <summary>True when a JSON bridge is registered.</summary>
        public static bool IsRegistered => _bridge != null;

        /// <summary>
        /// Registers the JSON implementation used by <see cref="Serialize"/>, <see cref="Deserialize"/>
        /// and <see cref="GetRequired"/>. A later registration replaces the earlier one.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="bridge"/> is null.</exception>
        public static void Register(IJsonBridge bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        /// <summary>Clears the registration. Tests only.</summary>
        internal static void Reset()
        {
            _bridge = null;
        }

        /// <summary>
        /// Returns the registered bridge, or throws an <see cref="InvalidOperationException"/> whose
        /// message names TurboHTTP.JSON and <paramref name="requiredBy"/> when none is registered.
        /// Exceptions the returned bridge throws reach the caller unwrapped.
        /// </summary>
        /// <param name="requiredBy">Feature name used in the error message.</param>
        public static IJsonBridge GetRequired(string requiredBy)
        {
            var bridge = _bridge;
            if (bridge == null)
            {
                throw new InvalidOperationException(
                    "TurboHTTP.JSON is required for " + requiredBy + ", but no JSON bridge is registered. " +
                    "Include the TurboHTTP.JSON and TurboHTTP.Complete assemblies (the startup bootstrap " +
                    "registers it), or call TurboHTTP.JSON.JsonModuleRegistration.EnsureRegistered() at startup.");
            }

            return bridge;
        }

        /// <summary>
        /// Deserializes <paramref name="json"/> to <paramref name="payloadType"/> through the registered bridge.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// No bridge is registered (the message names TurboHTTP.JSON), or deserialization failed
        /// (the bridge's exception is the inner exception).
        /// </exception>
        public static object Deserialize(string json, Type payloadType, string requiredBy)
        {
            if (payloadType == null) throw new ArgumentNullException(nameof(payloadType));
            var bridge = GetRequired(requiredBy);

            try
            {
                return bridge.Deserialize(json, payloadType);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to deserialize JSON payload for {requiredBy}.",
                    ex);
            }
        }

        /// <summary>
        /// Serializes <paramref name="payload"/> as <paramref name="payloadType"/> through the registered bridge.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// No bridge is registered (the message names TurboHTTP.JSON), or serialization failed
        /// (the bridge's exception is the inner exception).
        /// </exception>
        public static string Serialize(object payload, Type payloadType, string requiredBy)
        {
            if (payloadType == null) throw new ArgumentNullException(nameof(payloadType));
            var bridge = GetRequired(requiredBy);

            try
            {
                return bridge.Serialize(payload, payloadType);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to serialize JSON payload for {requiredBy}.",
                    ex);
            }
        }
    }
}
