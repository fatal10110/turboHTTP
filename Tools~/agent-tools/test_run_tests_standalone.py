"""Tests for run-tests.sh --platform StandaloneWindows64 (#404) and its total gate (#903), with a faked Windows editor.

The Windows side is the stub set of test_wsl_interop.py (cmd.exe, wslpath, rsync); the stub Unity.exe records its
arguments and writes FAKE_RESULTS (a one-case pass by default) at -testResults, like the Test Runner does after a
player run. platform_matrix.py then reads the result file the script named.
"""
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

from test_wsl_interop import CMD, PRELUDE, RSYNC, WSLPATH

REPO = pathlib.Path(__file__).resolve().parents[1].parent
UNITY_DIR = REPO / "Tools~" / "unity"

UNITY = PRELUDE + r"""printf '%s\n' "$*" >> "$FAKE_WSL.unity.calls"
log=""; xml=""
while [ $# -gt 0 ]; do
  case "$1" in -logFile) log="$2"; shift ;; -testResults) xml="$2"; shift ;; esac
  shift
done
[ -z "$log" ] || echo "fake unity log" > "$log"
[ -z "$xml" ] || cat "$FAKE_RESULTS" > "$xml"
exit "${FAKE_UNITY_EXIT:-0}"
"""


def results_xml(*suites, total=None):
    names = suites or ("Fake",)
    cases = "".join(f'<test-case name="A" fullname="TurboHTTP.Tests.UnityModule.{s}.A" '
                    f'classname="TurboHTTP.Tests.UnityModule.{s}" result="Passed" />' for s in names)
    count = len(names) if total is None else total
    if count == 0:
        cases = ""
    return (f'<test-run total="{count}" passed="{count}" failed="0" skipped="0" inconclusive="0" duration="0.1">'
            f'<test-suite type="Assembly" name="TurboHTTP.Tests.Runtime.dll">{cases}</test-suite></test-run>')


class StandaloneRunTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        root = pathlib.Path(self.tmp.name)
        self.root = root
        bin_dir = root / "bin"
        for name, body in (("cmd.exe", CMD), ("wslpath", WSLPATH), ("rsync", RSYNC)):
            self.write_exe(bin_dir / name, body)
        self.unity = root / "Editor" / "Unity.exe"
        self.write_exe(self.unity, UNITY)
        self.state = root / "state"
        self.localappdata = root / "localappdata"
        self.localappdata.mkdir()
        self.results = root / "fake-results.xml"
        self.results.write_text(results_xml())
        self.host_proj = self.project("host-proj")
        self.player_proj = self.project("player-proj")
        self.env = dict(os.environ, PATH=f"{bin_dir}{os.pathsep}{os.environ['PATH']}", FAKE_WSL=str(self.state),
                        FAKE_LOCALAPPDATA=str(self.localappdata), FAKE_RESULTS=str(self.results), wsl_poll_s="0",
                        UNITY_PATH=str(self.unity), UNITY_TEST_PROJECT=str(self.host_proj),
                        UNITY_STANDALONE_TEST_PROJECT=str(self.player_proj), UNITY_PACKAGE_COPY=str(root / "pkg"))

    @staticmethod
    def write_exe(path, body):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body)
        path.chmod(0o755)

    def project(self, name):
        proj = self.root / name
        (proj / "ProjectSettings").mkdir(parents=True)
        (proj / "Packages").mkdir()
        (proj / "ProjectSettings" / "ProjectSettings.asset").write_text("PlayerSettings:\n  scriptingDefineSymbols: {}\n")
        (proj / "Packages" / "manifest.json").write_text("{}")
        return proj

    def run_tests(self, *args, **env):
        return subprocess.run(["bash", str(UNITY_DIR / "run-tests.sh"), *args], env=dict(self.env, **env),
                              capture_output=True, text=True, timeout=120)

    def unity_calls(self):
        path = pathlib.Path(f"{self.state}.unity.calls")
        return path.read_text().splitlines() if path.exists() else []

    def result_files(self):
        return sorted(p.name for p in (self.localappdata / "turbohttp").glob("results-*.xml"))

    def test_mono_player_run_builds_with_the_test_runner_and_names_its_results(self):
        result = self.run_tests("--platform", "StandaloneWindows64", "--backend", "Mono", "--filter", "Fake")
        out = result.stdout + result.stderr
        self.assertEqual(result.returncode, 0, out)
        self.assertIn("run-tests: total=1 passed=1", result.stdout)
        [call] = self.unity_calls()
        self.assertIn("-runTests -testPlatform StandaloneWindows64", call)
        self.assertIn("-buildTarget StandaloneWindows64 -turbohttpBackend Mono", call)
        self.assertIn(str(self.player_proj), call, "the player builds in its own project")
        [name] = self.result_files()
        self.assertRegex(name, r"^results-StandaloneWindows64-Mono-\d{8}-\d{6}\.xml$")
        hook = self.player_proj / "Assets" / "Editor" / "TurboHttpStandaloneTestBuild.cs"
        self.assertIn("ITestPlayerBuildModifier", hook.read_text())
        self.assertFalse((self.host_proj / "Assets").exists(), "the host project never gets the player hook")

    def test_il2cpp_backend_reaches_the_build_hook(self):
        result = self.run_tests("--platform", "StandaloneWindows64", "--backend", "IL2CPP", "--filter", "Fake")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("-turbohttpBackend IL2CPP", self.unity_calls()[0])
        self.assertRegex(self.result_files()[0], r"^results-StandaloneWindows64-IL2CPP-")

    def test_player_results_fill_the_standalone_windows_cell(self):
        self.results.write_text(results_xml("UnityReliabilitySuiteTests", "UnityStressSuiteTests",
                                            "UnityPerformanceBudgetTests"))
        result = self.run_tests("--platform", "StandaloneWindows64", "--backend", "Mono", "--filter", "Unity")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        xml = self.localappdata / "turbohttp" / self.result_files()[0]
        out = self.root / "matrix.json"
        matrix = subprocess.run([sys.executable, str(UNITY_DIR / "platform_matrix.py"), "--out", str(out), "--fresh",
                                 "--gate", str(xml)], capture_output=True, text=True, timeout=60)
        self.assertEqual(matrix.returncode, 0, matrix.stdout + matrix.stderr)
        cell = next(c for c in json.loads(out.read_text())["cells"] if c["id"] == "standalone-windows")
        self.assertEqual((cell["status"], cell["rows"]), ("pass", ["standalonewindows64-mono-x86_64-disabled-host"]))

    def test_unfiltered_player_run_is_held_to_its_own_baseline_key(self):
        result = self.run_tests("--platform", "StandaloneWindows64", "--backend", "Mono")
        self.assertEqual(result.returncode, 4, result.stdout + result.stderr)
        self.assertIn("no Player-StandaloneWindows64 entry", result.stdout)

    def test_backend_is_required_for_the_player_and_rejected_for_the_editor(self):
        result = self.run_tests("--platform", "StandaloneWindows64", "--filter", "Fake")
        self.assertEqual(result.returncode, 2)
        self.assertIn("needs --backend Mono|IL2CPP", result.stderr)
        result = self.run_tests("--platform", "PlayMode", "--backend", "Mono", "--filter", "Fake")
        self.assertEqual(result.returncode, 2)
        self.assertEqual(self.unity_calls(), [])

    def test_player_run_needs_a_windows_editor(self):
        linux_unity = self.root / "Linux" / "Unity"
        self.write_exe(linux_unity, UNITY)
        result = self.run_tests("--platform", "StandaloneWindows64", "--backend", "Mono", "--filter", "Fake",
                                UNITY_PATH=str(linux_unity))
        self.assertEqual(result.returncode, 2)
        self.assertIn("needs a Windows editor", result.stderr)
        self.assertEqual(self.unity_calls(), [])

    def test_filter_that_matches_no_test_exits_4(self):
        self.results.write_text(results_xml(total=0))
        for args in (("--platform", "EditMode", "--filter", "Http2FlowControl"),
                     ("--platform", "StandaloneWindows64", "--backend", "Mono", "--filter", "NoSuchTestXyz")):
            result = self.run_tests(*args)
            self.assertEqual(result.returncode, 4, result.stdout + result.stderr)
            self.assertIn("no test ran (total=0)", result.stdout)


if __name__ == "__main__":
    unittest.main()
