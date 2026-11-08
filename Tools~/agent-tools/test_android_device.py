"""Tests for the player liveness and launch checks of Tools~/unity/run-device-tests.sh (android-adb.sh, #412).

The phone is faked: a stub adb answers every call the script makes, and the player's process follows a plan
file (<state>.proc.plan, one word per process check; a trailing "*" repeats the word forever):
  live    the player runs: pidof prints its pid, ps lists it in the middle of about 2000 lines (150 KB, as on a
          phone with several user profiles, where `grep -q` under pipefail misread a live player as gone)
  gone    a complete answer without the player
  empty   an answer with no output (adb or WSL interop dropped it)
  cut     a truncated ps listing (no end marker), or an empty pidof answer
  fail    adb fails (the script's adbr retries 5 times; each try takes a word)
  finish  the player runs and has just written its result file
The result file exists once a "finish" word was used, or (FAKE_RESULT_ON_LAUNCH=1) once the player was launched.
The script runs with --serial --skip-build (no Unity), its poll pauses at 0 seconds.
"""
import os
import pathlib
import re
import subprocess
import tempfile
import unittest

from test_wsl_interop import PRELUDE

REPO = pathlib.Path(__file__).resolve().parents[1].parent
UNITY_DIR = REPO / "Tools~" / "unity"
PKG = "com.turbohttp.devicetests"
SERIAL = "80917dd4"

ADB = PRELUDE + r"""[ "$1" = -s ] && shift 2
echo "$*" >> "$FAKE_WSL.adb.calls"
pkg=com.turbohttp.devicetests
listing() {  # $1: with the player (y/n), $2: lines
  local i
  printf 'USER           PID  PPID     VSZ    RSS WCHAN            ADDR S NAME\n'
  for ((i = 1; i <= $2; i++)); do
    printf 'u0_a%-8d %6d  1133 12425308 166548 0                  0 S com.vendor.service%d\n' "$i" "$((1000 + i))" "$i"
    if [ "$1" = y ] && [ "$i" = 950 ]; then
      printf 'u0_a73       26650  1133 12425308 166548 0                  0 S %s\n' "$pkg"
    fi
  done
}
proc() {  # $1: pidof or ps, $2: the shell command
  local word
  word="$(plan_next proc)"
  echo "$word" >> "$FAKE_WSL.proc.calls"
  case "$word" in fail) outage ;; finish) touch "$FAKE_WSL.finished"; word=live ;; esac
  if [ "$1" = pidof ]; then
    case "$word" in live) printf '26650\r\n' ;; gone) printf 'none\r\n' ;; esac
    return 0
  fi
  case "$word" in
    live) listing y 2000 ;;
    gone) listing n 2000 ;;
    cut) listing y 300 | head -c 30000; return 0 ;;
    *) return 0 ;;
  esac
  case "$2" in *'echo turbohttp-ps-end'*) echo turbohttp-ps-end ;; esac
}
windows() {
  local i
  printf 'WINDOW MANAGER POLICY STATE (dumpsys window policy)\n'
  if [ "${FAKE_KEYGUARD:-0}" = 1 ]; then printf '    KeyguardShowing=true\n'; fi
  local focus="${FAKE_FOCUS:-}"
  [ -n "$focus" ] || focus='Window{5f3e1a u0 com.android.launcher3/.Launcher}'
  printf '  mCurrentFocus=%s\n' "$focus"
  for ((i = 1; i <= 3000; i++)); do printf '    Window #%d: Window{%x u0 com.vendor.overlay%d}\n' "$i" "$i" "$i"; done
}
case "$*" in
  get-state) printf 'device\r\n' ;;
  devices) printf 'List of devices attached\r\n%s\tdevice\r\n' "$FAKE_SERIAL" ;;
  'shell getprop ro.build.version.sdk') printf '%s\r\n' "$FAKE_SDK" ;;
  'shell pidof '*) proc pidof "$*" ;;
  'shell ps '*) proc ps "$*" ;;
  'shell [ -s '*)
    if [ -f "$FAKE_WSL.finished" ]; then echo y
    else case "$*" in *'echo n') echo n ;; esac
    fi ;;
  'exec-out cat '*)
    cat <<'XML'
<test-run total="1" passed="1" failed="0" skipped="0" inconclusive="0" duration="0.1">
  <test-case name="T" fullname="Fake.T" result="Passed" />
</test-run>
XML
    ;;
  'shell dumpsys window') windows ;;
  'shell rm -f '*) if [ "${FAKE_CLEAR_ON_RM:-0}" = 1 ]; then rm -f "$FAKE_WSL.finished"; fi ;;
  'shell monkey '*) if [ "${FAKE_RESULT_ON_LAUNCH:-0}" = 1 ]; then touch "$FAKE_WSL.finished"; fi ;;
  'logcat -d -b events')
    if [ "${FAKE_PROC_START:-0}" = 1 ]; then
      echo "11-08 20:30:01.000  1133  1200 I am_proc_start: [0,26650,10073,$pkg,pre-top-activity,{$pkg/com.unity3d.player.UnityPlayerActivity}]"
    fi ;;
esac
exit 0
"""

