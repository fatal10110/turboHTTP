"""Tests for the token-saving lane tools: wave_mail worktree/pr-state/ci-wait/stop
signalling, look.py (C# lookups) and issue_slice.py. GitHub is replaced by a fake gh script."""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
MB = HERE / "wave_mail.py"
LOOK = HERE / "look.py"
SLICE = HERE / "issue_slice.py"

FAKE_GH = textwrap.dedent('''\
    #!/usr/bin/env python3
    import json, os, sys
    data = json.load(open(os.environ["FAKE_GH_DATA"]))
    key = " ".join(sys.argv[1:])
    for k, v in data.items():
        if key.startswith(k):
            print(json.dumps(v)); sys.exit(0)
    sys.stderr.write("fake gh: no answer for " + key); sys.exit(1)
''')


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp()).resolve()
        self.waves = self.tmp / "wt" / ".waves"
        self.waves.mkdir(parents=True)
        gh = self.tmp / "gh"
        gh.write_text(FAKE_GH)
        gh.chmod(0o755)
        self.gh_data = self.tmp / "gh.json"
        self.env = dict(os.environ, WAVES_ROOT=str(self.waves), WAVES_GH=str(gh), FAKE_GH_DATA=str(self.gh_data),
                        WAVES_POLL="0.05", WAVES_CI_POLL="0.05")

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def gh(self, data):
        self.gh_data.write_text(json.dumps(data))

    def run_py(self, script, *args, check=True):
        r = subprocess.run([sys.executable, str(script), *map(str, args)], capture_output=True, text=True, env=self.env)
        if check and r.returncode:
            self.fail(f"{script.name} {args}: rc={r.returncode} {r.stderr}")
        return r

    def mb(self, *args, check=True):
        r = self.run_py(MB, *args, check=check)
        return json.loads(r.stdout) if r.stdout.strip() else None


