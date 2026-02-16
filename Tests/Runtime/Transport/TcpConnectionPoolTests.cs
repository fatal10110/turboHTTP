using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport
{
    [TestFixture]
    public class TcpConnectionPoolTests
    {
        private sealed class PassiveServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _lock = new object();
            private int _acceptCount;

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public int AcceptCount => Volatile.Read(ref _acceptCount);

            public PassiveServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = Task.Run(AcceptLoop);
            }

            private async Task AcceptLoop()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client = null;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        if (_cts.IsCancellationRequested)
                            break;
                        continue;
                    }

                    lock (_lock)
                    {
                        _clients.Add(client);
                    }

                    Interlocked.Increment(ref _acceptCount);
                }
            }

            public void CloseAllClients(bool reset)
            {
                lock (_lock)
                {
                    foreach (var client in _clients)
                    {
                        try
                        {
                            if (reset)
                                client.Client.LingerState = new LingerOption(true, 0);
                            client.Close();
                        }
                        catch { }
                    }
                    _clients.Clear();
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                _listener.Stop();
                CloseAllClients(false);
            }
        }

        [Test]
        public void GetConnection_CreatesNewConnection()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool();
                using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.IsNotNull(lease.Connection);
                Assert.IsFalse(lease.Connection.IsReused);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ReturnConnection_ConnectionReused()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                using var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                var conn1 = lease1.Connection;
                lease1.ReturnToPool();
                lease1.Dispose();

                using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.AreSame(conn1, lease2.Connection);
                Assert.IsTrue(lease2.Connection.IsReused);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ReturnConnection_StaleConnection_Disposed()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                using var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                var conn1 = lease1.Connection;

                server.CloseAllClients(reset: true);

                // Try to observe server-side reset first.
                bool observedRemoteClose = false;
                var probeByte = new byte[] { 0x2A };
                for (int i = 0; i < 200; i++)
                {
                    if (!conn1.IsAlive)
                    {
                        observedRemoteClose = true;
                        break;
                    }

                    try
                    {
                        conn1.Socket.Send(probeByte);
                    }
                    catch (SocketException)
                    {
                        observedRemoteClose = true;
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        observedRemoteClose = true;
                        break;
                    }

                    await Task.Delay(20);
                }

                // Some runtimes do not surface remote reset promptly. Force-close locally
                // so this test deterministically validates pool behavior.
                if (!observedRemoteClose && conn1.IsAlive)
                {
                    try { conn1.Socket.Shutdown(SocketShutdown.Both); } catch { }
                    try { conn1.Socket.Close(); } catch { }
                }

                lease1.ReturnToPool();
                lease1.Dispose();

                using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.AreNotSame(conn1, lease2.Connection);
                Assert.IsFalse(conn1.IsAlive);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Dispose_DrainsAllConnections()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                var pool = new TcpConnectionPool();
                ConnectionLease lease = null;
                try
                {
                    lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                    var conn = lease.Connection;
                    lease.ReturnToPool();
                    lease.Dispose();
                    pool.Dispose();

                    Assert.IsFalse(conn.IsAlive);
                }
                finally
                {
                    lease?.Dispose();
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void MaxConnectionsPerHost_BlocksWhenAtLimit()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                using var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                var task = pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);

                await Task.Delay(100);
                Assert.IsFalse(task.IsCompleted);

                lease1.Dispose();
                using var lease2 = await task;
                Assert.IsNotNull(lease2.Connection);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void MaxConnectionsPerHost_WaitThenProceed()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 2);

                using var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);

                var task = pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                await Task.Delay(100);
                Assert.IsFalse(task.IsCompleted);

                lease1.Dispose();
                using var lease3 = await task;
                Assert.IsNotNull(lease3.Connection);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DisposedPool_ReturnConnection_DisposesConnection()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                var pool = new TcpConnectionPool();

                using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                var conn = lease.Connection;
                pool.Dispose();

                lease.ReturnToPool();
                lease.Dispose();

                Assert.IsFalse(conn.IsAlive);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CaseInsensitiveHostKey_SharesPool()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                using var lease1 = await pool.GetConnectionAsync("LOCALHOST", server.Port, false, CancellationToken.None);
                var conn = lease1.Connection;
                lease1.ReturnToPool();
                lease1.Dispose();

                using var lease2 = await pool.GetConnectionAsync("localhost", server.Port, false, CancellationToken.None);
                Assert.AreSame(conn, lease2.Connection);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PooledConnection_IsReused_FalseForNewConnection()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool();

                using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.IsFalse(lease.Connection.IsReused);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PooledConnection_IsReused_TrueAfterPoolDequeue()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                using var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                lease1.ReturnToPool();
                lease1.Dispose();

                using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.IsTrue(lease2.Connection.IsReused);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConnectionLease_Dispose_AlwaysReleasesSemaphore()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                lease.Dispose();

                using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.IsNotNull(lease2.Connection);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConnectionLease_ReturnToPool_ThenDispose_DoesNotDisposeConnection()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                using var lease1 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                var conn = lease1.Connection;
                lease1.ReturnToPool();
                lease1.Dispose();

                using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.AreSame(conn, lease2.Connection);
                Assert.IsTrue(lease2.Connection.IsAlive);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConnectionLease_NoReturnToPool_ThenDispose_DisposesConnection()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool();

                var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                var conn = lease.Connection;
                lease.Dispose();

                Assert.IsFalse(conn.IsAlive);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConnectionLease_ExceptionPath_SemaphoreReleased()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                ConnectionLease lease = null;
                try
                {
                    lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                    throw new InvalidOperationException("simulate error");
                }
                catch (InvalidOperationException)
                {
                    lease?.Dispose();
                }

                using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.IsNotNull(lease2.Connection);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConnectionLease_DoubleDispose_Idempotent()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool();
                var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                lease.Dispose();
                Assert.DoesNotThrow(() => lease.Dispose());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PooledConnection_IsAlive_AfterDispose_ReturnsFalse()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool();
                using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                var conn = lease.Connection;
                conn.Dispose();
                Assert.IsFalse(conn.IsAlive);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConnectionLease_Dispose_AfterPoolDispose_DoesNotThrow()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                var pool = new TcpConnectionPool();
                var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                pool.Dispose();
                Assert.DoesNotThrow(() => lease.Dispose());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConnectionLease_ConcurrentReturnAndDispose_NoRace()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);

                var t1 = Task.Run(() => lease.ReturnToPool());
                var t2 = Task.Run(() => lease.Dispose());
                await Task.WhenAll(t1, t2);

                using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                Assert.IsNotNull(lease2.Connection);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void GetConnectionAsync_AfterPoolDispose_ThrowsObjectDisposedException()
        {
            using var server = new PassiveServer();
            var pool = new TcpConnectionPool();
            pool.Dispose();

            AssertAsync.ThrowsAsync<ObjectDisposedException>(async () =>
                await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None));
        }

        [Test]
        public void EnqueueConnection_AfterPoolDispose_DisposesConnection()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                var pool = new TcpConnectionPool();

                var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);
                var conn = lease.Connection;

                pool.Dispose();
                pool.EnqueueConnection(conn);

                Assert.IsFalse(conn.IsAlive);
                lease.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SemaphoreCapEviction_DrainsIdleConnections_BeforeRemoval()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                // Create an idle connection for eviction key
                using var lease1 = await pool.GetConnectionAsync("localhost", server.Port, false, CancellationToken.None);
                var conn = lease1.Connection;
                lease1.ReturnToPool();
                lease1.Dispose();

                var semField = typeof(TcpConnectionPool).GetField("_semaphores", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var idleField = typeof(TcpConnectionPool).GetField("_idleConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                var semaphores = (ConcurrentDictionary<string, SemaphoreSlim>)semField.GetValue(pool);
                var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);

                // Inflate semaphore count beyond cap with dummy entries that are not eligible for eviction
                for (int i = 0; i < 1000; i++)
                {
                    var sem = new SemaphoreSlim(1, 1);
                    sem.Wait(0); // currentCount = 0, not eligible for eviction
                    semaphores[$"dummy-{i}"] = sem;
                }

                // Ensure eviction candidate has idle connection queued
                var evictKey = $"{conn.Host}:{conn.Port}:{(conn.IsSecure ? "s" : "")}";
                Assert.IsTrue(idle.ContainsKey(evictKey));

                // Trigger eviction with a different current key
                using var lease2 = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);

                Assert.IsFalse(conn.IsAlive);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SemaphoreCapEviction_NeverEvictsCurrentKey()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                var semField = typeof(TcpConnectionPool).GetField("_semaphores", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var semaphores = (ConcurrentDictionary<string, SemaphoreSlim>)semField.GetValue(pool);

                for (int i = 0; i < 1000; i++)
                {
                    var sem = new SemaphoreSlim(1, 1);
                    sem.Wait(0);
                    semaphores[$"dummy-{i}"] = sem;
                }

                using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);

                var currentKey = $"127.0.0.1:{server.Port}:";
                Assert.IsTrue(semaphores.ContainsKey(currentKey));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SemaphoreCapEviction_DoesNotDisposeSemaphores()        {
            Task.Run(async () =>
            {
                using var server = new PassiveServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);

                var semField = typeof(TcpConnectionPool).GetField("_semaphores", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var semaphores = (ConcurrentDictionary<string, SemaphoreSlim>)semField.GetValue(pool);

                var tracked = new SemaphoreSlim(1, 1);
                semaphores["tracked"] = tracked; // eligible for eviction

                for (int i = 0; i < 1000; i++)
                {
                    var sem = new SemaphoreSlim(1, 1);
                    sem.Wait(0);
                    semaphores[$"dummy-{i}"] = sem;
                }

                using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, false, CancellationToken.None);

                bool waited = tracked.Wait(0);
                if (waited)
                    tracked.Release();
            }).GetAwaiter().GetResult();
        }

