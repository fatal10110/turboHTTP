#!/usr/bin/env python3
"""Regression test for the sync-over-async ban (#181).

Compiles one scratch file under the shadow-build settings (Shadow.props, which wires the banned-API
analyzer to BannedSymbols.txt) and checks that every way of blocking on a Task or ValueTask raises
RS0030, that awaiting does not, and that shadow_build.py would report each hit as a new warning (it is
not in analyzer-baseline.txt), which fails check.sh. Before ValueTaskAwaiter(`1).GetResult were
banned, the two ValueTask lines compiled without a warning and this test failed.

Needs the .NET SDK (dotnet on PATH); no Unity DLLs. Builds in .build/banned-symbols-selftest.
"""
import collections
import os
import shutil
import sys
import unittest
from xml.sax.saxutils import escape

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import shadow_build  # noqa: E402

WORK = os.path.join(shadow_build.ROOT, ".build", "banned-symbols-selftest")

# Each statement line ends with "// banned" (must raise RS0030) or "// allowed" (must not).
SCRATCH = """using System.Threading.Tasks;

namespace TurboHTTP.BannedSymbolsSelfTest
{
    internal static class Scratch
    {
        internal static void ValueTaskGetResult() => new ValueTask().GetAwaiter().GetResult(); // banned
        internal static int ValueTaskOfTGetResult() => new ValueTask<int>(1).GetAwaiter().GetResult(); // banned
        internal static void TaskGetResult() => Task.CompletedTask.GetAwaiter().GetResult(); // banned
        internal static int TaskOfTGetResult() => Task.FromResult(1).GetAwaiter().GetResult(); // banned
        internal static int TaskResult() => Task.FromResult(1).Result; // banned
        internal static void TaskWait() => Task.CompletedTask.Wait(); // banned

        internal static async Task<int> Awaited()
        {
            await new ValueTask().ConfigureAwait(false); // allowed
            return await new ValueTask<int>(1).ConfigureAwait(false); // allowed
        }
    }
}
"""


def marked_lines(marker):
    return {i for i, line in enumerate(SCRATCH.splitlines(), 1) if line.rstrip().endswith("// " + marker)}


@unittest.skipUnless(shutil.which("dotnet"), "dotnet SDK not on PATH")
class SyncOverAsyncBanTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # A fresh directory every run: an up-to-date incremental build would not re-report warnings.
        shutil.rmtree(WORK, ignore_errors=True)
        os.makedirs(WORK)
        cls.scratch = os.path.join(WORK, "Scratch.cs")
        with open(cls.scratch, "w", encoding="utf-8") as f:
            f.write(SCRATCH)
        project = os.path.join(WORK, "BannedSymbolsSelfTest.csproj")
        with open(project, "w", encoding="utf-8") as f:
            f.write("\n".join([
                '<Project Sdk="Microsoft.NET.Sdk">',
                f'  <Import Project="{escape(os.path.join(shadow_build.CI, "Shadow.props"))}" />',
                "  <PropertyGroup><AssemblyName>TurboHTTP.BannedSymbolsSelfTest</AssemblyName></PropertyGroup>",
                f'  <ItemGroup><Compile Include="{escape(cls.scratch)}" /></ItemGroup>',
                "</Project>", ""]))
        # The same build invocation and diagnostic parser as the shadow build.
        cls.code, cls.diags, cls.output = shadow_build.build(project)

    def rs0030(self):
        return [d for d in self.diags
                if d["rule"] == "RS0030" and os.path.basename(d["file"]) == "Scratch.cs"]

    def test_scratch_project_builds(self):
        errors = [d for d in self.diags if d["kind"] == "error"]
        self.assertEqual(self.code, 0, self.output[-3000:])
        self.assertEqual(errors, [])

    def test_every_blocking_wait_raises_rs0030(self):
        hit = {int(d["line"]) for d in self.rs0030() if d["kind"] == "warning"}
        missing = sorted(marked_lines("banned") - hit)
        self.assertEqual(missing, [], "no RS0030 on scratch lines %s:\n%s" % (
            missing, "\n".join(SCRATCH.splitlines()[i - 1].strip() for i in missing)))

    def test_awaiting_is_not_flagged(self):
        flagged = sorted(marked_lines("allowed") & {int(d["line"]) for d in self.rs0030()})
        self.assertEqual(flagged, [])

    def test_shadow_build_reports_the_hits_as_new_warnings(self):
        found = collections.Counter(shadow_build.baseline_key(d) for d in self.rs0030())
        with open(shadow_build.BASELINE, encoding="utf-8") as f:
            base = collections.Counter(l.rstrip("\n") for l in f if l.strip() and not l.startswith("#"))
        new = found - base
        self.assertEqual(sum(new.values()), len(marked_lines("banned")))


if __name__ == "__main__":
    unittest.main()