LIVE, GONE = "live", "gone"


class DeviceRunLivenessTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = pathlib.Path(self.tmp.name)
        self.root = root
        # A Linux editor layout: Unity (never started with --skip-build) and the Android SDK's adb beside it.
        unity = root / "Editor" / "Unity"
        self.write_exe(unity, "#!/usr/bin/env bash\nexit 99\n")
        self.write_exe(root / "Editor" / "Data" / "PlaybackEngines" / "AndroidPlayer" / "SDK" / "platform-tools" / "adb",
                       ADB)
        cache = root / "cache"
        (cache / "turbohttp").mkdir(parents=True)
        (cache / "turbohttp" / "device-tests-High-arm64.apk").write_text("apk")
        self.state = root / "state"
        self.env = dict(os.environ, UNITY_PATH=str(unity), XDG_CACHE_HOME=str(cache), FAKE_WSL=str(self.state),
                        FAKE_SERIAL=SERIAL, FAKE_SDK="31", wsl_poll_s="0", device_poll_s="0", launch_poll_s="0")

    def tearDown(self):
        self.tmp.cleanup()

    @staticmethod
    def write_exe(path, body):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body)
        path.chmod(0o755)

    def plan(self, words):
        pathlib.Path(f"{self.state}.proc.plan").write_text(words + "\n")

    def calls(self, kind):
        path = pathlib.Path(f"{self.state}.{kind}.calls")
        return path.read_text().splitlines() if path.exists() else []

    def run_device(self, **env):
        return subprocess.run(["bash", str(UNITY_DIR / "run-device-tests.sh"), "--serial", SERIAL, "--skip-build",
                               "--filter", "Fake"], env=dict(self.env, **env), capture_output=True, text=True,
                              timeout=120)

    def assert_pulled(self, result):
        out = result.stdout + result.stderr
        self.assertEqual(result.returncode, 0, out)
        self.assertIn("run-tests: total=1 passed=1", result.stdout)
        self.assertNotIn("is not running", out)
        self.assertTrue(any(c.startswith("exec-out cat ") for c in self.calls("adb")), out)
        self.assertIn(f"shell am force-stop {PKG}", self.calls("adb"), "a finished player is stopped")

    # --- acceptance: a transient empty, failed or partial answer does not end the wait ---

    def test_transient_answers_do_not_end_the_wait_pidof(self):
        # Guard, launch, then: dropped answers, two misses, a reset, two misses, the result file.
        self.plan("gone live empty fail fail fail fail fail gone gone live gone gone finish")
        result = self.run_device()
        self.assert_pulled(result)
        self.assertIn(f"installed and launched on {SERIAL}", result.stdout)

    def test_live_player_in_a_long_ps_listing_is_running(self):
        # API 23 reads ps. The player is in the middle of ~150 KB: `grep -q` under pipefail misread it as gone.
        self.plan("gone live cut live live finish")
        result = self.run_device(FAKE_SDK="23")
        self.assert_pulled(result)

    def test_profile_heavy_listing_pulls_the_results_when_the_player_finishes(self):
        self.plan("gone live live live live live finish gone*")
        result = self.run_device(FAKE_SDK="25")
        self.assert_pulled(result)

    # --- acceptance: a really dead player is still detected within the documented bound ---

    def test_dead_player_is_reported_after_three_complete_misses(self):
        # An unknown answer between the misses neither counts nor resets them.
        self.plan("gone live live gone empty gone gone live*")
        result = self.run_device()
        out = result.stdout + result.stderr
        self.assertEqual(result.returncode, 2, out)
        self.assertIn(f"the player ({PKG}) is not running (gone on 3 checks in a row)", result.stdout)
        self.assertIn("run-device-tests: no results", result.stdout)
        self.assertIn("run-device-tests: device log ", result.stdout)
        self.assertEqual(len(self.calls("proc")), 7)

    def test_adb_that_stops_answering_is_reported_and_waited_for(self):
        self.plan("gone live empty empty empty empty finish")
        result = self.run_device()
        self.assert_pulled(result)
        self.assertEqual(result.stdout.count("adb gave no answer to the process check 3 times in a row"), 1)

    def test_dead_player_on_the_ps_path(self):
        self.plan("gone live gone gone gone live*")
        result = self.run_device(FAKE_SDK="23")
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("is not running (gone on 3 checks in a row)", result.stdout)

    # --- the launch: a blocked launch is told apart from a player that started ---

    def test_blocked_launch_names_the_focused_window(self):
        focus = "Window{1a2b u0 com.google.android.gms/com.google.android.gms.kids.LockscreenActivityV2}"
        self.plan("gone*")
        result = self.run_device(FAKE_FOCUS=focus)
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn(f"the player did not start on {SERIAL}", result.stderr)
        self.assertIn("focused window: u0 com.google.android.gms/com.google.android.gms.kids.LockscreenActivityV2",
                      result.stderr)
        self.assertNotIn("installed and launched", result.stdout)
        self.assertTrue(any(c.startswith("shell monkey ") for c in self.calls("adb")))

    def test_player_that_finished_between_two_checks_is_pulled(self):
        self.plan("gone*")
        result = self.run_device(FAKE_RESULT_ON_LAUNCH="1")
        self.assert_pulled(result)
        self.assertIn(f"installed and launched on {SERIAL}", result.stdout)

    def test_player_that_started_and_died_is_not_a_blocked_launch(self):
        self.plan("gone*")
        result = self.run_device(FAKE_PROC_START="1")
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertNotIn("did not start", result.stderr)
        self.assertIn("installed and launched", result.stdout)
        self.assertIn("is not running", result.stdout)

    def test_lock_screen_stops_before_the_launch(self):
        self.plan("gone*")
        result = self.run_device(FAKE_KEYGUARD="1")
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("shows its lock screen", result.stderr)
        self.assertFalse(any(c.startswith("shell monkey ") for c in self.calls("adb")))

    # --- a queued run never replaces the player of a run in progress ---

    def test_running_player_of_an_earlier_run_is_not_replaced(self):
        self.plan("live*")
        result = self.run_device()
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("of an earlier run is still running", result.stderr)
        self.assertIn(f"adb -s {SERIAL} shell am force-stop {PKG}", result.stderr)
        adb = self.calls("adb")
        for step in ("install", "shell rm", "logcat -c", "shell monkey"):
            self.assertFalse(any(c.startswith(step) for c in adb), f"{step} ran: {adb}")

    def test_finished_player_that_did_not_quit_is_replaced(self):
        # Its result file is on the device: that run ended, so the new run goes ahead (and deletes the file).
        pathlib.Path(f"{self.state}.finished").touch()
        self.plan("live live finish")
        result = self.run_device(FAKE_CLEAR_ON_RM="1")
        self.assertIn("finished (its result file is on the device) but still runs; replacing it", result.stdout)
        self.assert_pulled(result)

    def test_unknown_answers_before_the_run_are_asked_again(self):
        self.plan("empty empty gone live finish")
        result = self.run_device()
        self.assert_pulled(result)

    # --- no script pipes a captured adb answer into grep -q ---

    def test_scripts_do_not_pipe_adb_answers_into_grep_q(self):
        pattern = re.compile(r"""printf '%s\\n' "\$\w+" \| grep -q""")
        for name in ("run-device-tests.sh", "build-player.sh", "android-adb.sh"):
            with self.subTest(name):
                self.assertIsNone(pattern.search((UNITY_DIR / name).read_text()))
        text = (UNITY_DIR / "build-player.sh").read_text()
        self.assertIn('. "$repo/Tools~/unity/android-adb.sh"', text)
        self.assertIn('android_process_state "$pkg_id"', text)


if __name__ == "__main__":
    unittest.main()
