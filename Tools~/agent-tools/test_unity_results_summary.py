"""Tests for the skip gates of Tools~/unity/summarize_results.py (#151, #190)."""
import importlib.util
import io
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent.parent / "unity" / "summarize_results.py"
BASELINE = SCRIPT.with_name("skip-baseline.txt")


def load_module():
    spec = importlib.util.spec_from_file_location("summarize_results", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def results_xml(*skips, failed=False, inconclusive=()):
    cases = ['<test-case name="Ok" fullname="N.T.Ok" result="Passed" />']
    for i, reason in enumerate(skips):
        body = f"<reason><message><![CDATA[{reason}]]></message></reason>" if reason is not None else ""
        cases.append(f'<test-case name="S{i}" fullname="N.T.Skip{i}" result="Skipped" label="Ignored">{body}</test-case>')
    for i, reason in enumerate(inconclusive):
        cases.append(f'<test-case name="I{i}" fullname="N.T.Assume{i}" result="Inconclusive">'
                     f'<reason><message><![CDATA[{reason}]]></message></reason></test-case>')
    if failed:
        cases.append('<test-case name="Bad" fullname="N.T.Bad" result="Failed"><failure><message>boom</message></failure></test-case>')
    return ('<test-run total="3" passed="1" failed="%d" skipped="%d" inconclusive="0" duration="1">'
            '<test-suite name="s">%s</test-suite></test-run>' % (1 if failed else 0, len(skips), "".join(cases)))


class SummarizeResultsTest(unittest.TestCase):
    def setUp(self):
        self.module = load_module()
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.baseline = Path(self.tmp.name) / "baseline.txt"
        self.baseline.write_text("# comment\nEditMode 0\nPlayMode 2  # two env skips\n", encoding="utf-8")

    def run_summary(self, xml, platform="PlayMode", filtered=False):
        path = Path(self.tmp.name) / "results.xml"
        path.write_text(xml, encoding="utf-8")
        out = io.StringIO()
        code = self.module.summarize(str(path), platform, filtered, str(self.baseline), out=out)
        return code, out.getvalue()

    def test_inconclusive_counts_as_skipped(self):
        code, out = self.run_summary(results_xml(inconclusive=["[env] window open", "no reason marker"]))
        self.assertEqual(4, code)
        self.assertIn("SKIP N.T.Assume1: no reason marker", out)
        self.assertIn("SKIP GATE FAILED", out)
        code, _ = self.run_summary(results_xml(inconclusive=["[env] a", "[env] b", "[env] c"]))
        self.assertEqual(4, code)  # three > baseline 2

    def test_within_baseline_with_explained_skips_passes(self):
        code, out = self.run_summary(results_xml("[env] no network", "tracked in #151"))
        self.assertEqual(0, code)
        self.assertIn("SKIP N.T.Skip0: [env] no network", out)
        self.assertIn("within baseline 2", out)

    def test_more_skips_than_baseline_fails_unfiltered_run(self):
        code, out = self.run_summary(results_xml("[env] a", "[env] b", "[env] c"))
        self.assertEqual(4, code)
        self.assertIn("3 skipped > baseline 2", out)

    def test_filtered_run_does_not_apply_the_baseline(self):
        code, out = self.run_summary(results_xml("[env] a", "[env] b", "[env] c"), filtered=True)
        self.assertEqual(0, code)
        self.assertIn("filtered run", out)

    def test_unexplained_skip_fails_even_on_a_filtered_run(self):
        code, out = self.run_summary(results_xml("flaky on CI"), filtered=True)
        self.assertEqual(4, code)
        self.assertIn("no #issue or [env] marker", out)

    def test_skip_without_reason_fails(self):
        code, _ = self.run_summary(results_xml(None), filtered=True)
        self.assertEqual(4, code)

    def test_missing_platform_entry_fails_unfiltered_run(self):
        self.baseline.write_text("EditMode 0\n", encoding="utf-8")
        code, out = self.run_summary(results_xml(), platform="PlayMode")
        self.assertEqual(4, code)
        self.assertIn("no PlayMode entry", out)

    def test_no_skips_passes_and_failures_are_listed(self):
        code, out = self.run_summary(results_xml(failed=True), platform="EditMode")
        self.assertEqual(0, code)
        self.assertIn("FAIL N.T.Bad: boom", out)

    def test_committed_baseline_covers_both_platforms(self):
        self.assertIsNotNone(self.module.read_baseline(str(BASELINE), "EditMode"))
        self.assertIsNotNone(self.module.read_baseline(str(BASELINE), "PlayMode"))


if __name__ == "__main__":
    unittest.main()
