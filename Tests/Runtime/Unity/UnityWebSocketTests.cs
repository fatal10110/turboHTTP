using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Unity;
using TurboHTTP.Unity.WebSocket;
using TurboHTTP.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    public class UnityWebSocketTests
    {
        [UnityTest]
        public IEnumerator UnityWebSocketBridge_MessageCallback_DispatchedOnMainThread()
        {
            var _ = MainThreadDispatcher.Instance;

            var fakeClient = new FakeWebSocketClient();
            using var bridge = new UnityWebSocketBridge(fakeClient);

            bool called = false;
            bool onMainThread = false;

            bridge.OnMessage += message =>
            {
                try
                {
                    onMainThread = MainThreadDispatcher.IsMainThread();
                    called = true;
                }
                finally
                {
                    message?.Dispose();
                }
            };

            fakeClient.EmitMessageFromWorker(CreateTestMessage("bridge-message"));

            yield return new WaitUntil(() => called);

            Assert.IsTrue(onMainThread, "Expected OnMessage callback on Unity main thread.");
        }

        [Test]
        public void UnityWebSocketClient_FireAndForgetSend_DoesNotThrow_WhenDisconnected()
        {
            var go = new GameObject("ws-component-send-test");
            try
            {
                var component = go.AddComponent<UnityWebSocketClient>();
                LogAssert.Expect(LogType.Error, "[TurboHTTP] UnityWebSocketClient send text failed: WebSocket client is not connected.");
                Assert.DoesNotThrow(() => component.Send("text"));
                LogAssert.Expect(LogType.Error, "[TurboHTTP] UnityWebSocketClient send binary failed: WebSocket client is not connected.");
                Assert.DoesNotThrow(() => component.Send(new byte[] { 1, 2, 3 }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [UnityTest]
        public IEnumerator UnityWebSocketBridge_ConnectedCallback_DispatchedOnMainThread()
        {
            var _ = MainThreadDispatcher.Instance;

            var fakeClient = new FakeWebSocketClient();
            using var bridge = new UnityWebSocketBridge(fakeClient);

            bool called = false;
            bool onMainThread = false;

            bridge.OnConnected += () =>
            {
                onMainThread = MainThreadDispatcher.IsMainThread();
                called = true;
            };

            fakeClient.EmitConnectedFromWorker();

            yield return new WaitUntil(() => called);

            Assert.IsTrue(onMainThread, "Expected OnConnected callback on Unity main thread.");
        }

        private static WebSocketMessage CreateTestMessage(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text ?? string.Empty);
            var ctor = typeof(WebSocketMessage).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[]
                {
                    typeof(WebSocketMessageType),
                    typeof(byte[]),
                    typeof(int),
                    typeof(string),
                    typeof(bool)
                },
                modifiers: null);

            if (ctor == null)
                throw new InvalidOperationException("Unable to access WebSocketMessage internal constructor.");

            return (WebSocketMessage)ctor.Invoke(new object[]
            {
                WebSocketMessageType.Text,
                data,
                data.Length,
                text,
                false
            });
        }

        private sealed class FakeWebSocketClient : IWebSocketClient
        {
            public event Action OnConnected;
            public event Action<WebSocketMessage> OnMessage;
            public event Action<WebSocketException> OnError;
            public event Action<WebSocketCloseCode, string> OnClosed;
            public event Action<WebSocketMetrics> OnMetricsUpdated;
            public event Action<ConnectionQuality> OnConnectionQualityChanged;

            public WebSocketState State { get; private set; } = WebSocketState.None;
            public string SubProtocol => null;
            public WebSocketMetrics Metrics => default;
            public WebSocketHealthSnapshot Health => WebSocketHealthSnapshot.Unknown;

            public Task ConnectAsync(Uri uri, CancellationToken ct = default)
            {
                State = WebSocketState.Open;
                OnConnected?.Invoke();
                return Task.CompletedTask;
            }

            public Task ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct = default)
            {
                State = WebSocketState.Open;
                OnConnected?.Invoke();
                return Task.CompletedTask;
            }

            public Task SendAsync(string message, CancellationToken ct = default)
            {
                return Task.CompletedTask;
            }

            public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
            {
                return Task.CompletedTask;
            }

            public Task SendAsync(byte[] data, CancellationToken ct = default)
            {
                return Task.CompletedTask;
            }

            public ValueTask<WebSocketMessage> ReceiveAsync(CancellationToken ct = default)
            {
                throw new InvalidOperationException("Not used by this fake.");
            }

            public IAsyncEnumerable<WebSocketMessage> ReceiveAllAsync(CancellationToken ct = default)
            {
                throw new InvalidOperationException("Not used by this fake.");
            }

            public Task CloseAsync(WebSocketCloseCode code = WebSocketCloseCode.NormalClosure, string reason = null, CancellationToken ct = default)
            {
                State = WebSocketState.Closed;
                OnClosed?.Invoke(code, reason ?? string.Empty);
                return Task.CompletedTask;
            }

            public void Abort()
            {
                State = WebSocketState.Closed;
            }

            public void Dispose()
            {
                State = WebSocketState.Closed;
            }

            public ValueTask DisposeAsync()
            {
                State = WebSocketState.Closed;
                return default;
            }

            public void EmitMessageFromWorker(WebSocketMessage message)
            {
                Task.Run(() => OnMessage?.Invoke(message));
            }

            public void EmitConnectedFromWorker()
            {
                Task.Run(() => OnConnected?.Invoke());
            }
        }
    }
}
