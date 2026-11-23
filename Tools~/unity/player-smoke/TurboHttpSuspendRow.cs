// #1132: the WebSocket keep-alive across an application suspension, on a real player. Runs only when the run
// asks for it (build-player.sh --suspend on android-il2cpp with --run: intent extra turbohttp_smoke_suspend=1;
// iOS by hand, testing.md); otherwise TurboHttpPlayerSmoke logs it SKIP.
//
// The row opens a ws:// connection with a short keep-alive (ping every 1 s, pong timeout 2 s) to a loopback
// echo server running inside this player, which answers each ping after PongDelay, so a ping is in flight
// about half of the time. Once the server holds a pong it logs ReadyLine; build-player.sh then backgrounds
// the app (KEYCODE_HOME) for longer than the pong timeout and brings it back. After the resume
// (OnApplicationPause(false), forwarded by TurboHttpPlayerSmoke) the row waits SettleTime, long enough for a
// pong timer that expired during the suspension to close the connection, and PASSes only when the
// connection is still Open and an echo round trip succeeds. A close with PongTimeout is the #1132 regression.
//
// The server is in the same process: an OS suspension freezes it together with the client, so the row
// exercises the process-wide ordering (timers that expired while frozen firing before the read completion
// carrying the pong), not network delay. max-ping-gap tells whether the process was frozen (the server saw no
// ping for about the background time) or kept running in the background (Android may not freeze a home-d app).
//
// Diagnostics read the library's internal keep-alive counters by reflection (best effort: "unknown" when a
// field is not found; the fields are written by the library itself, so UnityLinker keeps them):
// grace-waits (pong timeouts that entered the #1132 grace re-check), liveness-extensions (pong windows
// restarted because bytes arrived or the app resumed) and resume-signal (TurboHTTP.Unity.WebSocket's resume
// listener reported the resume to the keep-alive).
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.WebSocket;
using Stopwatch = System.Diagnostics.Stopwatch;

public static class TurboHttpSuspendRow
{
    public const string Id = "ws-suspend-resume";
    public const string Issue = "#1132";

    // build-player.sh --suspend backgrounds the app once this line is in logcat.
    public const string ReadyLine = "TURBOHTTP_SMOKE SUSPEND " + Id + " ready";

    private const string LoopbackHost = "127.0.0.1";

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(2);

    // Below PongTimeout with a margin: the keep-alive never times out while the process runs.
    private static readonly TimeSpan PongDelay = TimeSpan.FromSeconds(1);

    // From the ready line to the resume: the script backgrounds the app for about 10 s.
    private static readonly TimeSpan ResumeWait = TimeSpan.FromSeconds(180);

    // After the resume: twice PongTimeout plus its grace period (at most PongTimeout), so a deadline that
    // expired during the suspension has closed the connection by then if the grace path did not hold.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    // Written by TurboHttpPlayerSmoke.OnApplicationPause on the main thread, read by the row on the thread pool.
    private static int s_pauses;
    private static int s_resumes;
    private static long s_pausedAt;
    private static long s_resumedAt;
    private static int s_pingHeldAtPause = -1;
    private static long s_readyAt;
    private static LoopbackEchoServer s_server;

    /// <summary>Called from the smoke's MonoBehaviour for every OnApplicationPause.</summary>
    public static void ApplicationPaused(bool paused)
    {
        var now = Stopwatch.GetTimestamp();
        if (paused)
        {
            var server = Volatile.Read(ref s_server);
            if (server != null && Volatile.Read(ref s_pingHeldAtPause) < 0)
                Volatile.Write(ref s_pingHeldAtPause, server.PingHeld ? 1 : 0);
            Interlocked.Exchange(ref s_pausedAt, now);
            Interlocked.Increment(ref s_pauses);
        }
        else
        {
            Interlocked.Exchange(ref s_resumedAt, now);
            Interlocked.Increment(ref s_resumes);
        }
    }

