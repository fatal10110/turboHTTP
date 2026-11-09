using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests
{
    /// <summary>
    /// #426: an allocation gate measures only with a counter shown to count. Unity's Mono has
    /// <c>GC.GetAllocatedBytesForCurrentThread</c> and it always reads 0, so a gate that trusted it passed
    /// whatever the code allocated.
    /// </summary>
    [TestFixture]
    public class AllocationMeterTests
    {
        private const int KnownAllocationBytes = 256 * 1024;

        [Test]
        public void Counts_CounterThatReadsZero_IsFalse()
        {
            Assert.IsFalse(ManagedAllocationCounter.Counts(() => 0L));
        }

        [Test]
        public void Counts_CounterThatSeesTheProbe_IsTrue()
        {
            long reads = 0;
            Assert.IsTrue(ManagedAllocationCounter.Counts(() => reads++ * ManagedAllocationCounter.ProbeBytes));
        }

        [Test]
        public void Counts_CounterThatSeesLessThanTheProbe_IsFalse()
        {
            long reads = 0;
            Assert.IsFalse(ManagedAllocationCounter.Counts(() => reads++ * (ManagedAllocationCounter.ProbeBytes - 1L)));
        }

        [UnityTest]
        public IEnumerator Calibrated_CountsAKnownAllocation()
        {
            using var meter = new AllocationMeter();
            yield return meter.Calibrate();

            yield return null;
            meter.Begin();
            var block = new byte[KnownAllocationBytes];
            meter.End();
            yield return null;
            GC.KeepAlive(block);

            TestContext.WriteLine(
                "[Allocation] known_allocation counter=" + meter.Name +
                " bytes=" + KnownAllocationBytes +
                " measured=" + meter.Allocated);
            Assert.GreaterOrEqual(
                meter.Allocated,
                KnownAllocationBytes,
                "The " + meter.Name + " counter missed a " + KnownAllocationBytes + " byte allocation.");
        }
    }
}
