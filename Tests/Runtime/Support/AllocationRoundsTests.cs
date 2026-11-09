using System;
using NUnit.Framework;

namespace TurboHTTP.Tests
{
    /// <summary>
    /// #438: a gate judges the smallest of several isolated rounds, so a foreign allocation in one frame never
    /// fails it, while an allocation the section makes on every unit still does.
    /// </summary>
    [TestFixture]
    public class AllocationRoundsTests
    {
        [Test]
        public void BytesPerUnit_IsTheSmallestRound()
        {
            var rounds = new AllocationRounds();
            rounds.AddRound(16 * 256, 256);
            rounds.AddRound(0, 256);
            rounds.AddRound(4096 * 256, 256);

            Assert.AreEqual(0, rounds.BytesPerUnit);
            Assert.AreEqual(AllocationVerdict.Within, rounds.Judge(0));
        }

        [Test]
        public void PerUnitAllocationInEveryRound_IsOver()
        {
            var rounds = new AllocationRounds();
            for (var i = 0; i < 5; i++)
                rounds.AddRound(137 * 1024 + i, 1024);
            rounds.AddIdleFrame(136);

            Assert.AreEqual(137, rounds.BytesPerUnit);
            Assert.AreEqual(AllocationVerdict.Over, rounds.Judge(48));
        }

        [Test]
        public void ExcessTheIdleFrameExplains_IsUnresolved()
        {
            var rounds = new AllocationRounds();
            rounds.AddRound(64 * 1024, 1024);
            rounds.AddIdleFrame(40 * 1024);
            rounds.AddIdleFrame(32 * 1024);

            Assert.AreEqual(32 * 1024, rounds.ForeignFloorBytes);
            Assert.AreEqual(AllocationVerdict.Unresolved, rounds.Judge(32));
            Assert.AreEqual(AllocationVerdict.Over, rounds.Judge(31));
        }

        [Test]
        public void WithoutIdleFrames_NothingIsTakenOff()
        {
            var rounds = new AllocationRounds();
            rounds.AddRound(33 * 128, 128);

            Assert.AreEqual(0, rounds.ForeignFloorBytes);
            Assert.AreEqual(AllocationVerdict.Over, rounds.Judge(32));
        }

        [Test]
        public void NegativeReading_CountsAsZero()
        {
            var rounds = new AllocationRounds();
            rounds.AddRound(-5, 10);

            Assert.AreEqual(0, rounds.BytesPerUnit);
        }

        [Test]
        public void RoundWithoutUnits_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AllocationRounds().AddRound(0, 0));
        }

        [Test]
        public void JudgeBeforeAnyRound_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => new AllocationRounds().Judge(0));
        }
    }
}
