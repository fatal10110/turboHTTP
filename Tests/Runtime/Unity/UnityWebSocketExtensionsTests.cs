using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Tests.WebSocket;
using TurboHTTP.Unity;
using TurboHTTP.Unity.WebSocket;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// <c>UHttpClient.WebSocket(...)</c>: messages before the first <c>OnMessage</c> subscription (#606), the
    /// default headers a handshake inherits (#950, #605) and the client's proxy (#950).
    /// </summary>
    public class UnityWebSocketExtensionsTests
    {
        private const float WaitBoundSeconds = 10f;

        [SetUp]
        public void SetUp()
        {
            RawSocketWebSocketTransport.EnsureRegistered();
        }

        [UnityTest]
        public IEnumerator WebSocket_GreetingSentRightAfterHandshake_IsDeliveredToSubscriberAttachedAfterAwait()
        {
            _ = MainThreadDispatcher.Instance;

            using var server = new HandshakeCaptureServer(greeting: "hello");
            using var client = CreateClient();

            IWebSocketClient ws = null;
            Exception connectError = null;
            var connect = ConnectAsync();

            async Task ConnectAsync()
            {
                try
                {
                    ws = await client.WebSocket(server.Uri).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    connectError = ex;
                }
            }

            yield return WaitUntilBounded(() => connect.IsCompleted);
            Assert.IsNull(connectError, connectError?.ToString());

            var bridge = (UnityWebSocketBridge)ws;
            try
            {
                // The greeting reached the bridge before anyone subscribed (the documented
                // `await client.WebSocket(url); ws.OnMessage += ...` order).
                yield return WaitUntilBounded(() => bridge.PendingMessageCount == 1);
                Assert.AreEqual(1, bridge.PendingMessageCount, "the greeting was not held for the first subscriber");

                var received = new List<string>();
                bridge.OnMessage += message =>
                {
                    received.Add(message.Text);
                    message.Dispose();
                };

                yield return WaitUntilBounded(() => received.Count == 1);
                CollectionAssert.AreEqual(new[] { "hello" }, received);
                Assert.AreEqual(0, bridge.PendingMessageCount);
            }
            finally
            {
                bridge.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Bridge_HeldMessages_AreDeliveredInOrderBeforeLaterMessages()
        {
            _ = MainThreadDispatcher.Instance;

            var scripted = new ScriptedWebSocketClient();
            using var bridge = new UnityWebSocketBridge(scripted, bufferMessagesUntilSubscribed: true);

            scripted.EmitMessage("first");
            scripted.EmitMessage("second");
            Assert.AreEqual(2, bridge.PendingMessageCount);

            var received = new List<string>();
            bridge.OnMessage += message =>
            {
                received.Add(message.Text);
                message.Dispose();
            };
            scripted.EmitMessage("third");

            yield return WaitUntilBounded(() => received.Count == 3);
            CollectionAssert.AreEqual(new[] { "first", "second", "third" }, received);
            Assert.AreEqual(0, bridge.PendingMessageCount);
        }

        [Test]
        public void Bridge_HeldMessages_AreBoundedDroppingOldest()
        {
            var scripted = new ScriptedWebSocketClient();
            using var bridge = new UnityWebSocketBridge(scripted, bufferMessagesUntilSubscribed: true);
            int dropped = 0;
            bridge.OnMessageDropped += () => dropped++;

            for (int i = 0; i < UnityWebSocketBridge.MaxPendingMessages + 3; i++)
                scripted.EmitMessage("m" + i);

            Assert.AreEqual(UnityWebSocketBridge.MaxPendingMessages, bridge.PendingMessageCount);
            Assert.AreEqual(3, dropped);
        }

        [Test]
        public void Bridge_HeldMessages_AreBoundedByBytes()
        {
            var scripted = new ScriptedWebSocketClient();
            using var bridge = new UnityWebSocketBridge(scripted, bufferMessagesUntilSubscribed: true);
            int dropped = 0;
            bridge.OnMessageDropped += () => dropped++;

            // Each message is just over half the byte bound: the second evicts the first.
            var large = new string('x', (int)(UnityWebSocketBridge.MaxPendingBytes / 2) + 1);
            scripted.EmitMessage(large);
            scripted.EmitMessage(large);

            Assert.AreEqual(1, bridge.PendingMessageCount);
            Assert.AreEqual(1, dropped);
        }

        [Test]
        public void Bridge_PullReceive_ReleasesHeldMessages()
        {
            var scripted = new ScriptedWebSocketClient();
            using var bridge = new UnityWebSocketBridge(scripted, bufferMessagesUntilSubscribed: true);
            scripted.EmitMessage("held");
            Assert.AreEqual(1, bridge.PendingMessageCount);

            // A puller gets the message from the client's own receive queue, so the hold ends.
            var pending = bridge.ReceiveAsync(CancellationToken.None).AsTask();

            Assert.AreEqual(0, bridge.PendingMessageCount);
            Assert.IsTrue(scripted.CompleteReceive(null));
            Assert.IsTrue(pending.IsCompleted);
            scripted.EmitMessage("after-pull");
            Assert.AreEqual(0, bridge.PendingMessageCount);
        }

        [Test]
        public void Bridge_Dispose_ReleasesHeldMessages()
        {
            var scripted = new ScriptedWebSocketClient();
            var bridge = new UnityWebSocketBridge(scripted, bufferMessagesUntilSubscribed: true);
            scripted.EmitMessage("held");
            Assert.AreEqual(1, bridge.PendingMessageCount);

            bridge.Dispose();

            Assert.AreEqual(0, bridge.PendingMessageCount);
        }

        [Test]
        public void Bridge_PublicConstructor_DoesNotHoldMessages()
        {
            var scripted = new ScriptedWebSocketClient();
            using var bridge = new UnityWebSocketBridge(scripted);

            scripted.EmitMessage("unobserved");

            Assert.AreEqual(0, bridge.PendingMessageCount);
        }

        [Test]
        public void WebSocket_OtherOrigin_HandshakeCarriesOnlyAllowListedDefaultHeaders()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new HandshakeCaptureServer(greeting: null);
                var options = CreateOptions();
                // The WebSocket host (127.0.0.1) is not the BaseUrl origin.
                options.BaseUrl = "https://api.example.test";
                options.DefaultHeaders.Set("Authorization", "Bearer secret-token");
                options.DefaultHeaders.Set("Cookie", "session=secret");
                options.DefaultHeaders.Set("X-Api-Key", "secret-key");
                options.DefaultHeaders.Set("Proxy-Authorization", "Basic c2VjcmV0");
                options.DefaultHeaders.Set("Connection", "keep-alive");
                options.DefaultHeaders.Set("User-Agent", "TurboTest/1.0");
                options.DefaultHeaders.Set("Accept-Language", "en");
                using var client = new UHttpClient(options);

                var ws = await client.WebSocket(server.Uri).ConfigureAwait(false);
                try
                {
                    var headers = await server.RequestHeaders.ConfigureAwait(false);

                    Assert.IsFalse(headers.ContainsKey("Authorization"), "Authorization was forwarded");
                    Assert.IsFalse(headers.ContainsKey("Cookie"), "Cookie was forwarded");
                    Assert.IsFalse(headers.ContainsKey("X-Api-Key"), "X-Api-Key was forwarded");
                    Assert.IsFalse(headers.ContainsKey("Proxy-Authorization"), "Proxy-Authorization was forwarded");
                    Assert.AreEqual("Upgrade", headers["Connection"]);
                    Assert.AreEqual("TurboTest/1.0", headers["User-Agent"]);
                    Assert.AreEqual("en", headers["Accept-Language"]);
                }
                finally
                {
                    await ws.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void WebSocket_ClientProxyWithCredentials_IsApplied()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var proxy = new WebSocketTestProxyServer("user", "pass");
                var options = CreateOptions();
                options.Proxy = new ProxySettings
                {
                    Address = new Uri("http://127.0.0.1:" + proxy.Port),
                    Credentials = new NetworkCredential("user", "pass"),
                    AllowPlaintextProxyAuth = true,
                    UseEnvironmentVariables = false
                };
                using var client = new UHttpClient(options);

                var ws = await client.WebSocket(server.CreateUri("/via-proxy")).ConfigureAwait(false);
                try
                {
                    await ws.SendAsync("via-proxy", CancellationToken.None).ConfigureAwait(false);
                    using var message = await ws.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual("via-proxy", message.Text);
                    Assert.AreEqual("127.0.0.1:" + server.Port, proxy.LastAuthority);
                    Assert.AreEqual(1, proxy.AuthenticatedConnectCount);
                }
                finally
                {
                    await ws.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void WebSocket_ClientProxyCidrBypass_ConnectsDirectly()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var proxy = new WebSocketTestProxyServer();
                var options = CreateOptions();
                options.Proxy = new ProxySettings
                {
                    Address = new Uri("http://127.0.0.1:" + proxy.Port),
                    // CIDR is client bypass syntax the WebSocket bypass list does not understand.
                    BypassList = new[] { "127.0.0.0/8" },
                    UseEnvironmentVariables = false
                };
                using var client = new UHttpClient(options);

                var ws = await client.WebSocket(server.CreateUri("/direct")).ConfigureAwait(false);
                await ws.DisposeAsync().ConfigureAwait(false);

                Assert.AreEqual(0, proxy.ConnectionCount);
            });
        }

        [Test]
        public void WebSocket_ProxyCredentialsWithoutPlaintextProxyAuth_AreNotSent()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new WebSocketTestServer();
                using var proxy = new WebSocketTestProxyServer("user", "pass");
                var options = CreateOptions();
                options.Proxy = new ProxySettings
                {
                    Address = new Uri("http://127.0.0.1:" + proxy.Port),
                    Credentials = new NetworkCredential("user", "pass"),
                    UseEnvironmentVariables = false
                };
                using var client = new UHttpClient(options);

                var ex = await AssertWebSocketConnectFails(client, server.CreateUri("/no-auth")).ConfigureAwait(false);

                Assert.IsNotNull(ex, "connect succeeded although the proxy requires credentials");
                Assert.AreEqual(0, proxy.AuthenticatedConnectCount);
                Assert.AreEqual("127.0.0.1:" + server.Port, proxy.LastAuthority);
            });
        }

        private static async Task<WebSocketException> AssertWebSocketConnectFails(UHttpClient client, Uri uri)
        {
            try
            {
                var ws = await client.WebSocket(uri).ConfigureAwait(false);
                await ws.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            catch (WebSocketException ex)
            {
                return ex;
            }
        }

        private static UHttpClientOptions CreateOptions()
        {
            return new UHttpClientOptions
            {
                Transport = new MockTransport(useDefaultFallback: true),
                DisposeTransport = true,
                AllowCleartext = true,
                // No proxy from the machine's HTTP(S)_PROXY variables.
                Proxy = new ProxySettings { UseEnvironmentVariables = false }
            };
        }

        private static UHttpClient CreateClient() => new UHttpClient(CreateOptions());

        private static IEnumerator WaitUntilBounded(Func<bool> condition)
        {
            float deadline = Time.realtimeSinceStartup + WaitBoundSeconds;
            yield return new WaitUntil(() => condition() || Time.realtimeSinceStartup > deadline);
        }

        /// <summary>
        /// One-connection WebSocket server that records the handshake request headers and can send a text
        /// frame right after its 101 response; it then holds the connection until disposed.
        /// </summary>
        private sealed class HandshakeCaptureServer : IDisposable
        {
            private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            private const int MaxHeadBytes = 16 * 1024;

            private readonly TcpListener _listener;
            private readonly string _greeting;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly TaskCompletionSource<Dictionary<string, string>> _headers =
                new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Task _serveTask;
            private TcpClient _connection;

            public HandshakeCaptureServer(string greeting)
            {
                _greeting = greeting;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _serveTask = Task.Run(ServeAsync);
            }

            public Uri Uri => new Uri("ws://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/capture");

            /// <summary>Handshake request headers (case-insensitive names; repeated names joined with ", ").</summary>
            public Task<Dictionary<string, string>> RequestHeaders => _headers.Task;

            public void Dispose()
            {
                _cts.Cancel();
                _listener.Stop();
                _connection?.Dispose();
                _headers.TrySetCanceled();
            }

            private async Task ServeAsync()
            {
                try
                {
                    _connection = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    var stream = _connection.GetStream();
                    var head = await ReadHeadAsync(stream).ConfigureAwait(false);
                    var headers = ParseHeaders(head);
                    _headers.TrySetResult(headers);

                    string accept;
                    using (var sha1 = SHA1.Create())
                    {
                        accept = Convert.ToBase64String(
                            sha1.ComputeHash(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + AcceptGuid)));
                    }

                    var response = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 101 Switching Protocols\r\n" +
                        "Upgrade: websocket\r\n" +
                        "Connection: Upgrade\r\n" +
                        "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
                    await stream.WriteAsync(response, 0, response.Length, _cts.Token).ConfigureAwait(false);

                    if (_greeting != null)
                    {
                        var payload = Encoding.UTF8.GetBytes(_greeting);
                        var frame = new byte[2 + payload.Length];
                        frame[0] = 0x81; // FIN, text
                        frame[1] = (byte)payload.Length; // unmasked, under 126 bytes
                        Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
                        await stream.WriteAsync(frame, 0, frame.Length, _cts.Token).ConfigureAwait(false);
                    }

                    // Hold the connection open: read (and ignore) client frames until it closes.
                    var sink = new byte[1024];
                    while (await stream.ReadAsync(sink, 0, sink.Length, _cts.Token).ConfigureAwait(false) > 0)
                    {
                    }
                }
                catch (Exception ex)
                {
                    _headers.TrySetException(ex);
                }
            }

            private static async Task<string> ReadHeadAsync(NetworkStream stream)
            {
                var buffer = new List<byte>();
                var one = new byte[1];
                while (buffer.Count < MaxHeadBytes)
                {
                    if (await stream.ReadAsync(one, 0, 1).ConfigureAwait(false) == 0)
                        throw new InvalidOperationException("connection closed before the handshake head ended");

                    buffer.Add(one[0]);
                    int n = buffer.Count;
                    if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n')
                        return Encoding.ASCII.GetString(buffer.ToArray());
                }

                throw new InvalidOperationException("handshake head too large");
            }

            private static Dictionary<string, string> ParseHeaders(string head)
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon <= 0)
                        continue;

                    var name = lines[i].Substring(0, colon).Trim();
                    var value = lines[i].Substring(colon + 1).Trim();
                    headers[name] = headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
                }

                return headers;
            }
        }
    }
}
