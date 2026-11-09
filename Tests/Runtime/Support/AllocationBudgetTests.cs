using NUnit.Framework;

namespace TurboHTTP.Tests
{
    /// <summary>#438: each runtime and counter judges a gate by its own measured budget.</summary>
    [TestFixture]
    public class AllocationBudgetTests
    {
        private static readonly AllocationBudget s_budget = new AllocationBudget(
            precise: 1,
            optimizedFrame: 2,
            unoptimizedMonoFrame: 3,
            unoptimizedIl2CppFrame: 4);

        [TestCase(true, true, false, AllocationProfile.Precise)]
        [TestCase(true, false, true, AllocationProfile.Precise)]
        [TestCase(false, true, false, AllocationProfile.OptimizedFrame)]
        [TestCase(false, true, true, AllocationProfile.OptimizedFrame)]
        [TestCase(false, false, false, AllocationProfile.UnoptimizedMonoFrame)]
        [TestCase(false, false, true, AllocationProfile.UnoptimizedIl2CppFrame)]
        public void SelectProfile_PicksTheCounterThenTheCompilationThenTheBackend(
            bool precise,
            bool optimized,
            bool il2cpp,
            AllocationProfile expected)
        {
            Assert.AreEqual(expected, AllocationBudget.SelectProfile(precise, optimized, il2cpp));
        }

        [Test]
        public void For_ReturnsTheProfilesBudget()
        {
            Assert.AreEqual(1, s_budget.For(AllocationProfile.Precise));
            Assert.AreEqual(2, s_budget.For(AllocationProfile.OptimizedFrame));
            Assert.AreEqual(3, s_budget.For(AllocationProfile.UnoptimizedMonoFrame));
            Assert.AreEqual(4, s_budget.For(AllocationProfile.UnoptimizedIl2CppFrame));
        }
    }
}
