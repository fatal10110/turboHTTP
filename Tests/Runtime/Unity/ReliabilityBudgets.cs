using System.Globalization;
using NUnit.Framework;
using UnityEngine;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// Per-target budgets of the Unity reliability, stress and performance-budget suites (Phase 15.6,
    /// #105), and the machine-readable metric lines they print. Each metric is one line in the test's
    /// NUnit output:
    /// <c>TURBOHTTP_METRIC name=&lt;id&gt; value=&lt;n&gt; budget=&lt;n&gt; unit=&lt;u&gt; profile=&lt;p&gt; result=pass|fail</c>.
    /// <c>Tools~/unity/platform_matrix.py</c> collects them into <c>unity-platform-matrix.json</c>.
    /// </summary>
    /// <remarks>
    /// The editor and standalone values are conservative host budgets. The mobile values are first
    /// estimates (an x86_64 emulator row exists; the arm64 and iOS rows are needs-device-validation,
    /// #99) and are tightened from device runs.
    /// </remarks>
    internal sealed class ReliabilityBudgets
    {
        public const string MetricPrefix = "TURBOHTTP_METRIC";

        private static readonly ReliabilityBudgets EditorProfile = new ReliabilityBudgets("editor", maxFrameWorkMs: 25d);
        private static readonly ReliabilityBudgets StandaloneProfile = new ReliabilityBudgets("standalone", maxFrameWorkMs: 25d);
        private static readonly ReliabilityBudgets MobileProfile = new ReliabilityBudgets("mobile", maxFrameWorkMs: 50d);

        private ReliabilityBudgets(string profile, double maxFrameWorkMs)
        {
            Profile = profile;
            MaxFrameWorkMs = maxFrameWorkMs;
        }

        /// <summary>The profile of the running target: editor, mobile (Android/iOS players) or standalone.</summary>
        public static ReliabilityBudgets Current =>
            Application.isEditor ? EditorProfile
            : Application.isMobilePlatform ? MobileProfile
            : StandaloneProfile;

        public string Profile { get; }

        /// <summary>Most main-thread time one frame may spend on dispatcher work during a texture or audio burst.</summary>
        public double MaxFrameWorkMs { get; }

        /// <summary>
        /// How far past the dispatcher's per-frame time cap an item may still be started: only the dequeue
        /// cost, so the bound is one millisecond on every target.
        /// </summary>
        public double MaxLateStartMs => 1d;

        /// <summary>Temp files left after a load/cancel/destroy cycle has been cleaned up: none.</summary>
        public int MaxLeakedTempFiles => 0;

        /// <summary>Prints the metric line and fails the test with the threshold when <paramref name="value"/> exceeds <paramref name="budget"/>.</summary>
        public void AssertWithin(string name, double value, double budget, string unit)
        {
            var pass = value <= budget;
            Report(name, value, budget, unit, pass);
            Assert.That(
                value,
                Is.LessThanOrEqualTo(budget),
                name + " = " + Format(value) + " " + unit + " exceeds the " + Profile + " budget of " + Format(budget) + " " + unit);
        }

        /// <summary>Prints a metric line without a gate (for trend tracking only).</summary>
        public void Record(string name, double value, string unit)
        {
            TestContext.Out.WriteLine(
                MetricPrefix + " name=" + name + " value=" + Format(value) + " unit=" + unit + " profile=" + Profile);
        }

        private void Report(string name, double value, double budget, string unit, bool pass)
        {
            TestContext.Out.WriteLine(
                MetricPrefix + " name=" + name + " value=" + Format(value) + " budget=" + Format(budget) +
                " unit=" + unit + " profile=" + Profile + " result=" + (pass ? "pass" : "fail"));
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
