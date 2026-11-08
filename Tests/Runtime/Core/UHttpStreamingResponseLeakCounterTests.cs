using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// The finalizer counter behind <see cref="StreamingResponseLeakCheck"/> (#366): a dropped response
    /// is counted and reported, a disposed one is not.
    /// </summary>
    [TestFixture]
    public class UHttpStreamingResponseLeakCounterTests
    {
        private const int ResponseCount = 64;

        private List<string> _messages;

        [SetUp]
        public void SetUp()
        {
            _messages = new List<string>();
            // Keeps the deliberate leak below out of the run log, where it would look like a real one.
            TurboDiagnostics.SetSink(message =>
            {
                lock (_messages)
                    _messages.Add(message);
            });
        }

        [TearDown]
        public void TearDown()
        {
            TurboDiagnostics.ResetSink();
        }

        [Test]
        public void DroppedResponses_AreCountedByTheFinalizer()
        {
            int before = StreamingResponseLeakCheck.Begin();

            var dropped = DropResponses(ResponseCount);
            GC.Collect();
            GC.WaitForPendingFinalizers();

            int counted = UHttpStreamingResponse.UndisposedFinalizedCountForTesting - before;
            // Instances a conservative stack scan kept alive are disposed here, so none of them is
            // finalized later, after the sink is restored, as a stray warning under another test.
            foreach (var reference in dropped)
                (reference.Target as UHttpStreamingResponse)?.Dispose();

#if DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
            // The Mono GC scans stacks conservatively and may keep a few instances alive; most go.
            Assert.Greater(counted, ResponseCount / 2);
            lock (_messages)
                Assert.That(_messages, Has.Some.Contains("UHttpStreamingResponse was not disposed"));
#else
            Assert.AreEqual(0, counted, "The finalizer diagnostic is compiled out of release builds.");
#endif
        }

        [Test]
        public void DisposedResponses_AreNotCounted()
        {
            int before = StreamingResponseLeakCheck.Begin();

            CreateAndDisposeResponses(ResponseCount);

            StreamingResponseLeakCheck.AssertNoLeakSince(before);
            lock (_messages)
                Assert.That(_messages, Has.None.Contains("UHttpStreamingResponse was not disposed"));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] DropResponses(int count)
        {
            var references = new WeakReference[count];
            for (int i = 0; i < count; i++)
                references[i] = new WeakReference(NewResponse());
            return references;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CreateAndDisposeResponses(int count)
        {
            for (int i = 0; i < count; i++)
                NewResponse().Dispose();
        }

        private static UHttpStreamingResponse NewResponse()
        {
            return new UHttpStreamingResponse(
                HttpStatusCode.OK,
                new HttpHeaders(),
                new BufferedResponseBodySource(Array.Empty<byte>(), HttpHeaders.Empty));
        }
    }
}