class WaveMailTools(Base):
    def test_set_takes_several_pairs(self):
        st = self.mb("state", "--run", "r", "--lane", "a", "--set", "round=4", "stage=merge")
        self.assertEqual((st["round"], st["stage"]), (4, "merge"))

    def test_every_output_of_a_stopping_run_says_stop(self):
        self.mb("state", "--run", "r", "--lane", "a", "--set", "stage=impl")
        self.assertNotIn("stop", self.mb("state", "--run", "r", "--lane", "a"))
        self.mb("stop", "--run", "r")
        self.assertTrue(self.mb("state", "--run", "r", "--lane", "a")["stop"])

    def test_stop_banner_names_lane_worktrees_only(self):
        wt = self.tmp / "wt" / "wf-a"
        (wt / "x").mkdir(parents=True)
        self.mb("state", "--run", "r", "--lane", "a", "--set", f"worktree={wt}")
        self.assertEqual(self.run_py(MB, "stopcheck", wt / "x").returncode, 0)
        self.mb("stop", "--run", "r")
        r = self.run_py(MB, "stopcheck", wt / "x", check=False)
        self.assertEqual(r.returncode, 3)
        self.assertIn("### STOP", r.stdout)
        self.assertEqual(self.run_py(MB, "stopcheck", self.tmp, check=False).returncode, 0)
        review = self.tmp / "wt" / "review-a"
        review.mkdir()
        self.assertEqual(self.run_py(MB, "stopcheck", review, check=False).returncode, 3)

    def test_worktree_creates_reuses_and_removes(self):
        origin = self.tmp / "origin.git"
        go = self.tmp / "go"
        git = lambda *a, cwd=None: subprocess.run(["git", *map(str, a)], cwd=cwd, check=True, capture_output=True)
        git("init", "-q", "--bare", "-b", "main", origin)
        git("clone", "-q", origin, go)
        git("-C", go, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "init")
        git("-C", go, "push", "-q", "origin", "main")
        self.env["WAVES_REPO"] = str(go)
        r = self.mb("worktree", "--run", "r", "--lane", "a")
        self.assertTrue(r["created"])
        self.assertEqual(r["worktree"], str(self.tmp / "wt" / "wf-a"))
        st = self.mb("state", "--run", "r", "--lane", "a")
        self.assertEqual((st["stage"], st["branch"]), ("impl", "wf/a"))
        self.mb("state", "--run", "r", "--lane", "a", "--set", "stage=review")
        r = self.mb("worktree", "--run", "r", "--lane", "a")
        self.assertFalse(r["created"])
        self.assertEqual(self.mb("state", "--run", "r", "--lane", "a")["stage"], "review", "reuse keeps the stage")
        wt = Path(r["worktree"])
        git("-C", wt, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "work")
        git("-C", wt, "push", "-q", "origin", "wf/a")
        rv = self.mb("worktree", "--run", "r", "--lane", "a", "--review")
        self.assertEqual(rv["head"], self.mb("worktree", "--run", "r", "--lane", "a")["head"])
        self.assertEqual(rv["ahead_of_main"], 1)
        self.mb("worktree", "--run", "r", "--lane", "a", "--remove")
        self.assertFalse(wt.exists())

    def test_drain_refuses_only_new_lanes(self):
        origin = self.tmp / "origin.git"
        go = self.tmp / "go"
        git = lambda *a: subprocess.run(["git", *map(str, a)], check=True, capture_output=True)
        git("init", "-q", "--bare", "-b", "main", origin)
        git("clone", "-q", origin, go)
        git("-C", go, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "init")
        git("-C", go, "push", "-q", "origin", "main")
        self.env["WAVES_REPO"] = str(go)
        self.assertTrue(self.mb("worktree", "--run", "r", "--lane", "a")["created"])
        self.mb("stop", "--run", "r", "--drain")
        self.assertFalse(self.mb("worktree", "--run", "r", "--lane", "a")["created"], "a lane under way keeps working")
        r = self.mb("worktree", "--run", "r", "--lane", "b")
        self.assertTrue(r["stop"] and r["drain"])
        self.assertFalse((self.tmp / "wt" / "wf-b").exists())
        self.assertNotIn("stop", self.mb("state", "--run", "r", "--lane", "a"), "draining is not a stop for running lanes")

    def test_pr_state_is_compact(self):
        self.gh({"api repos/fatal10110/turboHTTP/pulls/7": {"state": "open", "merged": False, "draft": False,
                 "head": {"sha": "h1", "ref": "wf/a"}, "base": {"ref": "main"}, "mergeable_state": "behind"},
                 "api repos/fatal10110/turboHTTP/compare/main...h1": {"behind_by": 3}})
        self.assertEqual(self.mb("pr-state", "--pr", "7"), {"pr": 7, "state": "open", "merged": False, "merge_sha": "", "draft": False,
                         "head": "h1", "branch": "wf/a", "mergeable_state": "behind", "behind_by": 3})

    def runs(self, *rows):
        return {"api repos/fatal10110/turboHTTP/commits/h1/check-runs": {"check_runs": [
            {"id": i, "name": n, "status": s, "conclusion": c, "details_url": f"https://x/actions/runs/9/job/{i}"}
            for i, (n, s, c) in enumerate(rows, 1)]}}

    def test_ci_wait_success_needs_the_required_checks(self):
        self.gh(self.runs(("CodeQL", "completed", "success")))
        r = self.mb("ci-wait", "--run", "r", "--head", "h1", "--wait", "0.2")
        self.assertEqual((r["state"], r["missing"]), ("pending", ["ci"]))
        self.gh(self.runs(("lint", "completed", "success"), ("unity (EditMode)", "completed", "success"), ("ci", "completed", "success")))
        self.assertEqual(self.mb("ci-wait", "--run", "r", "--head", "h1", "--wait", "1")["state"], "success")

    def test_ci_wait_reports_failures_once_all_finished(self):
        self.gh(self.runs(("ci", "in_progress", None), ("lint", "completed", "success"),
                          ("unity (EditMode)", "completed", "failure"), ("unity (PlayMode)", "in_progress", None)))
        self.assertEqual(self.mb("ci-wait", "--run", "r", "--head", "h1", "--wait", "0.2")["state"], "pending")
        self.gh(self.runs(("ci", "completed", "failure"), ("lint", "completed", "success"),
                          ("unity (EditMode)", "completed", "failure"), ("unity (PlayMode)", "completed", "success")))
        r = self.mb("ci-wait", "--run", "r", "--head", "h1", "--wait", "1")
        self.assertEqual(r["state"], "failure")
        self.assertEqual([f["name"] for f in r["failed"]], ["ci", "unity (EditMode)"])
        self.assertEqual(r["failed"][1], {"name": "unity (EditMode)", "conclusion": "failure", "run_id": "9", "job_id": "3"})


