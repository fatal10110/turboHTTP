#!/usr/bin/env python3
"""Tests for summarize_heartbeats.py (#1297) on synthetic logcat -v epoch files; no device needed."""
import io
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import summarize_heartbeats as sh  # noqa: E402

T0 = 1764000000.0  # 2025-11-24 16:00:00 UTC


def beat(at, n, tick=1001, pool=2, main=16, workers="399/400", test="TurboHTTP.Tests.Http2.Foo.Bar(1, \"a b\")"):
    return (f"  {at:.3f}  4242  4300 I Unity   : TURBOHTTP_DEVICE_HEARTBEAT n={n} up_s={n * 15} "
            f"tick_max_ms={tick} pool_ms={pool} main_ms={main} workers={workers} io=200/200 min=4/4 "
            f"pending=n/a threads=52 gc=3 mem_mb=80 test={test}")


def other(at, text="some other line"):
    return f"  {at:.3f}  4242  4242 I Unity   : {text}"


class SummarizeHeartbeatsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)

    def run_main(self, lines, *extra):
        path = os.path.join(self.tmp.name, "logcat.txt")
        with open(path, "w", encoding="utf-8") as fh:
            fh.write("--------- beginning of main\n" + "\n".join(lines) + "\n")
        out = io.StringIO()
        with redirect_stdout(out):
            code = sh.main([path, "--prefix", "run-device-tests: ", *extra])
        return code, out.getvalue()

    def clean(self, count=6):
        return [other(T0 - 1, "TURBOHTTP_DEVICE_RUN started tests=3794")] + \
            [beat(T0 + 15 * i, i + 1) for i in range(count)]

    def test_clean_run_is_not_flagged(self):
        code, out = self.run_main(self.clean())
        self.assertEqual(code, 0, out)
        self.assertIn("heartbeat: 6 samples over 75 s; 1 s wait max 1001 ms, pool probe max 2 ms", out)
        self.assertIn("workers available min 399/400", out)
        self.assertNotIn("STALL", out)

    def test_no_heartbeat_is_reported_but_not_a_stall(self):
        code, out = self.run_main([other(T0)])
        self.assertEqual((code, out.strip()), (0, "run-device-tests: heartbeat: none logged"))

    def test_late_timer_is_a_guest_stall(self):
        lines = self.clean(3) + [beat(T0 + 45 + 1200, 4, tick=1200000, pool=1200010),
                                 beat(T0 + 60 + 1200, 5, tick=900000, pool=900000),
                                 beat(T0 + 75 + 2100, 6)]
        code, out = self.run_main(lines)
        self.assertEqual(code, 1, out)
        self.assertIn("STALL SUSPECTED (guest stall", out)
        self.assertIn("2 sample(s), 1 s wait max 1200000 ms", out)
        self.assertIn("running: TurboHTTP.Tests.Http2.Foo.Bar(1, \"a b\")", out)
        self.assertNotIn("starvation", out)
        self.assertIn("rerun it", out)

    def test_on_time_timer_with_late_pool_probe_is_starvation(self):
        lines = self.clean(3) + [beat(T0 + 45, 4, pool=45000, workers="380/400"), beat(T0 + 60, 5, workers="0/400")]
        code, out = self.run_main(lines)
        self.assertEqual(code, 1, out)
        self.assertIn("STALL SUSPECTED (thread-pool starvation", out)
        self.assertIn("2 sample(s), 1 s wait max 1001 ms, pool probe max 45000 ms", out)
        self.assertIn("workers available min 0/400", out)
        self.assertNotIn("guest stall", out)

    def test_blocked_main_thread_and_heartbeat_gap(self):
        lines = self.clean(2) + [beat(T0 + 30, 3, main=90000), beat(T0 + 200, 4)]
        code, out = self.run_main(lines)
        self.assertEqual(code, 1, out)
        self.assertIn("STALL SUSPECTED (main thread blocked", out)
        self.assertIn("STALL SUSPECTED (heartbeat gap", out)

    def test_live_flags_only_the_newest_heartbeat(self):
        stalled = self.clean(3) + [beat(T0 + 45, 4, pool=30000)]
        self.assertEqual(self.run_main(stalled, "--live"), (1, "starvation\n"))
        recovered = stalled + [beat(T0 + 60, 5)]
        self.assertEqual(self.run_main(recovered, "--live"), (0, ""))

    def test_live_flags_silent_heartbeats_until_the_run_finished(self):
        silent = self.clean(2) + [other(T0 + 15 + 120)]
        self.assertEqual(self.run_main(silent, "--live"), (1, "silent\n"))
        finished = self.clean(2) + [other(T0 + 20, "TURBOHTTP_DEVICE_RUN finished total=1"), other(T0 + 15 + 120)]
        self.assertEqual(self.run_main(finished, "--live"), (0, ""))

    def test_missing_log_is_not_an_error(self):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = sh.main([os.path.join(self.tmp.name, "absent.txt")])
        self.assertIn("cannot read", err.getvalue())
        self.assertEqual(code, 0)


if __name__ == "__main__":
    unittest.main()
