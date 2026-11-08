#!/usr/bin/env python3
"""Tests for platform_matrix.py (#105) on synthetic NUnit3 result files; no Unity needed."""
import io
import json
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stdout, redirect_stderr

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import platform_matrix as pm  # noqa: E402

NS = "TurboHTTP.Tests.UnityModule"


def case(cls, name, result="Passed", output=None, label=None):
    attrs = f'name="{name}" fullname="{NS}.{cls}.{name}" classname="{NS}.{cls}" result="{result}"'
    if label:
        attrs += f' label="{label}"'
    body = f"<output><![CDATA[{output}]]></output>" if output else ""
    return f"<test-case {attrs}>{body}</test-case>"


def results_xml(*cases):
    return ('<?xml version="1.0" encoding="utf-8"?>'
            '<test-run id="2" testcasecount="0" result="Passed" total="0" passed="0" failed="0" '
            'start-time="2026-11-08 10:00:00Z" duration="12.5">'
            '<test-suite type="Assembly" name="TurboHTTP.Tests.Runtime.dll">' + "".join(cases) +
            "</test-suite></test-run>")


METRIC = ("TURBOHTTP_METRIC name=texture_burst.max_frame_work_ms value=3.25 budget=25 unit=ms "
          "profile=editor result=pass\nsome other log line\n"
          "TURBOHTTP_METRIC name=texture_burst.frames value=17 unit=frames profile=editor")


class PlatformMatrixTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)

    def write(self, name, text):
        path = os.path.join(self.tmp.name, name)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(text)
        return path

    def run_main(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = pm.main(list(argv))
        return code, out.getvalue(), err.getvalue()

    def test_metadata_from_runner_file_names(self):
        self.assertEqual(pm.metadata_from_name("/c/results-PlayMode-20261108-101500.xml")["run"], "PlayMode")
        meta = pm.metadata_from_name("results-Android-High-x86_64-api33-emulator-5554-20261108-101500.xml")
        self.assertEqual((meta["target"], meta["backend"], meta["stripping"], meta["arch"], meta["api"], meta["device"]),
                         ("Android", "IL2CPP", "High", "x86_64", "33", "emulator"))
        self.assertEqual(meta["serial"], "emulator-5554")
        phone = pm.metadata_from_name("results-Android-Low-arm64-api34-20261108-101500.xml")
        self.assertEqual((phone["arch"], phone["device"], phone["stripping"]), ("arm64", "device", "Low"))
        self.assertNotIn("serial", phone)
        self.assertEqual(pm.metadata_from_name("results.xml"), {})

    def test_parse_metric_lines(self):
        metric = pm.parse_metric("TURBOHTTP_METRIC name=a.b value=1.5 budget=2 unit=ms profile=mobile result=pass")
        self.assertEqual(metric, {"name": "a.b", "value": 1.5, "budget": 2.0, "unit": "ms", "profile": "mobile",
                                  "result": "pass"})
        self.assertIsNone(pm.parse_metric("TURBOHTTP_METRIC name=a value=notanumber"))
        self.assertIsNone(pm.parse_metric("TURBOHTTP_METRIC value=1"))
        self.assertIsNone(pm.parse_metric("log TURBOHTTP_METRIC name=a value=1"))

    def test_row_counts_suites_and_collects_metrics(self):
        path = self.write("results-PlayMode-20261108-101500.xml", results_xml(
            case("UnityStressSuiteTests", "TextureBurst", output=METRIC),
            case("UnityStressSuiteTests", "AudioChurn", result="Failed"),
            case("UnityReliabilitySuiteTests", "Flood"),
            case("OtherTests", "Skipped", result="Skipped", label="Ignored"),
        ))
        row = pm.build_row(path, unity_version="2021.3.45f2", commit="abc")
        self.assertEqual(row["id"], "editor-playmode")
        self.assertEqual(row["totals"], {"total": 4, "passed": 2, "failed": 1, "skipped": 1, "inconclusive": 0})
        self.assertEqual(row["status"], "fail")
        self.assertEqual(row["suites"][f"{NS}.UnityStressSuiteTests"]["status"], "fail")
        self.assertEqual(row["suites"][f"{NS}.UnityReliabilitySuiteTests"]["status"], "pass")
        self.assertEqual(row["suites"][f"{NS}.OtherTests"]["status"], "skipped")
        self.assertEqual(row["certification_suites"], ["UnityReliabilitySuiteTests", "UnityStressSuiteTests"])
        self.assertEqual([m["name"] for m in row["metrics"]], ["texture_burst.frames", "texture_burst.max_frame_work_ms"])
        self.assertEqual(row["metrics"][1]["budget"], 25.0)
        self.assertEqual(row["unity_version"], "2021.3.45f2")
        self.assertEqual(row["duration_s"], 12.5)

    def test_cells_mark_unrun_targets_and_policy_disabled(self):
        path = self.write("results-Android-High-x86_64-api33-emulator-5554-20261108-101500.xml",
                          results_xml(case("UnityReliabilitySuiteTests", "Flood")))
        matrix = pm.build_matrix([pm.build_row(path)], generated="t")
        cells = {c["id"]: c for c in matrix["cells"]}
        self.assertEqual(matrix["schema"], "turbohttp.unity-platform-matrix")
        self.assertEqual(matrix["schema_version"], 1)
        self.assertEqual(cells["android-il2cpp-x86_64-emulator"]["status"], "pass")
        self.assertEqual(cells["android-il2cpp-x86_64-emulator"]["rows"], ["android-il2cpp-x86_64-api33-high-emulator"])
        self.assertEqual(cells["android-il2cpp-x86_64-emulator"]["missing_certification_suites"],
                         ["UnityStressSuiteTests", "UnityPerformanceBudgetTests"])
        self.assertEqual(cells["android-il2cpp-arm64"]["status"], "needs-device-validation")
        self.assertEqual(cells["ios-il2cpp"]["status"], "needs-device-validation")
        self.assertEqual(cells["webgl"]["status"], "policy-disabled")
        self.assertEqual(cells["editor-playmode-mono"]["status"], "not-run")
        self.assertEqual([c["id"] for c in matrix["cells"]], [c["id"] for c in pm.CELLS], "cell ids and order are stable")

    def test_empty_run_is_no_tests(self):
        path = self.write("results-EditMode-20261108-101500.xml", results_xml())
        matrix = pm.build_matrix([pm.build_row(path)])
        cell = next(c for c in matrix["cells"] if c["id"] == "editor-editmode-mono")
        self.assertEqual(cell["status"], "no-tests")
        self.assertNotIn("missing_certification_suites", cell, "EditMode does not run the runtime suites")

    def test_main_merges_rows_by_id_and_gates_on_fail(self):
        out = os.path.join(self.tmp.name, "out", "matrix.json")
        playmode = self.write("results-PlayMode-20261108-101500.xml", results_xml(case("UnityStressSuiteTests", "A")))
        code, stdout, _ = self.run_main("--out", out, "--set", "commit=c1", playmode)
        self.assertEqual(code, 0)
        self.assertIn("editor-playmode-mono", stdout)

        failing = self.write("results-Android-High-x86_64-api33-20261108-111500.xml",
                             results_xml(case("UnityStressSuiteTests", "A", result="Failed")))
        code, _, _ = self.run_main("--out", out, "--gate", failing)
        self.assertEqual(code, 1)
        with open(out, encoding="utf-8") as fh:
            matrix = json.load(fh)
        self.assertEqual(sorted(r["id"] for r in matrix["rows"]),
                         ["android-il2cpp-x86_64-api33-high-emulator", "editor-playmode"])

        rerun = self.write("results-PlayMode-20261108-121500.xml", results_xml(case("UnityStressSuiteTests", "A")))
        self.run_main("--out", out, rerun)
        with open(out, encoding="utf-8") as fh:
            rows = {r["id"]: r for r in json.load(fh)["rows"]}
        self.assertEqual(rows["editor-playmode"]["source"], "results-PlayMode-20261108-121500.xml", "same id replaces")
        self.assertEqual(len(rows), 2)

        code, _, _ = self.run_main("--out", out, "--fresh", rerun)
        with open(out, encoding="utf-8") as fh:
            self.assertEqual([r["id"] for r in json.load(fh)["rows"]], ["editor-playmode"])
        self.assertEqual(code, 0)

    def test_set_overrides_name_and_rejects_unknown_keys(self):
        ios = self.write("results.xml", results_xml(case("UnityReliabilitySuiteTests", "Flood")))
        out = os.path.join(self.tmp.name, "m.json")
        code, _, err = self.run_main("--out", out, ios)
        self.assertEqual(code, 2)
        self.assertIn("target unknown", err)
        code, _, _ = self.run_main("--out", out, "--set", "target=iOS", "--set", "backend=IL2CPP",
                                   "--set", "device=simulator", "--set", "unity_version=6000.0.40f1", ios)
        self.assertEqual(code, 0)
        with open(out, encoding="utf-8") as fh:
            matrix = json.load(fh)
        cell = next(c for c in matrix["cells"] if c["id"] == "ios-il2cpp")
        self.assertEqual(cell["status"], "pass")
        self.assertEqual(matrix["rows"][0]["unity_version"], "6000.0.40f1")
        code, _, err = self.run_main("--out", out, "--set", "color=red", ios)
        self.assertEqual(code, 2)


if __name__ == "__main__":
    unittest.main()
