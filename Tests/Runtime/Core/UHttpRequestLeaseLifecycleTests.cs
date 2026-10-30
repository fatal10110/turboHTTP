using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// Lease lifecycle of client-created requests (#62 stale references, #148 hold-count and
    /// send/response/dispose orderings). All tests are deterministic: they drive the lease state
    /// machine directly or through <see cref="MockTransport"/>, without timing.
    /// </summary>
    [TestFixture]
    public class UHttpRequestLeaseLifecycleTests
    {
        private const string EventDispose = "Dispose";
        private const string EventEndSend = "EndSend";
        private const string EventReleaseResponse = "ReleaseResponse";

        [Test]
        public void StaleReference_AfterDisposeAndNewLease_ThrowsAndCannotReachNewRequest()
        {
            using var client = CreateClient();

            var stale = client.Get("https://example.test/first");
            stale.Dispose();

            var current = client.Get("https://example.test/second");
            Assert.AreNotSame(stale, current, "A disposed request must never be handed out again.");

            Assert.Throws<ObjectDisposedException>(() => stale.WithHeader("Authorization", "Bearer stale"));
            Assert.Throws<ObjectDisposedException>(() => stale.WithMetadata("key", "stale"));
            Assert.Throws<ObjectDisposedException>(() => stale.WithBody("stale"));
            AssertAsync.ThrowsAsync<ObjectDisposedException, UHttpResponse>(() => stale.SendBufferedAsync());
            AssertAsync.ThrowsAsync<ObjectDisposedException, UHttpStreamingResponse>(() => stale.SendStreamingAsync());
            AssertAsync.ThrowsAsync<ObjectDisposedException, UHttpResponse>(() => client.SendBufferedAsync(stale));

            // Even references the stale holder cached before the dispose never reach the new lease.
            stale.Headers.Set("Authorization", "Bearer stale");
            Assert.IsNull(current.Headers.Get("Authorization"));
            Assert.AreEqual(new Uri("https://example.test/second"), current.Uri);

            current.Dispose();
        }

        [Test]
        public void StaleReference_AfterSendAutoDisposeAndNewLease_Throws()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient();

                var stale = client.Get("https://example.test/retry");
                using (await stale.SendBufferedAsync().ConfigureAwait(false))
                {
                }

                var current = client.Get("https://example.test/other-caller");
                Assert.AreNotSame(stale, current);

                Assert.Throws<ObjectDisposedException>(() => stale.WithHeader("Authorization", "Bearer stale"));
                await ExpectThrowsAsync<ObjectDisposedException>(
                    async () => await stale.SendBufferedAsync().ConfigureAwait(false)).ConfigureAwait(false);

                Assert.IsNull(current.Headers.Get("Authorization"));
                Assert.IsTrue(current.IsLeaseActive);
                current.Dispose();
            });
        }

        [Test]
        public void SendBufferedAsync_OnDifferentClient_ThrowsAndKeepsLease()
        {
            AssertAsync.Run(async () =>
            {
                using var clientA = CreateClient();
                using var clientB = CreateClient();
                var owner = new CountingMemoryOwner(4);
                var request = clientA.Post("https://example.test/cross").WithLeasedBody(owner, 4);

                var ex = await ExpectThrowsAsync<InvalidOperationException>(
                    () => clientB.SendBufferedAsync(request)).ConfigureAwait(false);
                StringAssert.Contains("different client", ex.Message);
                Assert.IsTrue(request.IsLeaseActive, "A rejected send must not consume the lease.");
                Assert.AreEqual(0, owner.DisposeCount);

                using (await clientA.SendBufferedAsync(request).ConfigureAwait(false))
                {
                }

                request.Dispose();
                Assert.IsFalse(request.IsLeaseActive);
                Assert.AreEqual(1, owner.DisposeCount);
            });
        }

        [Test]
        public void SendStreamingAsync_OnDifferentClient_ThrowsAndKeepsLease()
        {
            AssertAsync.Run(async () =>
            {
                using var clientA = CreateClient();
                using var clientB = CreateClient();
                var owner = new CountingMemoryOwner(4);
                var request = clientA.Post("https://example.test/cross-stream").WithLeasedBody(owner, 4);

                var ex = await ExpectThrowsAsync<InvalidOperationException>(
                    () => clientB.SendStreamingAsync(request)).ConfigureAwait(false);
                StringAssert.Contains("different client", ex.Message);
                Assert.IsTrue(request.IsLeaseActive, "A rejected send must not consume the lease.");
                Assert.AreEqual(0, owner.DisposeCount);

                request.Dispose();
                Assert.IsFalse(request.IsLeaseActive);
                Assert.AreEqual(1, owner.DisposeCount);
            });
        }

        [Test]
        public void BufferedResponse_DisposedTwice_CompletesLeaseExactlyOnce()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient();
                var owner = new CountingMemoryOwner(4);
                var request = client.Post("https://example.test/double").WithLeasedBody(owner, 4);

                // Request dispose (auto, at the end of the send) happens before the response dispose.
                var response = await request.SendBufferedAsync().ConfigureAwait(false);
                Assert.IsTrue(request.IsLeaseActive, "The response still references the request.");
                Assert.AreEqual(0, owner.DisposeCount);

                response.Dispose();
                response.Dispose();

                Assert.IsFalse(request.IsLeaseActive);
                Assert.AreEqual(1, owner.DisposeCount);
                Assert.Throws<ObjectDisposedException>(() => request.WithHeader("X-After", "1"));
            });
        }

        [Test]
        public void StreamingResponse_DisposedTwice_CompletesLeaseExactlyOnce()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient();
                var owner = new CountingMemoryOwner(4);
                var request = client.Post("https://example.test/double-stream").WithLeasedBody(owner, 4);

                var response = await request.SendStreamingAsync().ConfigureAwait(false);
                Assert.IsTrue(request.IsLeaseActive, "The response still references the request.");
                Assert.AreEqual(0, owner.DisposeCount);

                await response.DisposeAsync().ConfigureAwait(false);
                await response.DisposeAsync().ConfigureAwait(false);

                Assert.IsFalse(request.IsLeaseActive);
                Assert.AreEqual(1, owner.DisposeCount);
            });
        }

        [Test]
        public void ResponseDisposedBeforeRequest_CompletesLeaseOnRequestDispose()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient();
                var owner = new CountingMemoryOwner(4);
                var request = client.Post("https://example.test/response-first").WithLeasedBody(owner, 4);

                var response = await client.SendBufferedAsync(request).ConfigureAwait(false);
                response.Dispose();
                Assert.IsTrue(request.IsLeaseActive, "The caller has not disposed the request yet.");
                Assert.AreEqual(0, owner.DisposeCount);

                request.Dispose();
                request.Dispose();
                Assert.IsFalse(request.IsLeaseActive);
                Assert.AreEqual(1, owner.DisposeCount);
            });
        }

        [Test]
        public void UnmatchedReleaseResponseHold_IsClampedAndLeaseCompletesOnce()
        {
            using var client = CreateClient();
            var owner = new CountingMemoryOwner(4);
            var request = client.Post("https://example.test/extra-release").WithLeasedBody(owner, 4);

            // Unmatched release with no hold: must not go negative, which would block the next send.
            request.ReleaseResponseHold();
            Assert.IsTrue(request.IsLeaseActive);
            Assert.DoesNotThrow(() => request.BeginSend());

            request.RetainForResponse();
            request.ReleaseResponseHold();
            request.ReleaseResponseHold(); // double release
            request.EndSend();
            Assert.IsTrue(request.IsLeaseActive, "Dispose has not been requested.");
            Assert.AreEqual(0, owner.DisposeCount);

            request.Dispose();
            Assert.IsFalse(request.IsLeaseActive);
            Assert.AreEqual(1, owner.DisposeCount);

            // Releases after completion are no-ops.
            request.ReleaseResponseHold();
            request.EndSend();
            Assert.AreEqual(1, owner.DisposeCount);
        }

        [Test]
        public void ReleaseResponseHold_WithOtherHoldOutstanding_KeepsLeaseUntilLastRelease()
        {
            using var client = CreateClient();
            var owner = new CountingMemoryOwner(4);
            var request = client.Post("https://example.test/two-holds").WithLeasedBody(owner, 4);

            request.BeginSend();
            request.RetainForResponse();
            request.RetainForResponse();
            request.EndSend();
            request.Dispose();

            request.ReleaseResponseHold();
            Assert.IsTrue(request.IsLeaseActive, "One response still references the request.");
            Assert.AreEqual(0, owner.DisposeCount);

            request.ReleaseResponseHold();
            Assert.IsFalse(request.IsLeaseActive);
            Assert.AreEqual(1, owner.DisposeCount);
        }

        private static IEnumerable<TestCaseData> SendWithResponseOrderings()
        {
            var events = new[] { EventDispose, EventEndSend, EventReleaseResponse };
            for (int a = 0; a < 3; a++)
                for (int b = 0; b < 3; b++)
                    for (int c = 0; c < 3; c++)
                    {
                        if (a == b || b == c || a == c)
                            continue;

                        yield return new TestCaseData(events[a], events[b], events[c]);
                    }
        }

        [TestCaseSource(nameof(SendWithResponseOrderings))]
        public void SendWithResponse_EveryOrdering_CompletesLeaseExactlyOnceAtLastEvent(
            string first,
            string second,
            string third)
        {
            using var client = CreateClient();
            var owner = new CountingMemoryOwner(4);
            var request = client.Post("https://example.test/ordering").WithLeasedBody(owner, 4);

            request.BeginSend();
            request.RetainForResponse();

            var events = new[] { first, second, third };
            for (int i = 0; i < events.Length; i++)
            {
                Assert.IsTrue(request.IsLeaseActive, $"Lease completed before event {i} ({events[i]}).");
                Assert.AreEqual(0, owner.DisposeCount);
                Apply(request, events[i]);
            }

            Assert.IsFalse(request.IsLeaseActive);
            Assert.AreEqual(1, owner.DisposeCount);

            // Repeating every event (double dispose, double release, late EndSend) never completes twice.
            for (int i = 0; i < events.Length; i++)
                Apply(request, events[i]);

            Assert.IsFalse(request.IsLeaseActive);
            Assert.AreEqual(1, owner.DisposeCount);
        }

        [TestCase(EventDispose, EventEndSend)]
        [TestCase(EventEndSend, EventDispose)]
        public void SendWithoutResponse_EitherOrdering_CompletesLeaseExactlyOnceAtLastEvent(
            string first,
            string second)
        {
            using var client = CreateClient();
            var owner = new CountingMemoryOwner(4);
            var request = client.Post("https://example.test/failed-send").WithLeasedBody(owner, 4);

            request.BeginSend();

            Apply(request, first);
            Assert.IsTrue(request.IsLeaseActive);
            Assert.AreEqual(0, owner.DisposeCount);

            Apply(request, second);
            Assert.IsFalse(request.IsLeaseActive);
            Assert.AreEqual(1, owner.DisposeCount);

            Apply(request, first);
            Apply(request, second);
            Assert.AreEqual(1, owner.DisposeCount);
        }

        [Test]
        public void DisposeWithoutSend_CompletesLeaseImmediately()
        {
            using var client = CreateClient();
            var owner = new CountingMemoryOwner(4);
            var request = client.Post("https://example.test/unsent").WithLeasedBody(owner, 4);

            request.Dispose();

            Assert.IsFalse(request.IsLeaseActive);
            Assert.AreEqual(1, owner.DisposeCount);
            Assert.Throws<ObjectDisposedException>(() => request.BeginSend());
        }

        private static void Apply(UHttpRequest request, string leaseEvent)
        {
            switch (leaseEvent)
            {
                case EventDispose:
                    request.Dispose();
                    break;
                case EventEndSend:
                    request.EndSend();
                    break;
                case EventReleaseResponse:
                    request.ReleaseResponseHold();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(leaseEvent), leaseEvent, null);
            }
        }

        private static UHttpClient CreateClient()
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = new MockTransport(),
                DisposeTransport = true
            });
        }

        private static async Task<T> ExpectThrowsAsync<T>(Func<Task> action) where T : Exception
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (T expected)
            {
                return expected;
            }

            Assert.Fail($"Expected {typeof(T).Name}, but no exception was thrown.");
            return null;
        }

        private sealed class CountingMemoryOwner : IMemoryOwner<byte>
        {
            private int _disposeCount;

            public CountingMemoryOwner(int length)
            {
                Memory = new byte[length];
            }

            public Memory<byte> Memory { get; }

            public int DisposeCount => Volatile.Read(ref _disposeCount);

            public void Dispose()
            {
                Interlocked.Increment(ref _disposeCount);
            }
        }
    }
}