class LookTool(Base):
    def test_batches_ranges_cs_and_rg(self):
        src = self.tmp / "src"
        (src / "Sub").mkdir(parents=True)
        (src / "Sub" / "Conn.cs").write_text(textwrap.dedent("""\
            namespace X
            {
                /// <summary>A connection.</summary>
                internal sealed partial class Conn : IDisposable
                {
                    private int _next = 1;

                    /// <summary>Allocates an id.</summary>
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public int Allocate(int step)
                    {
                        if (step > 0) { return _next += step; }
                        return _next;
                    }

                    public bool IsAlive => _next > 0;

                    public int Count { get; private set; }

                    public Conn(int n) { _next = n; }

                    public void Other() { Allocate(1); }
                }
            }
            """))
        (src / "Other.cs").write_text("class Other { void Allocate() { } }\n")
        out = self.run_py(LOOK, "--root", src, "Sub/Conn.cs:1-2", "cs:.:Conn.Allocate,IsAlive,Count", "cs:Sub:Conn",
                          "rg:.:Other", "rgl:.:return").stdout
        self.assertIn("1\tnamespace X", out)
        self.assertIn("8\t        /// <summary>Allocates an id.</summary>", out)
        self.assertIn("14\t        }", out)
        self.assertNotIn("Other.cs:1-1 (Conn.Allocate)", out, "Type.Member stays inside the type")
        self.assertIn("16\t        public bool IsAlive => _next > 0;", out)
        self.assertIn("18\t        public int Count { get; private set; }", out)
        self.assertRegex(out, r"Conn\.cs:3-23 \(Conn\)")
        self.assertIn("Other.cs:1:class Other", out)
        self.assertNotIn("### STOP", out)

    def test_call_sites_are_not_declarations(self):
        f = self.tmp / "C.cs"
        f.write_text("class C {\n  void A() { return; }\n  void B() {\n    A();\n    var x = A();\n  }\n}\n")
        out = self.run_py(LOOK, f"cs:{f}:A").stdout
        self.assertEqual(out.count("== "), 1, out)

    def test_caps_long_output(self):
        f = self.tmp / "big.txt"
        f.write_text("\n".join(f"line {i}" for i in range(1, 400)))
        out = self.run_py(LOOK, f"{f}:1-399", "--max", "20").stdout
        self.assertIn("20\tline 20", out)
        self.assertNotIn("21\tline 21", out)
        self.assertIn("more lines cut", out)


BODY = textwrap.dedent('''\
    <!-- seed-key: H2-01 -->
    ## Summary
    HPACK desync.

    ## Location
    - `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs:220` — drop

    ## Acceptance criteria
    - [ ] decode every block

    ## Context
    See #13 and #12; plan in `Development/docs/phases/phase-03b-http2.md`.
''')


class IssueSlice(Base):
    def setUp(self):
        super().setUp()
        self.gh({"issue view 11": {"number": 11, "title": "[HTTP/2] HPACK", "state": "OPEN", "milestone": {"title": "v1.0-beta"},
                 "labels": [{"name": "area:http2"}], "body": BODY,
                 "comments": [{"createdAt": "2026-10-19T00:00:00Z", "body": "still open on main"}]},
                 "issue view 12": {"number": 12, "title": "[HTTP/2] trailers", "state": "OPEN"},
                 "issue view 13": {"number": 13, "title": "[HTTP/2] stream ids", "state": "CLOSED"}})

    def test_slice_prints_body_refs_and_paths(self):
        out = self.run_py(SLICE, "11").stdout
        self.assertIn("#11 [OPEN] [HTTP/2] HPACK  (v1.0-beta)  labels: area:http2", out)
        self.assertNotIn("seed-key", out)
        self.assertIn("- [ ] decode every block", out)
        self.assertIn("still open on main", out)
        self.assertIn("Development/docs/phases/phase-03b-http2.md", out)
        self.assertIn("#12 [OPEN] [HTTP/2] trailers", out)
        self.assertIn("#13 [CLOSED] [HTTP/2] stream ids", out)

    def test_section_and_cache(self):
        self.assertIn("decode every block", self.run_py(SLICE, "11", "--section", "acceptance").stdout)
        self.gh({})  # GitHub gone: the cache still answers
        self.assertIn("HPACK desync.", self.run_py(SLICE, "11", "--no-refs").stdout)


if __name__ == "__main__":
    unittest.main()