    public static async Task<string> RunAsync(Action<string> log, CancellationToken ct)
    {
        // The smoke app's own policy for its in-process server: loopback cleartext is permitted, every other host
        // is still the platform's answer (Android refuses cleartext by default from API 28). Restored after the row.
        var platformPolicy = CleartextTrafficPolicy.PlatformPolicy;
        var loopbackExempted = !CleartextTrafficPolicy.IsPermitted(LoopbackHost, true);
        if (loopbackExempted)
            CleartextTrafficPolicy.PlatformPolicy = host => host == LoopbackHost || platformPolicy(host);

        var server = new LoopbackEchoServer(PongDelay);
        var events = new ClientEvents();
        var client = new WebSocketClient();
        client.OnError += events.Error;
        client.OnClosed += events.Closed;
        try
        {
            server.Start();
            var options = new WebSocketConnectionOptions
            {
                PingInterval = PingInterval,
                PongTimeout = PongTimeout,
                AllowCleartext = true,
            };
            await client.ConnectAsync(new Uri("ws://" + LoopbackHost + ":" + server.Port + "/"), options, ct)
                .ConfigureAwait(false);
            await EchoAsync(client, "before-suspend", ct).ConfigureAwait(false);

            // A keep-alive ping is in flight: the server holds its pong for PongDelay.
            await WhenAsync(server.FirstPingHeld, ct).ConfigureAwait(false);
            if (!await server.FirstPingHeld.ConfigureAwait(false))
                throw new InvalidOperationException("the connection ended before a keep-alive ping: " + Describe(client, events, server));
            // A player also gets OnApplicationPause(false) at startup: only a pause then a resume after this line count.
            var pausesAtReady = Volatile.Read(ref s_pauses);
            var resumesAtReady = Volatile.Read(ref s_resumes);
            Volatile.Write(ref s_pingHeldAtPause, -1);
            Volatile.Write(ref s_server, server);
            var readyAt = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref s_readyAt, readyAt);
            log(ReadyLine + " ping-in-flight pings=" + server.Pings + " port=" + server.Port);

            while (Volatile.Read(ref s_pauses) == pausesAtReady || Volatile.Read(ref s_resumes) == resumesAtReady ||
                Interlocked.Read(ref s_resumedAt) < Interlocked.Read(ref s_pausedAt))
            {
                if (Elapsed(readyAt) > ResumeWait)
                {
                    throw new TimeoutException("no OnApplicationPause(true) then (false) within " + ResumeWait.TotalSeconds +
                        " s of the ready line: the app was not backgrounded and resumed (" + Describe(client, events, server) + ")");
                }

                await Task.Delay(Poll, ct).ConfigureAwait(false);
            }

            var resumedAt = Interlocked.Read(ref s_resumedAt);
            while (Elapsed(resumedAt) < SettleTime && client.State == WebSocketState.Open)
                await Task.Delay(Poll, ct).ConfigureAwait(false);

            if (client.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("connection not Open after the resume (#1132: " +
                    (events.PongTimeout ? "closed with PongTimeout" : "closed") + "): " + Describe(client, events, server));
            }

            await EchoAsync(client, "after-resume", ct).ConfigureAwait(false);
            if (client.State != WebSocketState.Open)
                throw new InvalidOperationException("connection closed after the echo: " + Describe(client, events, server));

            return "state=Open echo=ok " + Describe(client, events, server) +
                " cleartext=" + (loopbackExempted ? "loopback-exempted" : "permitted");
        }
        finally
        {
            Volatile.Write(ref s_server, null);
            Interlocked.Exchange(ref s_readyAt, 0);
            client.Dispose();
            await server.StopAsync().ConfigureAwait(false);
            if (loopbackExempted)
                CleartextTrafficPolicy.PlatformPolicy = platformPolicy;
        }
    }

    private static async Task EchoAsync(WebSocketClient client, string text, CancellationToken ct)
    {
        await client.SendAsync(text, ct).ConfigureAwait(false);
        using (var message = await client.ReceiveAsync(ct).ConfigureAwait(false))
        {
            if (!message.IsText || message.Text != text)
                throw new InvalidOperationException("echo of '" + text + "' came back as " + message.Type + " '" + message.Text + "'");
        }
    }

    private static async Task WhenAsync(Task task, CancellationToken ct)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => cancelled.TrySetCanceled()))
        {
            await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    private static TimeSpan Elapsed(long since)
    {
        return TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency);
    }

    // One line of what the pause/resume, the server and the library's keep-alive saw.
    private static string Describe(WebSocketClient client, ClientEvents events, LoopbackEchoServer server)
    {
        var pausedAt = Interlocked.Read(ref s_pausedAt);
        var resumedAt = Interlocked.Read(ref s_resumedAt);
        var background = pausedAt != 0 && resumedAt > pausedAt
            ? ((resumedAt - pausedAt) / (double)Stopwatch.Frequency).ToString("F1") + "s"
            : "n/a";
        var inFlight = Volatile.Read(ref s_pingHeldAtPause);
        var text = "state=" + client.State +
            " pauses=" + Volatile.Read(ref s_pauses) + " resumes=" + Volatile.Read(ref s_resumes) +
            " background=" + background +
            " ping-in-flight-at-pause=" + (inFlight < 0 ? "n/a" : inFlight == 1 ? "yes" : "no") +
            " max-ping-gap=" + server.MaxPingGap.TotalSeconds.ToString("F1") + "s pings=" + server.Pings +
            " " + KeepAliveCounters(client);
        if (events.Description != null)
            text += " event=" + events.Description;
        if (server.Error != null)
            text += " server-error=" + server.Error;
        return text.Replace('\n', ' ').Replace('\r', ' ');
    }

    // Reads WebSocketClient._connection and WebSocketConnection's keep-alive counters (internal, #1132) by
    // reflection; diagnostics only, so a missing field reads "unknown" instead of failing the row.
    private static string KeepAliveCounters(WebSocketClient client)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            var connectionField = typeof(WebSocketClient).GetField("_connection", instance);
            var connection = connectionField?.GetValue(client);
            var type = connectionField?.FieldType;
            var grace = type?.GetField("_keepAlivePongGraceWaits", instance);
            var extensions = type?.GetField("_keepAliveLivenessExtensions", instance);
            var resumed = type?.GetField("s_applicationResumedTimestamp", BindingFlags.Static | BindingFlags.NonPublic);
            // The library's last resume timestamp, set by its own OnApplicationPause(false) listener: after the ready line?
            var readyAt = Interlocked.Read(ref s_readyAt);
            var signal = resumed == null ? "unknown"
                : readyAt == 0 ? "n/a"
                : (long)resumed.GetValue(null) >= readyAt ? "yes" : "no";
            return "grace-waits=" + Counter(grace, connection) +
                " liveness-extensions=" + Counter(extensions, connection) + " resume-signal=" + signal;
        }
        catch (Exception ex)
        {
            return "keep-alive-counters=unknown(" + ex.GetType().Name + ")";
        }
    }

    private static string Counter(FieldInfo field, object connection)
    {
        return field == null || connection == null ? "unknown" : ((long)field.GetValue(connection)).ToString();
    }

    // The client's error and close events (raised on library threads).
    private sealed class ClientEvents
    {
        private readonly object _gate = new object();
        private string _description;
        private bool _pongTimeout;

        public string Description
        {
            get
            {
                lock (_gate)
                    return _description;
            }
        }

        public bool PongTimeout
        {
            get
            {
                lock (_gate)
                    return _pongTimeout;
            }
        }

        public void Error(WebSocketException ex)
        {
            lock (_gate)
            {
                _pongTimeout |= ex.Error == WebSocketError.PongTimeout;
                _description = Append(_description, "error:" + ex.Error + ":" + ex.Message);
            }
        }

        public void Closed(WebSocketCloseCode code, string reason)
        {
            lock (_gate)
                _description = Append(_description, "closed:" + code + (string.IsNullOrEmpty(reason) ? string.Empty : ":" + reason));
        }

        private static string Append(string text, string item)
        {
            var next = text == null ? item : text + "," + item;
            return next.Length <= 300 ? next : next.Substring(0, 300);
        }
    }

    // A one-connection RFC 6455 echo server on 127.0.0.1: echoes text and binary messages, answers each ping
    // after the pong delay (RFC 6455 5.5.3: same payload), answers a close and ends. Bounded: an 8 KiB handshake
    // and 64 KiB frames; fragments, unmasked client frames and unknown opcodes end the connection.
    private sealed class LoopbackEchoServer
    {
        private const int MaxHandshakeBytes = 8 * 1024;
        private const int MaxPayloadBytes = 64 * 1024;
        private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly TimeSpan _pongDelay;
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        // True once a ping is held; false when the connection ended before one.
        private readonly TaskCompletionSource<bool> _firstPingHeld =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private Task _serveTask = Task.CompletedTask;

        // The serve loop writes these; the row and the main thread read them.
        private int _pingHeld;
        private int _pings;
        private long _lastPingAt;
        private long _maxPingGapTicks;
        private string _error;

        public LoopbackEchoServer(TimeSpan pongDelay)
        {
            _pongDelay = pongDelay;
        }

        public int Port { get; private set; }

        public Task<bool> FirstPingHeld => _firstPingHeld.Task;

        public bool PingHeld => Volatile.Read(ref _pingHeld) != 0;

        public int Pings => Volatile.Read(ref _pings);

        public TimeSpan MaxPingGap =>
            TimeSpan.FromSeconds(Interlocked.Read(ref _maxPingGapTicks) / (double)Stopwatch.Frequency);

        public string Error => Volatile.Read(ref _error);

        public void Start()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serveTask = ServeAsync(_cts.Token);
        }

        // Ends the serve loop (closing the listener and the connection) and waits for it; never throws.
        public async Task StopAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            await _serveTask.ConfigureAwait(false);
            _cts.Dispose();
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            TcpClient tcp = null;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                tcp.NoDelay = true;
                using (ct.Register(tcp.Dispose))
                {
                    var stream = tcp.GetStream();
                    await HandshakeAsync(stream, ct).ConfigureAwait(false);
                    await FramesAsync(stream, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Volatile.Write(ref _error, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                tcp?.Dispose();
                Volatile.Write(ref _pingHeld, 0);
                _firstPingHeld.TrySetResult(false);
            }
        }

        private static async Task HandshakeAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[MaxHandshakeBytes];
            var length = 0;
            while (true)
            {
                if (length == buffer.Length)
                    throw new InvalidDataException("handshake over " + MaxHandshakeBytes + " bytes");
                var read = await stream.ReadAsync(buffer, length, buffer.Length - length, ct).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("connection closed during the handshake");
                length += read;
                if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n"))
                    break;
            }

            string key = null;
            foreach (var line in Encoding.ASCII.GetString(buffer, 0, length).Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                var colon = line.IndexOf(':');
                if (colon > 0 && string.Equals(line.Substring(0, colon).Trim(), "Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                    key = line.Substring(colon + 1).Trim();
            }

            if (string.IsNullOrEmpty(key))
                throw new InvalidDataException("no Sec-WebSocket-Key");

            string accept;
            using (var sha1 = SHA1.Create())
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + AcceptGuid)));
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
            await stream.WriteAsync(response, 0, response.Length, ct).ConfigureAwait(false);
        }

        // The only writer of the stream: echoes, pongs and the close reply are written in order from here.
        private async Task FramesAsync(NetworkStream stream, CancellationToken ct)
        {
            var header = new byte[8];
            var mask = new byte[4];
            while (true)
            {
                await ReadExactAsync(stream, header, 2, ct).ConfigureAwait(false);
                var fin = (header[0] & 0x80) != 0;
                var opcode = header[0] & 0x0F;
                var masked = (header[1] & 0x80) != 0;
                long length = header[1] & 0x7F;
                if (length == 126)
                {
                    await ReadExactAsync(stream, header, 2, ct).ConfigureAwait(false);
                    length = (header[0] << 8) | header[1];
                }
                else if (length == 127)
                {
                    await ReadExactAsync(stream, header, 8, ct).ConfigureAwait(false);
                    length = 0;
                    for (var i = 0; i < 8; i++)
                        length = (length << 8) | header[i];
                }

                if (!fin || !masked || length < 0 || length > MaxPayloadBytes)
                    throw new InvalidDataException("unsupported frame: fin=" + fin + " masked=" + masked + " length=" + length);

                await ReadExactAsync(stream, mask, 4, ct).ConfigureAwait(false);
                var payload = new byte[length];
                await ReadExactAsync(stream, payload, payload.Length, ct).ConfigureAwait(false);
                for (var i = 0; i < payload.Length; i++)
                    payload[i] ^= mask[i & 3];

                switch (opcode)
                {
                    case 0x1:
                    case 0x2:
                        await WriteFrameAsync(stream, opcode, payload, ct).ConfigureAwait(false);
                        break;
                    case 0x9:
                        RecordPing();
                        Volatile.Write(ref _pingHeld, 1);
                        _firstPingHeld.TrySetResult(true);
                        await Task.Delay(_pongDelay, ct).ConfigureAwait(false);
                        await WriteFrameAsync(stream, 0xA, payload, ct).ConfigureAwait(false);
                        Volatile.Write(ref _pingHeld, 0);
                        break;
                    case 0xA:
                        break;
                    case 0x8:
                        await WriteFrameAsync(stream, 0x8, payload, ct).ConfigureAwait(false);
                        return;
                    default:
                        throw new InvalidDataException("unexpected opcode 0x" + opcode.ToString("X"));
                }
            }
        }

        private void RecordPing()
        {
            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Exchange(ref _lastPingAt, now);
            if (last != 0 && now - last > Interlocked.Read(ref _maxPingGapTicks))
                Interlocked.Exchange(ref _maxPingGapTicks, now - last);
            Interlocked.Increment(ref _pings);
        }

        private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(buffer, offset, count - offset, ct).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("connection closed mid-frame");
                offset += read;
            }
        }

        // Server frames are unmasked (RFC 6455 5.1); every payload here is under 64 KiB.
        private static Task WriteFrameAsync(NetworkStream stream, int opcode, byte[] payload, CancellationToken ct)
        {
            var headerLength = payload.Length < 126 ? 2 : 4;
            var frame = new byte[headerLength + payload.Length];
            frame[0] = (byte)(0x80 | opcode);
            if (payload.Length < 126)
            {
                frame[1] = (byte)payload.Length;
            }
            else
            {
                frame[1] = 126;
                frame[2] = (byte)(payload.Length >> 8);
                frame[3] = (byte)payload.Length;
            }

            Buffer.BlockCopy(payload, 0, frame, headerLength, payload.Length);
            return stream.WriteAsync(frame, 0, frame.Length, ct);
        }
    }
}
