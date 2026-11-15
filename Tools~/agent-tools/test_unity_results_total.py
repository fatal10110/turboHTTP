"""Tests for the total-count gates of Tools~/unity/summarize_results.py (#903): a run that executed no test, and an
unfiltered run whose total fell below the run target's min-total entry in Tools~/unity/skip-baseline.txt."""
import importlib.util
import io
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent.parent / "unity" / "summarize_results.py"
BASELINE = SCRIPT.with_name("skip-baseline.txt")


def load_module():
    spec = importlib.util.spec_from_file_location("summarize_results_total", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def results_xml(total):
    cases = "".join(f'<test-case name="T{i}" fullname="N.T.T{i}" result="Passed" />' for i in range(total))
    return (f'<test-run total="{total}" passed="{total}" failed="0" skipped="0" inconclusive="0" duration="1">'
            f'<test-suite name="s">{cases}</test-suite></test-run>')


class SummarizeTotalsTest(unittest.TestCase):
    def setUp(self):
        self.module = load_module()
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.baseline = Path(self.tmp.name) / "baseline.txt"
        self.baseline.write_text("EditMode 0\nPlayMode 0\nmin-total EditMode 2\nmin-total PlayMode 5  # measured\n",
                                 encoding="utf-8")

    def run_summary(self, total, platform="PlayMode", filtered=False):
        path = Path(self.tmp.name) / "results.xml"
        path.write_text(results_xml(total), encoding="utf-8")
        out = io.StringIO()
        code = self.module.summarize(str(path), platform, filtered, str(self.baseline), out=out)
        return code, out.getvalue()

    def test_filtered_run_that_matched_no_test_fails(self):
        code, out = self.run_summary(0, filtered=True)
        self.assertEqual(4, code)
        self.assertIn("TOTAL GATE FAILED", out)
        self.assertIn("no test ran", out)

    def test_unfiltered_run_with_no_test_fails(self):
        code, out = self.run_summary(0)
        self.assertEqual(4, code)
        self.assertIn("no test ran", out)

    def test_unfiltered_run_below_the_minimum_fails_with_the_delta(self):
        code, out = self.run_summary(3)
        self.assertEqual(4, code)
        self.assertIn("total 3 < min-total 5 for PlayMode (2 fewer, ", out)

    def test_unfiltered_run_at_or_above_the_minimum_passes(self):
        for total in (5, 7):
            code, out = self.run_summary(total)
            self.assertEqual(0, code, out)
            self.assertIn(f"total={total} reaches min-total 5 for PlayMode", out)

    def test_filtered_run_needs_only_one_test(self):
        code, out = self.run_summary(1, filtered=True)
        self.assertEqual(0, code, out)
        self.assertIn("filtered run", out)

    def test_missing_min_total_entry_fails_unfiltered_run_only(self):
        self.baseline.write_text("PlayMode 0\n", encoding="utf-8")
        code, out = self.run_summary(9)
        self.assertEqual(4, code)
        self.assertIn("no min-total PlayMode entry", out)
        code, _ = self.run_summary(9, filtered=True)
        self.assertEqual(0, code)

    def test_min_total_lines_do_not_change_the_skip_baseline(self):
        self.assertEqual(0, self.module.read_baseline(str(self.baseline), "PlayMode"))
        self.assertEqual(5, self.module.read_min_total(str(self.baseline), "PlayMode"))
        self.assertEqual(2, self.module.read_min_total(str(self.baseline), "EditMode"))
        self.assertIsNone(self.module.read_min_total(str(self.baseline), "Player-Android"))

    def test_committed_minimums_cover_every_scripted_run_target(self):
        for key in ("EditMode", "PlayMode", "Player-Android"):
            value = self.module.read_min_total(str(BASELINE), key)
            self.assertIsNotNone(value, key)
            self.assertGreater(value, 0, key)
        # The skip baseline still reads its own two-column entries from the same file.
        self.assertEqual(0, self.module.read_baseline(str(BASELINE), "EditMode"))

    def test_cli_exits_4_for_a_run_with_no_test(self):
        path = Path(self.tmp.name) / "empty.xml"
        path.write_text(results_xml(0), encoding="utf-8")
        proc = subprocess.run([sys.executable, str(SCRIPT), str(path), "EditMode", "1", str(self.baseline)],
                              capture_output=True, text=True, timeout=60)
        self.assertEqual(4, proc.returncode, proc.stdout + proc.stderr)


if __name__ == "__main__":
    unittest.main()
