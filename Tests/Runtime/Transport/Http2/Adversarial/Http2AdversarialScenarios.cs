using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2.Adversarial
{
    /// <summary>
    /// Scripted adversarial-server scenarios for the HTTP/2 client (#111): each one drives a real
    /// <see cref="Http2Connection"/> with frame sequences a hostile or unusual server sends and checks the
    /// RFC 9113 / RFC 7541 outcome. They run as NUnit tests (Http2AdversarialServerTests, host-editor run)
    /// and as rows of the h2-interop CI job (Tools~/h2-interop). Deterministic: no wall clock, fixed seeds;
    /// the token is only a watchdog.
    /// </summary>
    internal static class Http2AdversarialScenarios
    {
        public static readonly IReadOnlyList<(string Name, Func<CancellationToken, Task> Run)> All =
            new (string, Func<CancellationToken, Task>)[]
            {
                ("hpack-sync/cancelled-stream-header-block", HpackSyncCancelledStreamHeaderBlockAsync),
                ("continuation/flood", ContinuationFloodAsync),
                ("continuation/huge-header-block", HugeHeaderBlockAsync),
                ("hpack/bomb-indexed-references", HpackBombAsync),
                ("hpack/table-size-churn", TableSizeChurnAsync),
                ("hpack/size-update-after-field", SizeUpdateAfterFieldAsync),
                ("hpack/size-update-above-settings", SizeUpdateAboveSettingsAsync),
                ("goaway/in-flight-posts-drain", GoAwayInFlightPostsAsync),
                ("goaway/refused-stream-burst", RefusedStreamBurstAsync),
                ("flow/initial-window-decrease-mid-upload", InitialWindowDecreaseMidUploadAsync),
                ("flow/reset-stream-data-returns-connection-credit", ResetStreamDataReturnsCreditAsync),
                ("streams/max-concurrent-lowered-random-cancel", MaxConcurrentLoweredRandomCancelAsync),
                ("body/content-length-short", ct => ContentLengthMismatchAsync("10", "abcd", ct)),
                ("body/content-length-long", ct => ContentLengthMismatchAsync("2", "abcdef", ct)),
                ("control/ping-and-settings-flood", ControlFrameFloodAsync),
            };

        public static IEnumerable<string> Names
        {
            get
            {
                foreach (var (name, _) in All)
                    yield return name;
            }
        }

        public static Func<CancellationToken, Task> Get(string name)
        {
            foreach (var (n, run) in All)
            {
                if (n == name)
                    return run;
            }

            throw new ArgumentException("unknown scenario " + name, nameof(name));
        }

        // a. RFC 9113 Section 4.3 / 5.1: a header block on a stream the client reset is still decoded, so the
        // dynamic-table entry it inserts resolves in the next response.
        private static async Task HpackSyncCancelledStreamHeaderBlockAsync(CancellationToken ct)
        {
            using var s = await ScriptedHttp2Server.StartAsync(ct);
            using var cancelA = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var a = s.GetAsync("/a", cancelA.Token);
            int aId = await s.NextRequestAsync(ct);
            cancelA.Cancel();
            await ScenarioAssert.ThrowsAsync<OperationCanceledException>(a, "cancelled request");
            ScenarioAssert.Equal(Http2ErrorCode.Cancel, await s.ExpectResetAsync(aId, ct), "RST_STREAM code of the cancelled stream");

            const string value = "inserted-by-a-cancelled-stream";
            var late = new HpackBlock().Status(200).LiteralIncremental("x-sync", value).Literal("x-pad", new string('p', 40));
            await s.WriteHeadersAsync(aId, late.ToArray(), endStream: false, ct, fragment: 7);
            await s.WriteDataAsync(aId, Encoding.ASCII.GetBytes("discarded"), endStream: true, ct);

            var b = s.GetAsync("/b", ct);
            int bId = await s.NextRequestAsync(ct);
            await s.WriteHeadersAsync(bId, new HpackBlock().Status(200).Indexed(HpackBlock.FirstDynamicIndex).ToArray(), endStream: true, ct);
            using (var response = await b)
                ScenarioAssert.Equal(value, response.Headers.Get("x-sync"), "x-sync from the entry the reset stream inserted");

            await AssertUsableAsync(s, "/after", ct);
        }

        // b. More CONTINUATION frames than the per-block cap: ENHANCE_YOUR_CALM, not an unbounded loop.
        private static async Task ContinuationFloodAsync(CancellationToken ct)
        {
            using var s = await ScriptedHttp2Server.StartAsync(ct);
            var request = s.GetAsync("/flood", ct);
            int id = await s.NextRequestAsync(ct);
            await s.WriteFrameAsync(Http2FrameType.Headers, Http2FrameFlags.None, id, new HpackBlock().Status(200).ToArray(), ct);
            for (int i = 0; i <= Http2Connection.MaxContinuationFramesPerHeaderBlock; i++)
                await s.WriteFrameAsync(Http2FrameType.Continuation, Http2FrameFlags.None, id, Array.Empty<byte>(), ct);

            var goaway = await s.ExpectGoAwayAsync(ct);
            ScenarioAssert.Equal(Http2ErrorCode.EnhanceYourCalm, goaway.Code, "GOAWAY code for a CONTINUATION flood");
            await ScenarioAssert.ThrowsAsync<UHttpException>(request, "request on the flooded connection");
        }

        // b. A header block larger than the buffered-block limit (default options: 64 KB) ends the connection.
        private static async Task HugeHeaderBlockAsync(CancellationToken ct)
        {
            using var s = await ScriptedHttp2Server.StartAsync(ct);
            var request = s.GetAsync("/huge", ct);
            int id = await s.NextRequestAsync(ct);
            await s.WriteFrameAsync(Http2FrameType.Headers, Http2FrameFlags.None, id, new HpackBlock().Status(200).ToArray(), ct);
            var chunk = new byte[4096];
            for (int sent = 0; sent <= 64 * 1024; sent += chunk.Length)
                await s.WriteFrameAsync(Http2FrameType.Continuation, Http2FrameFlags.None, id, chunk, ct);

            var goaway = await s.ExpectGoAwayAsync(ct);
            ScenarioAssert.Equal(Http2ErrorCode.EnhanceYourCalm, goaway.Code, "GOAWAY code for an oversize header block");
            await ScenarioAssert.ThrowsAsync<UHttpException>(request, "request with the oversize header block");
        }

        // c. HPACK bomb (RFC 7541 Section 7.3): 101 bytes referencing one 4 KB dynamic entry 100 times decode to
        // about 400 KB; the decoded-size cap stops it with COMPRESSION_ERROR.
        private static async Task HpackBombAsync(CancellationToken ct)
        {
            using var s = await ScriptedHttp2Server.StartAsync(ct);
            string big = new string('b', 4000);
            var seed = s.GetAsync("/seed", ct);
            int seedId = await s.NextRequestAsync(ct);
            await s.WriteHeadersAsync(seedId, new HpackBlock().Status(200).LiteralIncremental("x-bomb", big).ToArray(), endStream: true, ct);
            using (var response = await seed)
                ScenarioAssert.Equal(big, response.Headers.Get("x-bomb"), "seed entry");

            var bombed = s.GetAsync("/bomb", ct);
            int bombId = await s.NextRequestAsync(ct);
            var bomb = new HpackBlock().Status(200);
            for (int i = 0; i < 100; i++)
                bomb.Indexed(HpackBlock.FirstDynamicIndex);
            await s.WriteHeadersAsync(bombId, bomb.ToArray(), endStream: true, ct);

            var goaway = await s.ExpectGoAwayAsync(ct);
            ScenarioAssert.Equal(Http2ErrorCode.CompressionError, goaway.Code, "GOAWAY code for an HPACK bomb");
            await ScenarioAssert.ThrowsAsync<UHttpException>(bombed, "request answered with the bomb");
        }

        // c. Dynamic table size updates mid-connection (RFC 7541 Sections 4.2-4.4, 6.3): two updates in one
        // block, shrink with eviction, clear with an oversize insert, grow again; every reference resolves.
        private static async Task TableSizeChurnAsync(CancellationToken ct)
        {
            using var s = await ScriptedHttp2Server.StartAsync(ct);

            using (var r = await ExchangeAsync(s, "/1", new HpackBlock().SizeUpdate(0).SizeUpdate(4096).Status(200)
                       .LiteralIncremental("x-gen", "1"), ct))
                ScenarioAssert.Equal("1", r.Headers.Get("x-gen"), "r1 x-gen");

            // 64 bytes keep x-gen (38); inserting x-gen2 (39) evicts it.
            using (var r = await ExchangeAsync(s, "/2", new HpackBlock().SizeUpdate(64).Status(200)
                       .Indexed(HpackBlock.FirstDynamicIndex).LiteralIncremental("x-gen2", "2"), ct))
            {
                ScenarioAssert.Equal("1", r.Headers.Get("x-gen"), "r2 x-gen from the shrunk table");
                ScenarioAssert.Equal("2", r.Headers.Get("x-gen2"), "r2 x-gen2");
            }

            using (var r = await ExchangeAsync(s, "/3", new HpackBlock().Status(200).Indexed(HpackBlock.FirstDynamicIndex), ct))
            {
                ScenarioAssert.Equal("2", r.Headers.Get("x-gen2"), "r3 index 62 after eviction");
                ScenarioAssert.Equal(null, r.Headers.Get("x-gen"), "r3 evicted x-gen");
            }

            // Size 0 empties the table; an entry larger than the table is not an error (Section 4.4).
            using (var r = await ExchangeAsync(s, "/4", new HpackBlock().SizeUpdate(0).Status(200).LiteralIncremental("x-gen3", "3"), ct))
                ScenarioAssert.Equal("3", r.Headers.Get("x-gen3"), "r4 x-gen3");

            using (var r = await ExchangeAsync(s, "/5", new HpackBlock().SizeUpdate(4096).Status(200)
                       .LiteralIncremental("x-gen4", "4").LiteralIncremental("x-gen5", "5"), ct))
                ScenarioAssert.Equal("5", r.Headers.Get("x-gen5"), "r5 x-gen5");

            using (var r = await ExchangeAsync(s, "/6", new HpackBlock().Status(200)
                       .Indexed(HpackBlock.FirstDynamicIndex).Indexed(HpackBlock.FirstDynamicIndex + 1), ct))
            {
                ScenarioAssert.Equal("5", r.Headers.Get("x-gen5"), "r6 index 62");
                ScenarioAssert.Equal("4", r.Headers.Get("x-gen4"), "r6 index 63");
            }

            await AssertUsableAsync(s, "/after", ct);
        }

        // c. RFC 7541 Section 4.2: a size update after the first field is a decoding error.
        private static Task SizeUpdateAfterFieldAsync(CancellationToken ct) =>
            ExpectCompressionErrorAsync(new HpackBlock().Status(200).SizeUpdate(100), ct);

        // c. RFC 7541 Section 6.3: a size update above SETTINGS_HEADER_TABLE_SIZE (4096) is a decoding error.
        private static Task SizeUpdateAboveSettingsAsync(CancellationToken ct) =>
            ExpectCompressionErrorAsync(new HpackBlock().SizeUpdate(8192).Status(200), ct);

        // d. RFC 9113 Section 6.8 graceful shutdown with in-flight POSTs: GOAWAY(2^31-1), then the real last id,
        // then a raised id that must be ignored. Streams at or below it drain; the rest fail as not processed.
        private static async Task GoAwayInFlightPostsAsync(CancellationToken ct)
        {
            using var s = await ScriptedHttp2Server.StartAsync(ct);
            var body = Pattern(4096, 3);
            var posts = new Task<UHttpResponse>[3];
            for (int i = 0; i < posts.Length; i++)
                posts[i] = s.SendAsync(HttpMethod.POST, "/upload/" + i, body, ct);
            await s.WaitForAsync(() => s.EndedByClient.Count == posts.Length, "three complete uploads", ct);

            await s.WriteGoAwayAsync(int.MaxValue, Http2ErrorCode.NoError, ct);
            await s.SyncAsync(1, ct);
            await ExpectNotProcessedAsync(s.SendAsync(HttpMethod.POST, "/late", body, ct), "POST after GOAWAY");

            // The two lowest streams drain; the highest is above the new last-stream-id.
            var ids = new List<int>(s.RequestStreams);
            ids.Sort();
            await s.WriteGoAwayAsync(ids[1], Http2ErrorCode.NoError, ct);
            await ExpectNotProcessedAsync(posts[UploadIndex(s, ids[2])], "POST above the GOAWAY last-stream-id");
            await s.WriteGoAwayAsync(int.MaxValue, Http2ErrorCode.NoError, ct);
            await s.SyncAsync(2, ct);
            await ExpectNotProcessedAsync(s.SendAsync(HttpMethod.POST, "/late2", body, ct), "POST after a raised GOAWAY");

            for (int k = 0; k < 2; k++)
            {
                int i = UploadIndex(s, ids[k]);
                ScenarioAssert.Equal(body.Length, (int)s.RequestBodies[ids[k]].Length, "uploaded bytes of POST " + i);
                await s.RespondAsync(ids[k], 200, "done-" + i, ct);
                using var response = await posts[i];
                ScenarioAssert.Equal("done-" + i, response.GetBodyAsString(), "drained POST " + i);
            }

            ScenarioAssert.Equal(3, s.RequestStreams.Count, "streams opened after GOAWAY");
        }

        // d. RFC 9113 Section 8.7: REFUSED_STREAM on the first streams of a burst fails only those, as not processed.
        private static async Task RefusedStreamBurstAsync(CancellationToken ct)
        {
            using var s = await ScriptedHttp2Server.StartAsync(ct);
            var tasks = new Task<UHttpResponse>[6];
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = s.GetAsync("/burst/" + i, ct);
            await s.WaitForAsync(() => s.RequestStreams.Count == tasks.Length, "the burst", ct);

            var ids = new List<int>(s.RequestStreams);
            ids.Sort();
            for (int i = 0; i < 2; i++)
                await s.WriteResetAsync(ids[i], Http2ErrorCode.RefusedStream, ct);
            for (int i = 2; i < ids.Count; i++)
                await s.RespondAsync(ids[i], 200, "ok-" + s.PathOf(ids[i]), ct);

            for (int i = 0; i < tasks.Length; i++)
            {
                int id = s.StreamOf("/burst/" + i);
                if (id == ids[0] || id == ids[1])
                {
                    await ExpectNotProcessedAsync(tasks[i], "refused /burst/" + i);
                    continue;
                }

                using var response = await tasks[i];
                ScenarioAssert.Equal("ok-/burst/" + i, response.GetBodyAsString(), "/burst/" + i);
            }

            await AssertUsableAsync(s, "/after", ct);
        }

        // e. RFC 9113 Section 6.9.2: SETTINGS_INITIAL_WINDOW_SIZE lowered mid-upload drives the stream window
        // negative; the client sends nothing until WINDOW_UPDATE makes it positive, then exactly the credit.
        private static async Task InitialWindowDecreaseMidUploadAsync(CancellationToken ct)
        {
            const int window = Http2Constants.DefaultInitialWindowSize;
            using var s = await ScriptedHttp2Server.StartAsync(ct);
            var body = Pattern(100_000, 5);
            var post = s.SendAsync(HttpMethod.POST, "/upload", body, ct);
            int id = await s.NextRequestAsync(ct);
            await s.WaitForAsync(() => s.RequestBodies[id].Length == window, "the first window of the upload", ct);

            await s.WriteSettingsAsync(ct, (Http2SettingId.InitialWindowSize, 16384u));
            await s.WaitForAsync(() => s.SettingsPending == 0, "SETTINGS ACK", ct);
            // Stream window: 0 + (16384 - 65535) = -49151. Connection credit is plentiful from here on.
            await s.WriteWindowUpdateAsync(0, 1_000_000, ct);
            await s.WriteWindowUpdateAsync(id, 49151, ct);
            await s.SyncAsync(3, ct);
            ScenarioAssert.Equal((long)window, s.RequestBodies[id].Length, "bytes sent while the stream window was <= 0");

            await s.WriteWindowUpdateAsync(id, 20000, ct);
            await s.WaitForAsync(() => s.RequestBodies[id].Length == window + 20000, "20000 bytes of new credit", ct);
            await s.WriteWindowUpdateAsync(id, 20000, ct);
            await s.WaitForAsync(() => s.EndedByClient.Contains(id), "the end of the upload", ct);
            ScenarioAssert.True(Equal(body, s.RequestBodies[id].ToArray()), "uploaded body differs");

            await s.RespondAsync(id, 200, "stored", ct);
            using var response = await post;
            ScenarioAssert.Equal("stored", response.GetBodyAsString(), "upload response");
        }

        // RFC 9113 Section 6.9: DATA on streams the client reset still consumes connection credit, which the
        // client must return. 20 x 60000 bytes exceed its whole connection window, so a leak stalls the row.
        private static async Task ResetStreamDataReturnsCreditAsync(CancellationToken ct)
        {
            using var s = await ScriptedHttp2Server.StartAsync(ct);
            var junk = Pattern(60000, 7);
            for (int i = 0; i < 20; i++)
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var request = s.GetAsync("/reset/" + i, cancel.Token);
                int id = await s.NextRequestAsync(ct);
                cancel.Cancel();
                await ScenarioAssert.ThrowsAsync<OperationCanceledException>(request, "cancelled /reset/" + i);
                await s.ExpectResetAsync(id, ct);
                await s.WriteHeadersAsync(id, new HpackBlock().Status(200).ToArray(), endStream: false, ct);
                await s.WriteDataAsync(id, junk, endStream: true, ct, ignoreStreamWindow: true);
            }

            var big = s.GetAsync("/big", ct);
            int bigId = await s.NextRequestAsync(ct);
            var payload = Pattern(200_000, 9);
            await s.WriteHeadersAsync(bigId, new HpackBlock().Status(200).ToArray(), endStream: false, ct);
            await s.WriteDataAsync(bigId, payload, endStream: true, ct);
            using var response = await big;
            ScenarioAssert.True(Equal(payload, response.Body.ToArray()), "body after the reset streams differs");
        }

        // f. RFC 9113 Section 5.1.2: 120 concurrent requests against MAX_CONCURRENT_STREAMS 100, lowered to 10
        // mid-flight, with seeded random cancellation before admission, before the response and mid-response,
        // responses in shuffled order through a dynamic-table-heavy encoder (late blocks on reset streams too).
        private static async Task MaxConcurrentLoweredRandomCancelAsync(CancellationToken ct)
        {
            const int total = 120;
            const int initialLimit = 100;
            const int loweredLimit = 10;
            var rng = new Random(20261103);
            using var s = await ScriptedHttp2Server.StartAsync(ct, null, (Http2SettingId.MaxConcurrentStreams, (uint)initialLimit));
            using var encoder = new HpackEncoder();
            // Server view of the client's open streams: a stream stops counting when the server writes its
            // END_STREAM or the scenario cancels its request. Every response is followed by a PING round trip,
            // so the client always knows every closure but the newest one: its own count is the server's or
            // one higher, never lower, and the check below cannot fire for a client that keeps the limit.
            var serverEnded = new HashSet<int>();
            var cancelledStreams = new HashSet<int>();
            var cancelled = new HashSet<int>();
            long syncId = 1000;
            int limit = initialLimit;
            bool loweredAcked = false;
            s.OnRequestHeaders = id =>
            {
                int open = 0;
                foreach (int other in s.RequestStreams)
                {
                    if (!serverEnded.Contains(other) && !cancelledStreams.Contains(other))
                        open++;
                }

                int effective = loweredAcked ? loweredLimit : limit;
                ScenarioAssert.True(open < effective,
                    "stream " + id + " opened with " + open + " streams open and MAX_CONCURRENT_STREAMS " + effective);
            };

            var sources = new CancellationTokenSource[total];
            var tasks = new Task<UHttpResponse>[total];
            try
            {
                for (int i = 0; i < total; i++)
                {
                    sources[i] = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    tasks[i] = s.GetAsync("/r/" + i, sources[i].Token);
                }

                await s.WaitForAsync(() => s.RequestStreams.Count == initialLimit, "the first 100 streams", ct);
                await s.SyncAsync(1, ct);
                ScenarioAssert.Equal(initialLimit, s.RequestStreams.Count, "streams opened against MAX_CONCURRENT_STREAMS 100");

                await s.WriteSettingsAsync(ct, (Http2SettingId.MaxConcurrentStreams, (uint)loweredLimit));
                await s.WaitForAsync(() => s.SettingsPending == 0, "SETTINGS ACK", ct);
                loweredAcked = true;

                // Cancel a third of the queued requests: no slot is free, so they must never open a stream.
                var queued = new List<int>();
                for (int i = 0; i < total; i++)
                {
                    if (s.StreamOf("/r/" + i) == 0)
                        queued.Add(i);
                }

                ScenarioAssert.Equal(total - initialLimit, queued.Count, "queued requests");
                var stillQueued = new List<int>();
                foreach (int i in queued)
                {
                    if (rng.Next(3) == 0)
                        Cancel(i);
                    else
                        stillQueued.Add(i);
                }

                // A quarter of the open requests are cancelled before any response.
                var open = new List<int>(s.RequestStreams);
                foreach (int id in open)
                {
                    if (rng.Next(4) == 0)
                        Cancel(Index(id));
                }

                await RespondShuffledAsync(open);

                // The rest are admitted at most ten at a time; each batch is answered only once complete.
                int opened = 0;
                while (opened < stillQueued.Count)
                {
                    int expected = Math.Min(loweredLimit, stillQueued.Count - opened);
                    int next = initialLimit + opened;
                    await s.WaitForAsync(() => s.RequestStreams.Count >= next + expected, expected + " admitted requests", ct);
                    await s.SyncAsync(100 + opened, ct);
                    ScenarioAssert.Equal(next + expected, s.RequestStreams.Count, "admitted requests at MAX_CONCURRENT_STREAMS 10");
                    opened += expected;
                    await RespondShuffledAsync(s.RequestStreams.GetRange(next, expected));
                }

                ScenarioAssert.Equal(initialLimit + stillQueued.Count, s.RequestStreams.Count, "streams opened in total");
                for (int i = 0; i < total; i++)
                {
                    if (cancelled.Contains(i))
                    {
                        await ScenarioAssert.ThrowsAsync<OperationCanceledException>(tasks[i], "cancelled /r/" + i);
                        continue;
                    }

                    using var response = await tasks[i];
                    ScenarioAssert.Equal("body-" + i, response.GetBodyAsString(), "/r/" + i + " body");
                    ScenarioAssert.Equal(i.ToString(CultureInfo.InvariantCulture), response.Headers.Get("x-index"), "/r/" + i + " x-index");
                    ScenarioAssert.Equal("shared-across-streams", response.Headers.Get("x-shared"), "/r/" + i + " x-shared");
                }

                await AssertUsableAsync(s, "/after", ct);
            }
            finally
            {
                foreach (var source in sources)
                    source?.Dispose();
            }

            void Cancel(int i)
            {
                cancelled.Add(i);
                int streamId = s.StreamOf("/r/" + i.ToString(CultureInfo.InvariantCulture));
                if (streamId != 0)
                    cancelledStreams.Add(streamId);
                sources[i].Cancel();
            }

            int Index(int streamId) => int.Parse(s.PathOf(streamId).Substring("/r/".Length), CultureInfo.InvariantCulture);

            async Task RespondShuffledAsync(List<int> ids)
            {
                var order = new List<int>(ids);
                for (int k = order.Count - 1; k > 0; k--)
                {
                    int j = rng.Next(k + 1);
                    (order[k], order[j]) = (order[j], order[k]);
                }

                foreach (int id in order)
                {
                    int i = Index(id);
                    bool wasCancelled = cancelled.Contains(i);
                    serverEnded.Add(id);
                    if (wasCancelled && rng.Next(2) == 0)
                        continue; // no late response for this reset stream

                    var block = encoder.Encode(new List<(string, string)>
                    {
                        (":status", "200"),
                        ("x-index", i.ToString(CultureInfo.InvariantCulture)),
                        ("x-shared", "shared-across-streams"),
                        ("x-order", order.IndexOf(id).ToString(CultureInfo.InvariantCulture))
                    }).ToArray();
                    await s.WriteHeadersAsync(id, block, endStream: false, ct);
                    if (!wasCancelled && rng.Next(10) == 0)
                        Cancel(i); // mid-response: headers delivered, body not yet
                    await s.WriteDataAsync(id, Encoding.ASCII.GetBytes("body-" + i), endStream: true, ct,
                        ignoreStreamWindow: cancelled.Contains(i));
                    await s.SyncAsync(syncId++, ct);
                }
            }
        }

        // g. RFC 9113 Section 8.1.1: DATA that does not add up to content-length makes the response malformed:
        // the request fails, the stream is reset with PROTOCOL_ERROR, the connection stays usable.
        private static async Task ContentLengthMismatchAsync(string contentLength, string body, CancellationToken ct)
        {
            using var s = await ScriptedHttp2Server.StartAsync(ct);
            var request = s.GetAsync("/mismatch", ct);
            int id = await s.NextRequestAsync(ct);
            await s.WriteHeadersAsync(id, new HpackBlock().Status(200).Literal("content-length", contentLength).ToArray(), endStream: false, ct);
            await s.WriteDataAsync(id, Encoding.ASCII.GetBytes(body), endStream: true, ct);
            await ScenarioAssert.ThrowsAsync<UHttpException>(request, "response with content-length " + contentLength + " and " + body.Length + " body bytes");
            ScenarioAssert.Equal(Http2ErrorCode.ProtocolError, await s.ExpectResetAsync(id, ct), "RST_STREAM code for the malformed response");
            await AssertUsableAsync(s, "/after", ct);
        }

        // PING and SETTINGS floods (CVE-2019-9512/9515 shapes): every one is acknowledged, the connection stays usable.
        private static async Task ControlFrameFloodAsync(CancellationToken ct)
        {
            const int count = 500;
            using var s = await ScriptedHttp2Server.StartAsync(ct);
            for (int i = 0; i < count; i++)
            {
                await s.WritePingAsync(10_000 + i, ack: false, ct);
                await s.WriteSettingsAsync(ct);
            }

            await s.WaitForAsync(() => s.PingAcks.Count == count && s.SettingsPending == 0, "the flood's ACKs", ct);
            for (int i = 0; i < count; i++)
                ScenarioAssert.True(s.PingAcks.Contains(10_000 + i), "PING " + i + " not acknowledged");
            await AssertUsableAsync(s, "/after", ct);
        }

        // ---- helpers --------------------------------------------------------------------------------

        private static async Task ExpectCompressionErrorAsync(HpackBlock block, CancellationToken ct)
        {
            using var s = await ScriptedHttp2Server.StartAsync(ct);
            var request = s.GetAsync("/bad-block", ct);
            int id = await s.NextRequestAsync(ct);
            await s.WriteHeadersAsync(id, block.ToArray(), endStream: true, ct);
            var goaway = await s.ExpectGoAwayAsync(ct);
            ScenarioAssert.Equal(Http2ErrorCode.CompressionError, goaway.Code, "GOAWAY code");
            await ScenarioAssert.ThrowsAsync<UHttpException>(request, "request answered with the bad block");
        }

        private static async Task<UHttpResponse> ExchangeAsync(ScriptedHttp2Server s, string path, HpackBlock block, CancellationToken ct)
        {
            var request = s.GetAsync(path, ct);
            int id = await s.NextRequestAsync(ct);
            await s.WriteHeadersAsync(id, block.ToArray(), endStream: true, ct);
            return await request;
        }

        private static async Task AssertUsableAsync(ScriptedHttp2Server s, string path, CancellationToken ct)
        {
            var request = s.GetAsync(path, ct);
            int id = await s.NextRequestAsync(ct);
            await s.RespondAsync(id, 200, "usable", ct);
            using var response = await request;
            ScenarioAssert.Equal("usable", response.GetBodyAsString(), "follow-up request on the same connection");
        }

        private static async Task ExpectNotProcessedAsync(Task task, string what)
        {
            var ex = await ScenarioAssert.ThrowsAsync<Http2RetryableRequestException>(task, what);
            ScenarioAssert.True(ex.RequestNotProcessed, what + ": must fail as not processed (RFC 9113 Section 8.7)");
        }

        private static int UploadIndex(ScriptedHttp2Server s, int streamId) =>
            int.Parse(s.PathOf(streamId).Substring("/upload/".Length), CultureInfo.InvariantCulture);

        private static byte[] Pattern(int length, int seed)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
                bytes[i] = (byte)((i * 31 + seed) & 0xFF);
            return bytes;
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }

            return true;
        }
    }
}
