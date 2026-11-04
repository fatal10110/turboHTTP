using System;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TurboHTTP.Tools.Soak
{
    /// <summary>
    /// One fault profile of the user-space proxy. A TCP forwarder cannot drop single packets (the kernel
    /// retransmits below it), so "loss" here is what loss looks like to the application: a stalled byte
    /// stream (retransmission timeouts) or a connection reset. Kernel <c>tc netem</c> is not used.
    /// </summary>
    internal sealed class FaultProfile
    {
        public string Name;
        /// <summary>One-way delay added to every forwarded chunk, plus up to <see cref="JitterMs"/>.</summary>
        public int LatencyMs;
        public int JitterMs;
        /// <summary>Per-direction, per-connection bandwidth cap; 0 = none.</summary>
        public long BytesPerSecond;
        /// <summary>Chance per forwarded chunk that the direction stalls for StallMinMs..StallMaxMs.</summary>
        public double StallChance;
        public int StallMinMs;
        public int StallMaxMs;
        /// <summary>Chance per connection that it is reset (RST) after ResetMinBytes..ResetMaxBytes forwarded.</summary>
        public double ResetChance;
        public int ResetMinBytes;
        public int ResetMaxBytes;

        /// <summary>Whether request failures on this profile are an expected outcome of the injected fault.</summary>
        public bool InjectsFailures => ResetChance > 0;

        public static FaultProfile[] All() => new[]
        {
            new FaultProfile { Name = "latency", LatencyMs = 40, JitterMs = 10 },
            new FaultProfile { Name = "bandwidth", LatencyMs = 5, BytesPerSecond = 2 * 1024 * 1024 },
            new FaultProfile { Name = "lossy", LatencyMs = 10, JitterMs = 5, StallChance = 0.02, StallMinMs = 100, StallMaxMs = 600 },
            // Every connection is reset once it has forwarded its random budget, so long-lived HTTP/2
            // connections are torn down with streams in flight again and again, not just once.
            new FaultProfile { Name = "reset", LatencyMs = 2, ResetChance = 1.0, ResetMinBytes = 16 * 1024, ResetMaxBytes = 4 * 1024 * 1024 },
        };
    }

    /// <summary>
    /// Fault-injecting TCP forwarder (loopback only). It forwards ciphertext for TLS upstreams: it never
    /// terminates TLS, so it cannot weaken certificate validation.
    /// </summary>
    internal sealed class FaultProxy : IDisposable
    {
        private const int ChunkBytes = 16 * 1024;
        private readonly FaultProfile _profile;
        private readonly IPEndPoint _upstream;
        private readonly TcpListener _listener;
        private readonly Random _seeds;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private int _open;
        private long _accepted;
        private long _resets;

        public FaultProxy(FaultProfile profile, IPEndPoint upstream, int seed)
        {
            _profile = profile;
            _upstream = upstream;
            _seeds = new Random(seed);
            _listener = new TcpListener(IPAddress.Loopback, 0);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Open => Volatile.Read(ref _open);
        public long Accepted => Interlocked.Read(ref _accepted);
        public long Resets => Interlocked.Read(ref _resets);

        public void Start()
        {
            _listener.Start(512);
            _ = AcceptLoopAsync();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }

                int seed;
                lock (_seeds)
                    seed = _seeds.Next();
                Interlocked.Increment(ref _accepted);
                Interlocked.Increment(ref _open);
                _ = HandleAsync(client, new Random(seed));
            }
        }

        private async Task HandleAsync(Socket client, Random random)
        {
            Socket upstream = null;
            var link = new Link();
            try
            {
                client.NoDelay = true;
                upstream = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await upstream.ConnectAsync(_upstream, _cts.Token).ConfigureAwait(false);
                link.Client = client;
                link.Upstream = upstream;
                if (random.NextDouble() < _profile.ResetChance)
                    link.ResetBudget = random.Next(_profile.ResetMinBytes, _profile.ResetMaxBytes + 1);

                int seedA = random.Next();
                int seedB = random.Next();
                var up = PumpAsync(client, upstream, link, new Random(seedA));
                var down = PumpAsync(upstream, client, link, new Random(seedB));
                await Task.WhenAll(up, down).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A failed upstream connect or a torn pump: both sockets close below.
            }
            finally
            {
                if (link.TryReset())
                    Interlocked.Increment(ref _resets);
                CloseQuietly(client, abortive: link.WasReset);
                CloseQuietly(upstream, abortive: link.WasReset);
                Interlocked.Decrement(ref _open);
            }
        }

        /// <summary>Forwards one direction: a reader stamps each chunk with its due time, a writer delivers it.</summary>
        private async Task PumpAsync(Socket from, Socket to, Link link, Random random)
        {
            var channel = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
            var writer = WriteLoopAsync(to, channel.Reader, link, random);
            long lastDue = 0;
            try
            {
                while (!link.Closed)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
                    int n;
                    try
                    {
                        n = await from.ReceiveAsync(buffer.AsMemory(0, ChunkBytes), SocketFlags.None, _cts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        throw;
                    }

                    if (n == 0)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        break;
                    }

                    long delay = _profile.LatencyMs + (_profile.JitterMs > 0 ? random.Next(_profile.JitterMs + 1) : 0);
                    long due = Math.Max(lastDue, Stopwatch.GetTimestamp() + delay * Stopwatch.Frequency / 1000);
                    lastDue = due;
                    await channel.Writer.WriteAsync(new Chunk(buffer, n, due), _cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                link.Close();
            }
            finally
            {
                channel.Writer.TryComplete();
            }

            await writer.ConfigureAwait(false);
        }

        private async Task WriteLoopAsync(Socket to, ChannelReader<Chunk> reader, Link link, Random random)
        {
            long allowedAt = 0;
            try
            {
                await foreach (var chunk in reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    try
                    {
                        if (link.Closed)
                            continue;

                        await DelayUntilAsync(chunk.Due).ConfigureAwait(false);
                        if (_profile.StallChance > 0 && random.NextDouble() < _profile.StallChance)
                            await Task.Delay(random.Next(_profile.StallMinMs, _profile.StallMaxMs + 1), _cts.Token).ConfigureAwait(false);

                        if (link.CountAndCheckReset(chunk.Length))
                        {
                            link.Close();
                            continue;
                        }

                        int sent = 0;
                        while (sent < chunk.Length)
                            sent += await to.SendAsync(chunk.Buffer.AsMemory(sent, chunk.Length - sent), SocketFlags.None, _cts.Token).ConfigureAwait(false);

                        if (_profile.BytesPerSecond > 0)
                        {
                            long now = Stopwatch.GetTimestamp();
                            allowedAt = Math.Max(allowedAt, now) + chunk.Length * Stopwatch.Frequency / _profile.BytesPerSecond;
                            await DelayUntilAsync(allowedAt).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(chunk.Buffer);
                    }
                }

                if (!link.Closed)
                    to.Shutdown(SocketShutdown.Send); // pass the peer's FIN on
            }
            catch (Exception)
            {
                link.Close();
                while (reader.TryRead(out var rest))
                    ArrayPool<byte>.Shared.Return(rest.Buffer);
            }
        }

        private async Task DelayUntilAsync(long timestamp)
        {
            long remaining = timestamp - Stopwatch.GetTimestamp();
            if (remaining > Stopwatch.Frequency / 1000)
                await Task.Delay(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency), _cts.Token).ConfigureAwait(false);
        }

        private static void CloseQuietly(Socket socket, bool abortive)
        {
            if (socket == null)
                return;
            try
            {
                if (abortive)
                    socket.LingerState = new LingerOption(true, 0); // close sends RST
                socket.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private readonly struct Chunk
        {
            public readonly byte[] Buffer;
            public readonly int Length;
            public readonly long Due;

            public Chunk(byte[] buffer, int length, long due)
            {
                Buffer = buffer;
                Length = length;
                Due = due;
            }
        }

        /// <summary>State shared by the two directions of one proxied connection.</summary>
        private sealed class Link
        {
            public Socket Client;
            public Socket Upstream;
            public long ResetBudget = -1;
            private long _forwarded;
            private int _closed;
            private int _reset;

            public bool Closed => Volatile.Read(ref _closed) != 0;
            public bool WasReset => Volatile.Read(ref _reset) != 0;

            /// <summary>True once the forwarded total crosses the reset budget: the connection is then reset.</summary>
            public bool CountAndCheckReset(int bytes)
            {
                long total = Interlocked.Add(ref _forwarded, bytes);
                if (ResetBudget < 0 || total <= ResetBudget)
                    return false;
                Interlocked.Exchange(ref _reset, 1);
                return true;
            }

            /// <summary>Marks a reset that <see cref="CountAndCheckReset"/> decided; true once per connection.</summary>
            public bool TryReset() => WasReset && Interlocked.Exchange(ref ResetBudget, long.MinValue) != long.MinValue;

            public void Close()
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0)
                    return;
                // Wake the other direction's pending receive: an abortive close sends RST to both peers.
                foreach (var socket in new[] { Client, Upstream })
                {
                    try
                    {
                        if (socket == null)
                            continue;
                        if (WasReset)
                        {
                            socket.LingerState = new LingerOption(true, 0);
                            socket.Close(); // RST now, not FIN
                        }
                        else
                        {
                            socket.Shutdown(SocketShutdown.Both);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
    }
}
