using System;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Transport;
using TurboHTTP.WebSocket.Transport;
using UnityEngine;
using UnityEngine.Scripting;

namespace TurboHTTP
{
    /// <summary>
    /// Registers the default HTTP and WebSocket transports when the player (or Editor play mode)
    /// starts, so <c>new UHttpClient()</c> and <c>WebSocketTransportFactory.Create()</c> work without
    /// any startup code.
    /// </summary>
    /// <remarks>
    /// Unity 2021.3 does not run <c>[ModuleInitializer]</c> methods reliably, and nothing else
    /// references TurboHTTP.Transport or TurboHTTP.WebSocket.Transport statically, so the module
    /// initializers in those assemblies are only a secondary path. This class lives in the
    /// TurboHTTP.Complete assembly, the one TurboHTTP assembly that references UnityEngine and both
    /// transport assemblies; Unity calls <see cref="RegisterDefaultTransports"/> at
    /// <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/>, before any scene loads or
    /// <c>BeforeSceneLoad</c>/<c>Awake</c> code runs. The assembly carries
    /// <see cref="AlwaysLinkAssemblyAttribute"/> so UnityLinker processes it although no other
    /// assembly references it, and <see cref="PreserveAttribute"/> then keeps this type and method
    /// under every managed stripping level. A transport registered later (for example from
    /// <c>BeforeSceneLoad</c>) replaces this default; projects that strip or exclude this assembly
    /// call <c>RawSocketTransport.EnsureRegistered()</c> and
    /// <c>RawSocketWebSocketTransport.EnsureRegistered()</c> at startup instead.
    /// </remarks>
    [Preserve]
    internal static class TurboHttpBootstrap
    {
        /// <summary>
        /// Registers <see cref="RawSocketTransport"/> and <see cref="RawSocketWebSocketTransport"/> as
        /// the default transports. Runs once per player start and once per Editor play-mode entry
        /// (also when domain reload is disabled), so each session starts from the default
        /// registration. In the Editor it also runs after every domain reload, so editor tools and
        /// EditMode code outside play mode get the same defaults. With Enter Play Mode without a
        /// domain reload, static state survives from the previous session: the default HTTP
        /// transport that session created is disposed after the new registration, so its sockets
        /// and HTTP/2 read loops do not leak. WebSocket transports are created per call and owned by
        /// their callers, so there is no WebSocket singleton to dispose.
        /// </summary>
        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        internal static void RegisterDefaultTransports()
        {
            var previous = HttpTransportFactory.DetachCreatedDefault();
            RawSocketTransport.EnsureRegistered();
            RawSocketWebSocketTransport.EnsureRegistered();
            if (previous == null)
                return;

            try
            {
                previous.Dispose();
            }
            catch (Exception ex)
            {
                // Registration already succeeded; a failing Dispose of the old session's
                // transport must not break startup. Log the type only (no message text).
                Debug.LogWarning("TurboHTTP: disposing the previous session's default transport failed ("
                    + ex.GetType().Name + ").");
            }
        }

        /// <summary>
        /// Registers TurboHTTP.JSON with <see cref="ProjectJsonBridge"/>, so OAuth token parsing, the
        /// mocks, <c>GetJsonCoroutine&lt;T&gt;</c> and WebSocket JSON messages reach JSON through
        /// registration instead of a by-name type lookup that managed stripping can break. Same hooks
        /// as <see cref="RegisterDefaultTransports"/>; projects that exclude this assembly call
        /// <see cref="JsonModuleRegistration.EnsureRegistered"/> at startup instead.
        /// </summary>
        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        internal static void RegisterJsonBridge()
        {
            JsonModuleRegistration.EnsureRegistered();
        }
    }
}
