using TurboHTTP.WebSocket;
using UnityEngine;
using UnityEngine.Scripting;

namespace TurboHTTP.Unity.WebSocket
{
    /// <summary>
    /// Always-on hidden listener that reports application resume to the WebSocket keep-alive (#1132), so every
    /// connection in a Unity player (<c>WebSocketClient</c>, <c>ResilientWebSocketClient</c>,
    /// <see cref="UnityWebSocketClient"/>) restarts deadlines that ran across a suspension instead of closing with
    /// PongTimeout.
    /// </summary>
    /// <remarks>
    /// Nothing outside this assembly references the listener: <c>[assembly: AlwaysLinkAssembly]</c> makes
    /// UnityLinker process the assembly, and <see cref="PreserveAttribute"/> keeps the type, its startup hook
    /// and its player messages at every stripping level (#1280).
    /// </remarks>
    [Preserve]
    internal sealed class WebSocketResumeListener : MonoBehaviour
    {
        private static WebSocketResumeListener s_instance;

        /// <summary>The installed listener, or null outside play mode (tests).</summary>
        internal static WebSocketResumeListener Instance => s_instance;

        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        internal static void Install()
        {
            // Unity's null check also covers an instance destroyed by a previous play session.
            if (s_instance != null || !Application.isPlaying)
                return;

            var go = new GameObject("[TurboHTTP WebSocketResumeListener]");
            go.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<WebSocketResumeListener>();
        }

        [Preserve]
        private void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
        }

        // Internal for tests; Unity calls it as a player message regardless of access.
        [Preserve]
        internal void OnApplicationPause(bool pauseStatus)
        {
            if (!pauseStatus)
                WebSocketConnection.NotifyApplicationResumed();
        }
    }
}
