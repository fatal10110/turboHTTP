using System;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// #583: a <see cref="UHttpStreamingResponse"/> dropped without Dispose aborts its body source from
    /// the finalizer backstop (through the thread pool), in every build, so the transport releases what
    /// the body holds.
    /// </summary>
    [TestFixture]
    public class UHttpStreamingResponseFinalizerAbortTests
    {
        private const int ResponseCount = 16;

        [SetUp]
        public void SetUp()
        {
            // Keeps the deliberate leak below out of the run log, where it would look like a real one.
            TurboDiagnostics.SetSink(_ => { });
        }

        [TearDown]
        public void TearDown()
        {
            TurboDiagnostics.ResetSink();
        }

        [Test]
        public void DroppedResponses_AbortTheirBodySources()
        {
            AssertAsync.Run(async () =>
            {
                var probe = new AbortProbe();
                var dropped = DropResponses(probe, ResponseCount);
                GC.Collect();
                GC.WaitForPendingFinalizers();

                int finalized = 0;
                foreach (var reference in dropped)
                {
                    if (!reference.IsAlive)
                        finalized++;
                }

                // Instances a conservative stack scan kept alive are disposed here, which aborts them too.
                foreach (var reference in dropped)
                    (reference.Target as UHttpStreamingResponse)?.Dispose();

                // The Mono GC scans stacks conservatively and may keep a few instances alive; most go.
                Assert.That(finalized, Is.GreaterThan(ResponseCount / 2), "too few responses were finalized to test anything");

                // Every finalized response hands its Abort to the thread pool; wait for those to run.
                var all = probe.WaitForAborts(ResponseCount);
                var completed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.That(completed, Is.SameAs(all),
                    $"only {probe.AbortCount} of {ResponseCount} body sources were aborted; a dropped response must abort its body (#583)");
            });
        }

        [Test]
        public void DisposedResponse_IsNotAbortedAgainByTheFinalizer()
        {
            var probe = new AbortProbe();
            CreateAndDisposeResponses(probe, ResponseCount);
            int afterDispose = probe.AbortCount;

            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.That(afterDispose, Is.EqualTo(ResponseCount), "Dispose before EOF aborts the body once");
            Assert.That(probe.AbortCount, Is.EqualTo(afterDispose), "a disposed response suppresses its finalizer");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] DropResponses(AbortProbe probe, int count)
        {
            var references = new WeakReference[count];
            for (int i = 0; i < count; i++)
                references[i] = new WeakReference(NewResponse(probe));
            return references;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CreateAndDisposeResponses(AbortProbe probe, int count)
        {
            for (int i = 0; i < count; i++)
                NewResponse(probe).Dispose();
        }

        private static UHttpStreamingResponse NewResponse(AbortProbe probe)
        {
            return new UHttpStreamingResponse(HttpStatusCode.OK, new HttpHeaders(), new ProbeBodySource(probe));
        }

        private sealed class AbortProbe
        {
            private readonly object _gate = new object();
            private int _aborts;
            private int _target = int.MaxValue;
            private TaskCompletionSource<bool> _reached;

            public int AbortCount => Volatile.Read(ref _aborts);

            public void OnAbort()
            {
                TaskCompletionSource<bool> reached = null;
                lock (_gate)
                {
                    _aborts++;
                    if (_aborts >= _target)
                        reached = _reached;
                }

                reached?.TrySetResult(true);
            }

            public Task WaitForAborts(int target)
            {
                lock (_gate)
                {
                    _target = target;
                    _reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (_aborts >= target)
                        _reached.TrySetResult(true);
                    return _reached.Task;
                }
            }
        }

        private sealed class ProbeBodySource : IResponseBodySource
        {
            private readonly AbortProbe _probe;

            public ProbeBodySource(AbortProbe probe)
            {
                _probe = probe;
            }

            public long? Length => null;

            public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
            {
                data = default;
                return false;
            }

            public bool TryDetachBufferedBody(out DetachedBufferedBody body)
            {
                body = default;
                return false;
            }

            public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct) => new ValueTask<int>(0);

            public ValueTask DrainAsync(CancellationToken ct) => default;

            // Not idempotent on purpose: a second Abort of one source would show up in the count.
            public void Abort() => _probe.OnAbort();

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct) =>
                new ValueTask<HttpHeaders>(HttpHeaders.Empty);

            public ValueTask DisposeAsync() => default;
        }
    }
}
