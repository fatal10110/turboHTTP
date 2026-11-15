"""Tests for the device log capture of Tools~/unity/run-device-tests.sh (#419).

The stub adb of test_android_device.py gets a logcat ring: every log dump first "logs" three new lines, then answers
with at most the newest RING lines (fewer than the run logs in all, like the 16M buffer against a full suite),
filtered by -t <epoch time> (inclusive) when given. Two consecutive lines share each timestamp, so the inclusive
-t repeats lines the previous poll already wrote. The kept device log must hold every line exactly once.
"""
import os
import pathlib
import re
import unittest

import test_android_device as dev

RING = 4

LOGCAT = r"""
ring_poll() {  # $1: the -t time, or empty
  local k i ts
  k="$(cat "$FAKE_WSL.ring.count" 2>/dev/null || echo 0)"
  for i in 1 2 3; do
    k=$((k + 1))
    ts="$((1700000000 + k / 2)).000"
    printf '%s  4242  4243 I Fake    : line %d\n' "$ts" "$k" >> "$FAKE_WSL.ring.all"
  done
  echo "$k" > "$FAKE_WSL.ring.count"
  printf -- '--------- beginning of main\n'
  tail -n RING_SIZE "$FAKE_WSL.ring.all" | awk -v since="$1" 'since == "" || $1 + 0 >= since + 0'
}
case "$*" in
  'logcat -d -v epoch -b main -b system -b crash -b events'|'logcat -v epoch -b main -b system -b crash -b events -t '*)
    echo "$$" >> "$FAKE_WSL.logcat.pids"
    if [ "${FAKE_LOGCAT_FAIL:-0}" = 1 ]; then exit 1; fi
    # Android 6: no -v epoch; without the shell protocol the error arrives on stdout with exit 0.
    if [ "${FAKE_LOGCAT_OLD:-0}" = 1 ]; then echo "Invalid parameter epoch to -v"; exit 0; fi
    since=""
    case "$*" in *' -t '*) since="${*##* }" ;; esac
    ring_poll "$since"
    exit 0 ;;
  'logcat -d -b main -b system -b crash -b events') echo "fallback dump line"; exit 0 ;;
esac
""".replace("RING_SIZE", str(RING))

MAIN_CASE = '\ncase "$*" in\n  get-state)'
assert dev.ADB.count(MAIN_CASE) == 1
ADB = dev.ADB.replace(MAIN_CASE, "\n" + LOGCAT + MAIN_CASE)


class DeviceLogCaptureTests(dev.DeviceRunLivenessTests):
    def setUp(self):
        super().setUp()
        adb = self.root / "Editor" / "Data" / "PlaybackEngines" / "AndroidPlayer" / "SDK" / "platform-tools" / "adb"
        self.write_exe(adb, ADB)

    def devlog(self):
        [path] = (self.root / "cache" / "turbohttp").glob("logcat-Android-*.txt")
        return path

    def log_polls(self):
        return [c for c in self.calls("adb") if c.startswith("logcat ") and "-v epoch" in c]

    def test_device_log_covers_the_whole_run_beyond_the_ring_buffer(self):
        self.plan("gone live live live live live finish gone*")
        result = self.run_device()
        self.assert_pulled(result)
        polls = self.log_polls()
        self.assertGreaterEqual(len(polls), 4, polls)
        self.assertNotIn("-t", polls[0].split(), "the first poll takes everything since the logcat -c")
        self.assertTrue(all(p.split()[-2] == "-t" for p in polls[1:]), polls)
        logged = int(pathlib.Path(f"{self.state}.ring.count").read_text())
        self.assertGreater(logged, RING, "the run logged more than the ring holds")
        lines = self.devlog().read_text().splitlines()
        self.assertEqual([int(re.search(r"line (\d+)$", l).group(1)) for l in lines], list(range(1, logged + 1)),
                         "every line once, in order, none lost to the ring")
        self.assertIn(f"run-device-tests: device log {self.devlog()}", result.stdout)
        self.assertNotIn("logcat -d -b main -b system -b crash -b events", self.calls("adb"),
                         "the end-of-run dump is only the fallback")

    def test_no_log_process_outlives_the_run(self):
        self.plan("gone live live finish gone*")
        result = self.run_device()
        self.assert_pulled(result)
        pids = [int(p) for p in pathlib.Path(f"{self.state}.logcat.pids").read_text().split()]
        self.assertTrue(pids)
        for pid in pids:
            with self.assertRaises(ProcessLookupError, msg=f"logcat call {pid} still runs"):
                os.kill(pid, 0)
        # Every log call is a bounded dump (-d, or -t which implies it), never a stream that needs stopping.
        for call in (c for c in self.calls("adb") if c.startswith("logcat ")):
            words = call.split()
            self.assertTrue("-d" in words or "-t" in words or "-c" in words or "-G" in words, call)

    def test_dead_player_keeps_the_polled_log(self):
        self.plan("gone live live gone*")
        result = self.run_device()
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("is not running", result.stdout)
        self.assertIn("line 1", self.devlog().read_text())

    def assert_fallback_dump(self, **env):
        self.plan("gone live finish gone*")
        result = self.run_device(**env)
        self.assert_pulled(result)
        self.assertIn("logcat -d -b main -b system -b crash -b events", self.calls("adb"))
        self.assertEqual(self.devlog().read_text().strip(), "fallback dump line")

    def test_end_of_run_dump_is_the_fallback_when_no_poll_answers(self):
        self.assert_fallback_dump(FAKE_LOGCAT_FAIL="1")

    def test_end_of_run_dump_is_the_fallback_for_a_logcat_without_epoch_times(self):
        self.assert_fallback_dump(FAKE_LOGCAT_OLD="1")


# The inherited liveness tests already run in test_android_device.py.
for _name in [n for n in dir(dev.DeviceRunLivenessTests) if n.startswith("test_")]:
    setattr(DeviceLogCaptureTests, _name, None)

if __name__ == "__main__":
    unittest.main()
