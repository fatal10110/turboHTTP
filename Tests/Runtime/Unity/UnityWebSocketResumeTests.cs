using NUnit.Framework;
using TurboHTTP.Unity.WebSocket;
using TurboHTTP.WebSocket;
using UnityEngine;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>#1132: the Unity component reports application resume to the WebSocket keep-alive.</summary>
    public sealed class UnityWebSocketResumeTests
    {
        [Test]
        public void UnityWebSocketClient_ApplicationResume_SignalsKeepAlive()
        {
            var go = new GameObject("ws-component-resume-test");
            try
            {
                var component = go.AddComponent<UnityWebSocketClient>();

                long before = WebSocketConnection.ApplicationResumedTimestamp;
                component.OnApplicationPause(true);
                Assert.AreEqual(before, WebSocketConnection.ApplicationResumedTimestamp,
                    "Pausing must not count as a resume.");

                component.OnApplicationPause(false);
                Assert.Greater(WebSocketConnection.ApplicationResumedTimestamp, before,
                    "OnApplicationPause(false) did not signal the keep-alive.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
