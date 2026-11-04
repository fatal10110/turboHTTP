"""Tests for Tools~/unity/wsl-interop.sh and the Unity scripts that use it (#355).

The Windows side is faked: stub cmd.exe, wslpath, Unity.exe and adb.exe follow a per-tool plan file
(<state>.<tool>.plan, one word per call: "ok" or "fail"; a trailing "*" repeats the word forever), so a WSL
interop outage of any length runs on any host with bash, with the back-off at 0 seconds (wsl_poll_s=0).
A failing call behaves like an outage: no output, WSL's UtilAcceptVsock error on stderr, exit 1.
"""
import os
import pathlib
import subprocess
import tempfile
import unittest

REPO = pathlib.Path(__file__).resolve().parents[1].parent
UNITY_DIR = REPO / "Tools~" / "unity"
LIB = UNITY_DIR / "wsl-interop.sh"

# Prints "ok" or "fail" for tool $1 and consumes the plan's first word (a word ending in "*" is kept).
PRELUDE = r"""#!/usr/bin/env bash
plan_next() {
  local f="$FAKE_WSL.$1.plan" words
  [ -f "$f" ] || { echo ok; return; }
  read -r -a words < "$f" || true
  [ "${#words[@]}" -gt 0 ] || { echo ok; return; }
  case "${words[0]}" in *'*') echo "${words[0]%'*'}"; return ;; esac
  echo "${words[0]}"
  echo "${words[*]:1}" > "$f"
}
outage() { echo '<3>WSL (4242) ERROR: UtilAcceptVsock:281: accept4 failed 110' >&2; exit 1; }
"""

CMD = PRELUDE + r"""echo "$*" >> "$FAKE_WSL.cmd.calls"
[ "$(plan_next cmd)" = ok ] || outage
case "$*" in
  *LOCALAPPDATA*) printf '%s\r\n' "${FAKE_CMD_LOCALAPPDATA:-C:\\Users\\x\\AppData\\Local}" ;;
  *) a="$*"; printf '%s\r\n' "${a#/c echo }" ;;
esac
"""

WSLPATH = r"""#!/usr/bin/env bash
case "$1" in -u) echo "$FAKE_LOCALAPPDATA" ;; *) echo "$2" ;; esac
"""

# Writes its -logFile and, for -runTests, a one-case NUnit result at -testResults.
UNITY = PRELUDE + r"""echo "$*" >> "$FAKE_WSL.unity.calls"
[ "$(plan_next unity)" = ok ] || outage
log=""; xml=""
while [ $# -gt 0 ]; do
  case "$1" in -logFile) log="$2"; shift ;; -testResults) xml="$2"; shift ;; esac
  shift
done
[ -z "$log" ] || echo "fake unity log" > "$log"
if [ -n "$xml" ]; then
  cat > "$xml" <<'XML'
<test-run total="1" passed="1" failed="0" skipped="0" inconclusive="0" duration="0.1">
  <test-case name="T" fullname="Fake.T" result="Passed" />
</test-run>
XML
fi
"""

ADB = PRELUDE + r"""echo "$*" >> "$FAKE_WSL.adb.calls"
[ "$(plan_next adb)" = ok ] || outage
case "$*" in *get-state*) printf 'device\r\n' ;; esac
"""

# The stop point of the script tests: the scripts sync the package right after the lookups and adb checks.
RSYNC = """#!/usr/bin/env bash
echo "$*" >> "$FAKE_WSL.rsync.calls"
exit "${FAKE_RSYNC_EXIT:-0}"
"""

INTEROP = "WSL interop unavailable"


class WslInteropTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = pathlib.Path(self.tmp.name)
        self.root = root
        bin_dir = root / "bin"
        bin_dir.mkdir()
        for name, body in (("cmd.exe", CMD), ("wslpath", WSLPATH), ("rsync", RSYNC)):
            self.write_exe(bin_dir / name, body)
        # A Windows editor layout: Unity.exe with the Android SDK's adb.exe beside it.
        self.unity = root / "Editor" / "Unity.exe"
        self.write_exe(self.unity, UNITY)
        tools = root / "Editor" / "Data" / "PlaybackEngines" / "AndroidPlayer"
        self.write_exe(tools / "SDK" / "platform-tools" / "adb.exe", ADB)
        (tools / "Variations" / "il2cpp").mkdir(parents=True)
        self.state = root / "state"
        self.localappdata = root / "localappdata"
        self.localappdata.mkdir()
        self.env = dict(os.environ, PATH=f"{bin_dir}{os.pathsep}{os.environ['PATH']}", FAKE_WSL=str(self.state),
                        FAKE_LOCALAPPDATA=str(self.localappdata), wsl_poll_s="0", UNITY_PATH=str(self.unity))

    def tearDown(self):
        self.tmp.cleanup()

    @staticmethod
    def write_exe(path, body):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body)
        path.chmod(0o755)

    def plan(self, tool, words):
        pathlib.Path(f"{self.state}.{tool}.plan").write_text(words + "\n")

    def calls(self, tool):
        path = pathlib.Path(f"{self.state}.{tool}.calls")
        return path.read_text().splitlines() if path.exists() else []

    def run_lib(self, call):
        script = f'set -euo pipefail; . "$1"; {call}'
        return subprocess.run(["bash", "-c", script, "_", str(LIB)], env=self.env,
                              capture_output=True, text=True, timeout=60)

    def run_script(self, name, *args, **env):
        return subprocess.run(["bash", str(UNITY_DIR / name), *args], env=dict(self.env, **env),
                              capture_output=True, text=True, timeout=120)

    def assert_interop_exit(self, result, script):
        self.assertEqual(result.returncode, 5, result.stdout + result.stderr)
        self.assertIn(f"{script}: {INTEROP}", result.stderr)
        self.assertNotIn("compile errors?", result.stdout + result.stderr)

    # --- the library ---

    def test_lookup_survives_an_outage_shorter_than_the_backoff(self):
        self.plan("cmd", "fail fail fail ok")
        result = self.run_lib('wsl_resolve_localappdata t; echo "dir=$wsl_localappdata"')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(f"dir={self.localappdata}", result.stdout)
        self.assertEqual(len(self.calls("cmd")), 4)

    def test_lookup_reports_a_longer_outage_as_interop_unavailable(self):
        self.plan("cmd", "fail*")
        result = self.run_lib("wsl_resolve_localappdata t")
        self.assert_interop_exit(result, "t")
        self.assertEqual(len(self.calls("cmd")), 8)

    def test_lookup_answer_that_is_not_a_path_is_a_setup_error(self):
        result = self.run_lib("wsl_resolve_localappdata t")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.env["FAKE_CMD_LOCALAPPDATA"] = "%LOCALAPPDATA%"
        result = self.run_lib("wsl_resolve_localappdata t")
        self.assertEqual(result.returncode, 2, result.stderr)
        self.assertNotIn(INTEROP, result.stderr)

    def test_unity_start_that_wrote_no_log_is_retried(self):
        self.plan("unity", "fail ok")
        log = self.root / "u.log"
        result = self.run_lib(f'win=1; wsl_unity_launch t "{log}" "$UNITY_PATH" -logFile "{log}"; echo "code=$unity_code"')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("code=0", result.stdout)
        self.assertEqual(len(self.calls("unity")), 2)
        self.assertTrue(log.exists())

    def test_unity_that_never_starts_is_interop_unavailable(self):
        self.plan("unity", "fail*")
        log = self.root / "u.log"
        result = self.run_lib(f'win=1; wsl_unity_launch t "{log}" "$UNITY_PATH" -logFile "{log}"')
        self.assert_interop_exit(result, "t")
        self.assertEqual(len(self.calls("unity")), 8)

    def test_unity_that_started_and_failed_is_not_retried(self):
        log = self.root / "u.log"
        result = self.run_lib(f'win=1; wsl_unity_launch t "{log}" bash -c \'echo x > "$1"; exit 3\' _ "{log}";'
                              ' echo "code=$unity_code"')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("code=3", result.stdout)

    def test_stale_log_does_not_hide_a_failed_start(self):
        self.plan("unity", "fail*")
        log = self.root / "u.log"
        log.write_text("from an earlier run")
        result = self.run_lib(f'win=1; wsl_tries=2; wsl_unity_launch t "{log}" "$UNITY_PATH" -logFile "{log}"')
        self.assert_interop_exit(result, "t")

    def test_unity_off_wsl_is_one_attempt_and_a_setup_error(self):
        self.plan("unity", "fail*")
        log = self.root / "u.log"
        result = self.run_lib(f'win=0; wsl_unity_launch t "{log}" "$UNITY_PATH" -logFile "{log}"')
        self.assertEqual(result.returncode, 2, result.stderr)
        self.assertNotIn(INTEROP, result.stderr)
        self.assertEqual(len(self.calls("unity")), 1)

    # --- run-tests.sh end to end (rsync stubbed, a prepared test project) ---

    def project_env(self):
        proj = self.root / "proj"
        (proj / "ProjectSettings").mkdir(parents=True)
        (proj / "Packages").mkdir()
        (proj / "ProjectSettings" / "ProjectSettings.asset").write_text("PlayerSettings:\n  scriptingDefineSymbols: {}\n")
        (proj / "Packages" / "manifest.json").write_text("{}")
        return dict(UNITY_TEST_PROJECT=str(proj), UNITY_PACKAGE_COPY=str(self.root / "pkg"))

    def test_run_tests_survives_a_short_outage_at_lookup_and_unity_start(self):
        self.plan("cmd", "fail fail ok")
        self.plan("unity", "fail fail ok")
        result = self.run_script("run-tests.sh", "--platform", "EditMode", "--filter", "Fake", **self.project_env())
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("run-tests: total=1 passed=1", result.stdout)
        self.assertEqual(len(self.calls("unity")), 3)

    def test_run_tests_reports_a_unity_that_never_starts_as_interop_unavailable(self):
        self.plan("unity", "fail*")
        result = self.run_script("run-tests.sh", "--platform", "EditMode", "--filter", "Fake", **self.project_env())
        self.assert_interop_exit(result, "run-tests")

    def test_run_tests_lookup_outage_is_interop_unavailable(self):
        self.plan("cmd", "fail*")
        result = self.run_script("run-tests.sh", "--platform", "EditMode")
        self.assert_interop_exit(result, "run-tests")
        self.assertEqual(self.calls("rsync"), [])

    # --- the other scripts' lookups and run-device-tests.sh's adb readiness wait ---

    def test_every_script_reports_a_lookup_outage_as_interop_unavailable(self):
        self.plan("cmd", "fail*")
        for name, args, tag in (("build-player.sh", ["--target", "android-il2cpp"], "build-player"),
                                ("run-device-tests.sh", [], "run-device-tests"),
                                ("run-emulator-tests.sh", ["--api", "29"], "run-emulator-tests"),
                                ("run-samples.sh", [], "run-samples")):
            with self.subTest(name):
                self.assert_interop_exit(self.run_script(name, *args), tag)

    def test_adb_readiness_survives_a_short_outage(self):
        # The lookup answers, then adb.exe and the cmd.exe probe both fail twice: an outage, not a missing device.
        self.plan("cmd", "ok fail fail ok")
        self.plan("adb", "fail fail ok")
        result = self.run_script("run-device-tests.sh", FAKE_RSYNC_EXIT="42")
        self.assertEqual(result.returncode, 42, result.stdout + result.stderr)
        self.assertNotIn("no Android device", result.stderr)

    def test_adb_readiness_reports_a_longer_outage_as_interop_unavailable(self):
        self.plan("cmd", "ok fail*")
        self.plan("adb", "fail*")
        result = self.run_script("run-device-tests.sh")
        self.assert_interop_exit(result, "run-device-tests")
        self.assertNotIn("no Android device", result.stderr)

    def test_adb_error_with_interop_up_is_still_a_missing_device(self):
        self.plan("adb", "fail*")
        result = self.run_script("run-device-tests.sh")
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("no Android device", result.stderr)
        self.assertEqual(len(self.calls("adb")), 5)

    def test_emulator_boot_failure_during_an_outage_is_exit_5(self):
        text = (UNITY_DIR / "run-emulator-tests.sh").read_text()
        func = "boot_failed() {" + text.split("\nboot_failed() {", 1)[1].split("\n}\n", 1)[0] + "\n}\n"
        call = func + 'rc=0; boot_failed 29 || rc=$?; echo "rc=$rc"'
        self.assertIn("rc=1", self.run_lib("win=1; " + call).stdout)
        self.plan("cmd", "fail*")
        result = self.run_lib("win=1; " + call)
        self.assertIn("rc=5", result.stdout)
        self.assertIn(f"run-emulator-tests: {INTEROP}", result.stderr)

    def test_scripts_start_unity_through_the_helper(self):
        for name in ("run-tests.sh", "build-player.sh", "run-device-tests.sh", "run-samples.sh"):
            with self.subTest(name):
                text = (UNITY_DIR / name).read_text()
                self.assertTrue('. "$repo/Tools~/unity/wsl-interop.sh"' in text, f"{name} does not source wsl-interop.sh")
                self.assertTrue("wsl_unity_launch " in text, f"{name} does not use wsl_unity_launch")
                lines = text.splitlines()
                for i, line in enumerate(lines):
                    if line.lstrip().startswith('"$unity" '):
                        self.assertIn("wsl_unity_launch ", lines[i - 1], f"{name}:{i + 1} starts Unity directly")
                self.assertFalse("for _ in 1 2 3; do\n    localappdata=" in text, f"{name} keeps its own lookup")


if __name__ == "__main__":
    unittest.main()
