using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// #582: a <see cref="UHttpResponse"/> finalized without Dispose must not return its pooled body
    /// buffers, because a caller may still hold the <see cref="UHttpResponse.Body"/> sequence, which
    /// does not keep the response alive. The buffers are left to the GC and counted as abandoned.
    /// </summary>
    [TestFixture]
    public class UHttpResponseFinalizerBodyBufferTests
    {
        private const int ResponseCount = 32;
        private const int BodySize = 4096;
        private const int SegmentSize = 1024;

        [SetUp]
        public void DrainFinalizers()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        [Test]
        public void FinalizedPooledArrayResponse_LeavesHeldBodySequenceIntact()
        {
            var before = ByteArrayPool.GetDiagnostics();

            var dropped = DropResponses(ResponseCount, segmented: false);
            AssertHeldBodiesSurviveFinalization(dropped, before);
        }

        [Test]
        public void FinalizedSegmentedResponse_LeavesHeldBodySequenceIntact()
        {
            var before = ByteArrayPool.GetDiagnostics();

            var dropped = DropResponses(ResponseCount, segmented: true);
            AssertHeldBodiesSurviveFinalization(dropped, before);
        }

        [Test]
        public void DisposedResponse_StillReturnsBodyBufferToPool()
        {
            var before = ByteArrayPool.GetDiagnostics();

            var buffer = ByteArrayPool.Rent(BodySize);
            var response = new UHttpResponse(
                HttpStatusCode.OK,
                new HttpHeaders(),
                new ReadOnlyMemory<byte>(buffer, 0, BodySize),
                TimeSpan.Zero,
                new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/")),
                bodyFromPool: true);
            response.Dispose();

            var after = ByteArrayPool.GetDiagnostics();
            Assert.That(after.ReturnCount - before.ReturnCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(after.AbandonCount - before.AbandonCount, Is.EqualTo(0), "explicit Dispose returns, never abandons");
        }

        private static void AssertHeldBodiesSurviveFinalization(DroppedResponse[] dropped, ByteArrayPoolDiagnostics before)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Rent what a finalizer return would have handed back and overwrite it, as another request would.
            var scribbled = new List<byte[]>();
            for (int i = 0; i < ResponseCount * 4; i++)
            {
                var array = ByteArrayPool.Rent(BodySize);
                array.AsSpan().Fill(0xEE);
                scribbled.Add(array);
                var segment = ByteArrayPool.Rent(SegmentSize);
                segment.AsSpan().Fill(0xEE);
                scribbled.Add(segment);
            }

            int finalized = 0;
            try
            {
                for (int i = 0; i < dropped.Length; i++)
                {
                    if (dropped[i].Response.IsAlive)
                        continue;

                    finalized++;
                    byte expected = (byte)(i + 1);
                    var body = dropped[i].Body;
                    Assert.That(body.Length, Is.EqualTo(BodySize));
                    foreach (var memory in body)
                    {
                        var span = memory.Span;
                        for (int b = 0; b < span.Length; b++)
                        {
                            if (span[b] != expected)
                                Assert.Fail($"Body of finalized response {i} was overwritten after its buffer went back to the pool (byte 0x{span[b]:X2}, expected 0x{expected:X2}).");
                        }
                    }
                }
            }
            finally
            {
                foreach (var array in scribbled)
                    ByteArrayPool.Return(array);
            }

            // The Mono GC scans stacks conservatively and may keep a few instances alive; most go.
            Assert.That(finalized, Is.GreaterThan(ResponseCount / 2), "too few responses were finalized to test anything");

            var after = ByteArrayPool.GetDiagnostics();
            Assert.That(after.AbandonCount - before.AbandonCount, Is.GreaterThanOrEqualTo(finalized),
                "a finalized response counts its pooled body buffers with ByteArrayPool.Abandon");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static DroppedResponse[] DropResponses(int count, bool segmented)
        {
            var dropped = new DroppedResponse[count];
            for (int i = 0; i < count; i++)
            {
                var response = segmented ? NewSegmentedResponse((byte)(i + 1)) : NewPooledArrayResponse((byte)(i + 1));
                // Only the sequence is kept; the response itself becomes unreachable.
                dropped[i] = new DroppedResponse(response.Body, new WeakReference(response));
            }

            return dropped;
        }

        private static UHttpResponse NewPooledArrayResponse(byte fill)
        {
            var buffer = ByteArrayPool.Rent(BodySize);
            buffer.AsSpan(0, BodySize).Fill(fill);
            return new UHttpResponse(
                HttpStatusCode.OK,
                new HttpHeaders(),
                new ReadOnlyMemory<byte>(buffer, 0, BodySize),
                TimeSpan.Zero,
                new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/")),
                bodyFromPool: true);
        }

        private static UHttpResponse NewSegmentedResponse(byte fill)
        {
            var buffer = new SegmentedBuffer(SegmentSize);
            var chunk = new byte[BodySize];
            chunk.AsSpan().Fill(fill);
            buffer.Write(chunk);
            return new UHttpResponse(
                HttpStatusCode.OK,
                new HttpHeaders(),
                buffer.AsSequence(),
                buffer,
                TimeSpan.Zero,
                new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/")));
        }

        private readonly struct DroppedResponse
        {
            public DroppedResponse(ReadOnlySequence<byte> body, WeakReference response)
            {
                Body = body;
                Response = response;
            }

            public ReadOnlySequence<byte> Body { get; }
            public WeakReference Response { get; }
        }
    }
}