#if TURBOHTTP_INTEGRATION_TESTS
        [Test]
        [Category("Integration")]
        public void PooledConnection_TlsVersion_SetAfterTlsHandshake()
        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var pool = new TcpConnectionPool(
                    maxConnectionsPerHost: 1,
                    tlsBackend: TlsBackend.SslStream,
                    dnsTimeout: TimeSpan.FromSeconds(10));

                ConnectionLease lease = null;
                try
                {
                    try
                    {
                        lease = await pool.GetConnectionAsync("www.google.com", 443, true, cts.Token);
                    }
                    catch (Exception ex) when (IsNetworkEnvironmentIssue(ex))
                    {
                        Assert.Ignore($"TLS integration endpoint unavailable in this environment: {ex.GetType().Name}: {ex.Message}");
                        return;
                    }

                    Assert.IsNotNull(lease.Connection);
                    Assert.IsTrue(lease.Connection.IsSecure);
                    Assert.IsNotNull(lease.Connection.TlsVersion);
                    Assert.IsNotNull(lease.Connection.TlsProviderName);
                    Assert.That(lease.Connection.TlsVersion, Is.EqualTo("1.2").Or.EqualTo("1.3"));
                }
                finally
                {
                    lease?.Dispose();
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Category("Integration")]
        public void ITlsProvider_WrapAsync_ReturnsTlsResult_WithNegotiatedProtocol()
        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                Socket socket = null;
                NetworkStream stream = null;
                try
                {
                    try
                    {
                        socket = ConnectWithTimeout("www.google.com", 443, 10000);
                        stream = new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (IsNetworkEnvironmentIssue(ex))
                    {
                        Assert.Ignore($"TLS integration endpoint unavailable in this environment: {ex.GetType().Name}: {ex.Message}");
                        return;
                    }

                    var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
                    var wrapTask = provider.WrapAsync(stream, "www.google.com", new[] { "h2", "http/1.1" }, cts.Token);
                    var completed = await Task.WhenAny(wrapTask, Task.Delay(TimeSpan.FromSeconds(20), CancellationToken.None));
                    if (completed != wrapTask)
                    {
                        Assert.Ignore("TLS handshake exceeded timeout in this environment.");
                        return;
                    }

                    var result = await wrapTask;
                    Assert.IsNotNull(result.SecureStream);
                    Assert.AreEqual("SslStream", result.ProviderName);
                    Assert.That(result.NegotiatedAlpn, Is.EqualTo("h2").Or.EqualTo("http/1.1").Or.Null);
                    Assert.That(result.TlsVersion, Is.EqualTo("1.2").Or.EqualTo("1.3"));
                }
                finally
                {
                    try { stream?.Dispose(); } catch { }
                    try { socket?.Dispose(); } catch { }
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Category("Integration")]
        public void ITlsProvider_TlsBelowMinimum_ThrowsAuthenticationException()
        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                Socket socket = null;
                NetworkStream stream = null;
                try
                {
                    try
                    {
                        // badssl endpoint constrained to legacy TLS 1.0; should fail since provider enforces >= TLS1.2.
                        socket = ConnectWithTimeout("tls-v1-0.badssl.com", 1010, 10000);
                        stream = new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (IsNetworkEnvironmentIssue(ex))
                    {
                        Assert.Ignore($"Legacy TLS test endpoint unavailable in this environment: {ex.GetType().Name}: {ex.Message}");
                        return;
                    }

                    var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
                    var exThrown = AssertAsync.ThrowsAsync<AuthenticationException>(async () =>
                    {
                        await provider.WrapAsync(stream, "tls-v1-0.badssl.com", new[] { "http/1.1" }, cts.Token);
                    });
                    Assert.IsNotNull(exThrown);
                }
                finally
                {
                    try { stream?.Dispose(); } catch { }
                    try { socket?.Dispose(); } catch { }
                }
            }).GetAwaiter().GetResult();
        }

        private static Socket ConnectWithTimeout(string host, int port, int timeoutMs)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var ar = socket.BeginConnect(host, port, null, null);
            try
            {
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                    throw new TimeoutException($"Connect to {host}:{port} timed out after {timeoutMs}ms.");

                socket.EndConnect(ar);
                return socket;
            }
            finally
            {
                ar.AsyncWaitHandle.Close();
            }
        }

        private static bool IsNetworkEnvironmentIssue(Exception ex)
        {
            if (ex is TimeoutException || ex is SocketException || ex is IOException || ex is OperationCanceledException)
                return true;

            if (ex is UHttpException httpEx)
            {
                return httpEx.HttpError.Type == UHttpErrorType.Timeout
                    || httpEx.HttpError.Type == UHttpErrorType.NetworkError
                    || httpEx.HttpError.Type == UHttpErrorType.Cancelled;
            }

            return false;
        }
#endif
    }
}
