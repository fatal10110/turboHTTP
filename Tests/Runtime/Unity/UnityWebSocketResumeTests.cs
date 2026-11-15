using NUnit.Framework;
using TurboHTTP.Unity.WebSocket;
using TurboHTTP.WebSocket;
using UnityEngine;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>#1132: the Unity layer reports application resume to the WebSocket keep-alive.</summary>
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

        [Test]
        public void ResumeListener_IsInstalledInPlayMode_AndSignalsKeepAliveWithoutComponent()
        {
            // The AfterSceneLoad hook installs it on play-mode entry; Install is idempotent.
            WebSocketResumeListener.Install();
            var listener = WebSocketResumeListener.Instance;
            Assert.IsNotNull(listener, "No always-on resume listener: plain WebSocketClient users never get the signal.");
            Assert.AreEqual(1, Object.FindObjectsOfType<WebSocketResumeListener>().Length,
                "Install must not create a second listener.");

            long before = WebSocketConnection.ApplicationResumedTimestamp;
            listener.OnApplicationPause(true);
            Assert.AreEqual(before, WebSocketConnection.ApplicationResumedTimestamp,
                "Pausing must not count as a resume.");

            listener.OnApplicationPause(false);
            Assert.Greater(WebSocketConnection.ApplicationResumedTimestamp, before,
                "The resume listener did not signal the keep-alive.");
        }
    }
}
