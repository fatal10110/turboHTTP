"""Tests for Tools~/unity/run-linux-player-tests.sh: the relaunch on a pre-run Mono GC crash (#423).

The player is faked: a stub TurboHttpTests.x86_64 follows a plan file (one word per launch; a trailing "*"
repeats the word forever) and writes the log the real player writes to its -logFile:
  crash      dies in test discovery: tmp= line, "Caught fatal signal - signo:11", a GC stack and a long tail (a
             real crash log is large), no started line; with FAKE_CRASH_TO_CONSOLE=1 the report goes to stdout only
  pass       started, one test, finished failed=0 path=<results.xml> (written), exit 0
  fail       started, a TURBOHTTP_DEVICE_FAIL, finished failed=2, exit 1
  midcrash   started, one test, then "Caught fatal signal" and no finished line (a crash during the run)
  quit       exits 1 with neither a started line nor a crash report
  hang       never logs started and never exits (the launch timeout ends it)
"""
import os
import pathlib
import subprocess
import tempfile
import unittest

REPO = pathlib.Path(__file__).resolve().parents[1].parent
SCRIPT = REPO / "Tools~" / "unity" / "run-linux-player-tests.sh"

PLAYER = r"""#!/usr/bin/env bash
log=""
while [ $# -gt 0 ]; do
  case "$1" in -logFile) log="$2"; shift 2 ;; *) echo "$1" >> "$FAKE.args"; shift ;; esac
done
f="$FAKE.plan"
read -r -a words < "$f"
word="${words[0]}"
case "$word" in *'*') word="${word%'*'}" ;; *) echo "${words[*]:1}" > "$f" ;; esac
echo "$word" >> "$FAKE.launches"
echo "Initialize engine version: 2021.3.45f2" > "$log"
echo "TURBOHTTP_DEVICE_RUN tmp=/tmp/" >> "$log"
crash() {
  if [ "${FAKE_CRASH_TO_CONSOLE:-0}" = 1 ]; then echo "Caught fatal signal - signo:11 code:1 errno:0 addr:0x7eb67fff78d8"; exit 139; fi
  echo "Caught fatal signal - signo:11 code:1 errno:0 addr:0x7eb67fff78d8" >> "$log"
  seq 1 200000 >> "$log"
  echo "#1  0x007eb7ad6740ae in GC_mark_from" >> "$log"
  exit 139
}
finish() {  # $1 failed count
  echo '<test-run failed="'"$1"'"/>' > "$FAKE.results.xml"
  echo "TURBOHTTP_DEVICE_RUN finished total=3 passed=$((3 - $1)) failed=$1 skipped=0 path=$FAKE.results.xml" >> "$log"
}
case "$word" in
  crash) crash ;;
  quit) exit 1 ;;
  hang) exec sleep 30 ;;
esac
echo "TURBOHTTP_DEVICE_RUN started tests=3 unity=2021.3.45f2" >> "$log"
echo "TURBOHTTP_DEVICE_TEST A.B.C" >> "$log"
case "$word" in
  pass) finish 0; exit 0 ;;
  fail) echo "TURBOHTTP_DEVICE_FAIL A.B.D (Failed, 0.1s)" >> "$log"; finish 2; exit 1 ;;
  midcrash) crash ;;
esac
"""


class LinuxPlayerRunTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = pathlib.Path(self.tmp.name)
        self.player_dir = self.dir / "player"
        self.player_dir.mkdir()
        self.player = self.player_dir / "TurboHttpTests.x86_64"
        self.player.write_text(PLAYER)
        self.player.chmod(0o755)
        self.fake = str(self.dir / "fake")
        self.out = self.dir / "out"

    def tearDown(self):
        self.tmp.cleanup()

    def run_script(self, plan, *args, env=None, target=None):
        pathlib.Path(self.fake + ".plan").write_text(plan + "\n")
        full_env = dict(os.environ, FAKE=self.fake)
        full_env.pop("TURBOHTTP_LINUX_PLAYER_ATTEMPTS", None)
        full_env.pop("TURBOHTTP_LINUX_PLAYER_TIMEOUT", None)
        full_env.update(env or {})
        return subprocess.run(
            ["bash", str(SCRIPT), "--out", str(self.out), *args, str(target or self.player_dir)],
            env=full_env, capture_output=True, text=True, timeout=60)

    def launches(self):
        p = pathlib.Path(self.fake + ".launches")
        return p.read_text().split() if p.exists() else []

    def test_pass_first_launch_is_not_relaunched(self):
        r = self.run_script("pass")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(self.launches(), ["pass"])
        self.assertIn("TURBOHTTP_DEVICE_RUN finished total=3 passed=3 failed=0", r.stdout)
        self.assertEqual((self.out / "results.xml").read_text(), '<test-run failed="0"/>\n')
        args = pathlib.Path(self.fake + ".args").read_text().split()
        self.assertEqual(args, ["-batchmode", "-nographics"])

    def test_crash_before_the_run_is_relaunched(self):
        r = self.run_script("crash pass", target=self.player)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(self.launches(), ["crash", "pass"])
        self.assertIn("crashed before the run started", r.stdout)
        self.assertIn("#423", r.stdout)
        self.assertTrue((self.out / "player-1.log").exists())
        self.assertTrue((self.out / "player-2.log").exists())

    def test_crash_reported_only_on_the_console_is_relaunched(self):
        r = self.run_script("crash pass", env={"FAKE_CRASH_TO_CONSOLE": "1"})
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(self.launches(), ["crash", "pass"])
        self.assertIn("Caught fatal signal - signo:11", (self.out / "player-1.out").read_text())

    def test_crash_on_every_launch_stops_at_the_bound(self):
        r = self.run_script("crash*")
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertEqual(self.launches(), ["crash"] * 3)
        self.assertIn("all 3 launches crashed before the run started", r.stderr)
        self.assertFalse((self.out / "results.xml").exists())

    def test_attempts_from_option_and_environment(self):
        r = self.run_script("crash*", "--attempts", "2")
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertEqual(self.launches(), ["crash"] * 2)
        pathlib.Path(self.fake + ".launches").unlink()
        r = self.run_script("crash*", env={"TURBOHTTP_LINUX_PLAYER_ATTEMPTS": "1"})
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertEqual(self.launches(), ["crash"])

    def test_crash_after_the_run_started_is_not_relaunched(self):
        r = self.run_script("midcrash pass")
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertEqual(self.launches(), ["midcrash"])
        self.assertIn("crashed (Caught fatal signal", r.stderr)
        self.assertIn("not relaunched", r.stderr)

    def test_test_failures_exit_1_with_results(self):
        r = self.run_script("fail pass")
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertEqual(self.launches(), ["fail"])
        self.assertIn("TURBOHTTP_DEVICE_FAIL A.B.D", r.stdout)
        self.assertEqual((self.out / "results.xml").read_text(), '<test-run failed="2"/>\n')

    def test_exit_without_start_or_crash_is_not_relaunched(self):
        r = self.run_script("quit pass")
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertEqual(self.launches(), ["quit"])
        self.assertIn("without a crash report; not relaunched", r.stderr)

    def test_launch_that_never_starts_times_out(self):
        r = self.run_script("hang pass", env={"TURBOHTTP_LINUX_PLAYER_TIMEOUT": "1"})
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertEqual(self.launches(), ["hang"])
        self.assertIn("within 1s", r.stderr)

    def test_bad_arguments_launch_nothing(self):
        for args, target in ((["--attempts", "0"], None), ([], self.dir / "missing")):
            r = self.run_script("pass", *args, target=target)
            self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertEqual(self.launches(), [])


if __name__ == "__main__":
    unittest.main()
