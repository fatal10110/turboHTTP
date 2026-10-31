import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("wave_mail.py")


class WaveMailTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.env = dict(os.environ, WAVES_ROOT=self.tmp.name, WAVES_POLL="0.05")

    def tearDown(self):
        self.tmp.cleanup()

    def run_cli(self, *args, stdin=None, check=True):
        r = subprocess.run([sys.executable, str(SCRIPT), *args], input=stdin, text=True,
                           capture_output=True, env=self.env)
        if check and r.returncode:
            self.fail(f"{args}: {r.stderr}")
        return json.loads(r.stdout) if r.stdout.strip() else None

    def test_box_cursor_consumes_in_order(self):
        self.run_cli("send", "--run", "r", "--lane", "a", "--box", "orch", "--kind", "ready",
                     "--data", '{"pr": 7}', "--set", "stage=review", "--set", "pr=7")
        self.run_cli("send", "--run", "r", "--lane", "a", "--box", "orch", "--kind", "blocked")
        got = self.run_cli("recv", "--run", "r", "--lane", "a", "--box", "orch")
        self.assertEqual([m["kind"] for m in got["messages"]], ["ready", "blocked"])
        self.assertEqual(got["messages"][0]["data"], {"pr": 7})
        again = self.run_cli("recv", "--run", "r", "--lane", "a", "--box", "orch", "--wait", "0.2")
        self.assertTrue(again["timeout"])
        st = self.run_cli("state", "--run", "r", "--lane", "a")
        self.assertEqual((st["stage"], st["pr"]), ("review", 7))

    def test_recv_wakes_on_send(self):
        def later():
            time.sleep(0.3)
            self.run_cli("send", "--run", "r", "--lane", "a", "--box", "impl", "--kind", "review")
        t = threading.Thread(target=later)
        t.start()
        got = self.run_cli("recv", "--run", "r", "--lane", "a", "--box", "impl", "--wait", "5")
        t.join()
        self.assertEqual(got["messages"][0]["kind"], "review")

    def test_ask_needs_live_broker_then_round_trips(self):
        r = self.run_cli("ask", "--run", "r", "--from", "a/impl", "--type", "cs-scout", "--question", "q")
        self.assertIn("helper broker not running", r["error"])
        take = self.run_cli("take", "--run", "r", "--wait", "0")
        self.assertTrue(take["timeout"])
        ask = self.run_cli("ask", "--run", "r", "--from", "a/impl", "--type", "cs-scout",
                           "--question", "-", "--nowait", stdin="where is X?")
        take = self.run_cli("take", "--run", "r", "--wait", "1")
        self.assertEqual(take["events"][0]["question"], "where is X?")
        self.run_cli("answer", "--run", "r", "--id", ask["id"], stdin="X.cs:12")
        got = self.run_cli("answer-wait", "--run", "r", "--id", ask["id"], "--wait", "1")
        self.assertEqual(got["answer"], "X.cs:12")

    def test_brokers_take_only_their_types(self):
        self.run_cli("take", "--run", "r", "--broker", "helper", "--wait", "0")
        r = self.run_cli("ask", "--run", "r", "--from", "a/review", "--type", "merge", "--question", "{}", "--nowait")
        self.assertIn("merge broker not running", r["error"])
        self.run_cli("take", "--run", "r", "--broker", "merge", "--wait", "0")
        m = self.run_cli("ask", "--run", "r", "--from", "a/review", "--type", "merge", "--question", '{"pr": 5}', "--nowait")
        h = self.run_cli("ask", "--run", "r", "--from", "a/impl", "--type", "runner", "--question", "Tools~/ci/check.sh", "--nowait")
        got = self.run_cli("take", "--run", "r", "--broker", "helper", "--wait", "0")
        self.assertEqual([e["id"] for e in got["events"]], [h["id"]])
        got = self.run_cli("take", "--run", "r", "--broker", "merge", "--wait", "0")
        self.assertEqual([e["id"] for e in got["events"]], [m["id"]])
        self.assertEqual(self.run_cli("answer-wait", "--run", "r", "--id", m["id"], "--wait", "0")["pending"], True)

    def test_peek_does_not_consume(self):
        self.run_cli("send", "--run", "r", "--lane", "a", "--box", "review", "--kind", "ready")
        p = self.run_cli("recv", "--run", "r", "--lane", "a", "--box", "review", "--peek")
        self.assertEqual(p["messages"][0]["kind"], "ready")
        c = self.run_cli("recv", "--run", "r", "--lane", "a", "--box", "review")
        self.assertEqual(c["messages"][0]["kind"], "ready")
        self.assertTrue(self.run_cli("recv", "--run", "r", "--lane", "a", "--box", "review", "--wait", "0")["timeout"])

    def test_taken_request_counts_as_live_broker(self):
        self.run_cli("take", "--run", "r", "--broker", "merge", "--wait", "0")
        m = self.run_cli("ask", "--run", "r", "--from", "a/review", "--type", "merge", "--question", "{}", "--nowait")
        self.run_cli("take", "--run", "r", "--broker", "merge", "--wait", "0")
        alive = Path(self.tmp.name) / "r" / "broker-merge.alive"
        os.utime(alive, (time.time() - 3 * 3600, time.time() - 3 * 3600))
        self.assertTrue(self.run_cli("answer-wait", "--run", "r", "--id", m["id"], "--wait", "0")["pending"])
        r = self.run_cli("ask", "--run", "r", "--from", "b/review", "--type", "merge", "--question", "{}", "--nowait")
        self.assertIn("merge broker not running", r["error"])
        self.run_cli("pause", "--secs", "0", "--run", "r", "--broker", "merge")
        r = self.run_cli("ask", "--run", "r", "--from", "b/review", "--type", "merge", "--question", "{}", "--nowait")
        self.assertTrue(r["pending"])

    def test_relaunched_broker_requeues_unanswered_requests(self):
        self.run_cli("take", "--run", "r", "--broker", "merge", "--wait", "0")
        m1 = self.run_cli("ask", "--run", "r", "--from", "a/review", "--type", "merge", "--question", "{}", "--nowait")
        m2 = self.run_cli("ask", "--run", "r", "--from", "b/review", "--type", "merge", "--question", "{}", "--nowait")
        self.assertEqual(len(self.run_cli("take", "--run", "r", "--broker", "merge", "--wait", "0")["events"]), 2)
        self.run_cli("answer", "--run", "r", "--id", m1["id"], "--text", "{}")
        # the merger crashed before answering m2; its replacement re-serves only m2
        got = self.run_cli("take", "--run", "r", "--broker", "merge", "--requeue", "--wait", "0")
        self.assertEqual(([e["id"] for e in got["events"]], got["requeued"]), ([m2["id"]], 1))

    def test_requeue_skips_ended_lanes_and_old_requests(self):
        self.run_cli("take", "--run", "r", "--broker", "helper", "--wait", "0")
        old = self.run_cli("ask", "--run", "r", "--from", "a/impl", "--type", "runner", "--question", "x", "--nowait")
        ended = self.run_cli("ask", "--run", "r", "--from", "b/impl", "--type", "runner", "--question", "x", "--nowait")
        live = self.run_cli("ask", "--run", "r", "--from", "c/impl", "--type", "runner", "--question", "x", "--nowait")
        self.run_cli("take", "--run", "r", "--broker", "helper", "--wait", "0")
        p = Path(self.tmp.name) / "r" / "requests" / "taken" / f"{old['id']}.json"
        req = json.loads(p.read_text()); req["ts"] -= 5 * 3600; p.write_text(json.dumps(req))
        self.run_cli("state", "--run", "r", "--lane", "b", "--set", "status=failed")
        got = self.run_cli("take", "--run", "r", "--broker", "helper", "--requeue", "--wait", "0")
        self.assertEqual([e["id"] for e in got["events"]], [live["id"]])
        for rid in (old["id"], ended["id"]):
            self.assertIn("dropped", self.run_cli("answer-wait", "--run", "r", "--id", rid, "--wait", "0")["answer"])

    def test_dropped_merge_orphan_answers_retry(self):
        self.run_cli("take", "--run", "r", "--broker", "merge", "--wait", "0")
        m = self.run_cli("ask", "--run", "r", "--from", "a/review", "--type", "merge", "--question", "{}", "--nowait")
        self.run_cli("take", "--run", "r", "--broker", "merge", "--wait", "0")
        p = Path(self.tmp.name) / "r" / "requests" / "taken" / f"{m['id']}.json"
        req = json.loads(p.read_text()); req["ts"] -= 5 * 3600; p.write_text(json.dumps(req))
        self.run_cli("take", "--run", "r", "--broker", "merge", "--requeue", "--wait", "0")
        ans = json.loads(self.run_cli("answer-wait", "--run", "r", "--id", m["id"], "--wait", "0")["answer"])
        self.assertEqual(ans["status"], "retry")

    def test_answer_wait_reports_stop(self):
        self.run_cli("take", "--run", "r", "--wait", "0")
        h = self.run_cli("ask", "--run", "r", "--from", "a/impl", "--type", "runner", "--question", "x", "--nowait")
        self.run_cli("stop", "--run", "r")
        self.assertTrue(self.run_cli("answer-wait", "--run", "r", "--id", h["id"], "--wait", "1")["stop"])

    def test_drain_skips_stale_messages(self):
        self.run_cli("send", "--run", "r", "--lane", "a", "--box", "review", "--kind", "no_change")
        self.run_cli("send", "--run", "r", "--lane", "a", "--box", "review", "--kind", "ready")
        self.assertEqual(self.run_cli("drain", "--run", "r", "--lane", "a", "--box", "review", "--kinds", "no_change,blocked")["drained"], 1)
        got = self.run_cli("recv", "--run", "r", "--lane", "a", "--box", "review", "--peek")
        self.assertEqual([m["kind"] for m in got["messages"]], ["ready"])
        self.assertEqual(self.run_cli("drain", "--run", "r", "--lane", "a", "--box", "review")["drained"], 1)
        self.assertTrue(self.run_cli("recv", "--run", "r", "--lane", "a", "--box", "review", "--peek", "--wait", "0")["timeout"])
        self.run_cli("send", "--run", "r", "--lane", "a", "--box", "review", "--kind", "ready")
        got = self.run_cli("recv", "--run", "r", "--lane", "a", "--box", "review", "--peek")
        self.assertEqual([m["kind"] for m in got["messages"]], ["ready"])

    def test_markers_are_per_epoch(self):
        for t in ("A", "B"):
            self.run_cli("finish-track", "--run", "r", "--track", t, "--epoch", "1")
            self.run_cli("barrier", "--run", "r", "--wave", "0", "--track", t, "--tracks", "A,B", "--epoch", "1", "--wait", "0")
        self.assertTrue(self.run_cli("take", "--run", "r", "--tracks", "A,B", "--epoch", "1", "--wait", "0")["stop"])
        self.assertFalse(self.run_cli("take", "--run", "r", "--tracks", "A,B", "--epoch", "2", "--wait", "0")["stop"])
        r = self.run_cli("barrier", "--run", "r", "--wave", "0", "--track", "A", "--tracks", "A,B", "--epoch", "2", "--wait", "0")
        self.assertEqual(r["waiting_for"], ["B"])

    def test_names_reaching_paths_are_validated(self):
        for args in (["answer", "--run", "r", "--id", "../x", "--text", "y"],
                     ["lock", "acquire", "--run", "r", "--name", "../m", "--owner", "a", "--wait", "0"],
                     ["barrier", "--run", "r", "--wave", "../0", "--track", "A", "--tracks", "A", "--wait", "0"],
                     ["state", "--run", "r", "--lane", "A b"],
                     ["ask", "--run", "r", "--from", "a;b/impl", "--type", "runner", "--question", "x"]):
            r = subprocess.run([sys.executable, str(SCRIPT), *args], capture_output=True, text=True, env=self.env)
            self.assertNotEqual(r.returncode, 0, args)

    def test_take_stops_when_all_tracks_finished(self):
        self.run_cli("finish-track", "--run", "r", "--track", "A")
        r = self.run_cli("take", "--run", "r", "--tracks", "A,B", "--wait", "0")
        self.assertFalse(r["stop"])
        self.run_cli("finish-track", "--run", "r", "--track", "B")
        r = self.run_cli("take", "--run", "r", "--tracks", "A,B", "--wait", "1")
        self.assertTrue(r["stop"])

    def test_lock_is_exclusive_and_reentrant(self):
        self.assertTrue(self.run_cli("lock", "acquire", "--run", "r", "--owner", "a")["acquired"])
        self.assertTrue(self.run_cli("lock", "acquire", "--run", "r", "--owner", "a")["acquired"])
        self.assertFalse(self.run_cli("lock", "acquire", "--run", "r", "--owner", "b", "--wait", "0.2")["acquired"])
        self.assertFalse(self.run_cli("lock", "release", "--run", "r", "--owner", "b")["released"])
        self.assertTrue(self.run_cli("lock", "release", "--run", "r", "--owner", "a")["released"])
        self.assertTrue(self.run_cli("lock", "acquire", "--run", "r", "--owner", "b", "--wait", "0")["acquired"])

    def test_stale_lock_is_stolen(self):
        self.run_cli("lock", "acquire", "--run", "r", "--owner", "a")
        r = self.run_cli("lock", "acquire", "--run", "r", "--owner", "b", "--stale-min", "0", "--wait", "0")
        self.assertEqual((r["acquired"], r["stole_from"]), (True, "a"))

    def test_barrier_passes_when_every_track_arrives(self):
        self.run_cli("state", "--run", "r", "--lane", "x", "--set", "status=merged", "--set", "pr=5")
        r = self.run_cli("barrier", "--run", "r", "--wave", "1", "--track", "A", "--tracks", "A,B", "--wait", "0")
        self.assertEqual(r["waiting_for"], ["B"])
        r = self.run_cli("barrier", "--run", "r", "--wave", "1", "--track", "B", "--tracks", "A,B", "--wait", "0")
        self.assertTrue(r["passed"])
        self.assertEqual(r["lines"], ["x|merged||5|"])

    def test_stop_releases_waiters(self):
        self.run_cli("stop", "--run", "r")
        self.assertTrue(self.run_cli("recv", "--run", "r", "--lane", "a", "--box", "impl", "--wait", "5")["stop"])

    def test_validate_plan(self):
        plan = {"tracks": ["A", "B"], "waves": [
            {"lanes": [{"slug": "a", "track": "A", "issues": [1], "hot": ["h"]},
                       {"slug": "b", "track": "B", "issues": [2], "deps": ["a"], "hot": ["h"]}]},
            {"lanes": [{"slug": "c", "track": "B", "issues": [3], "deps": ["a", "zz"]}]}]}
        p = Path(self.tmp.name) / "plan.json"
        p.write_text(json.dumps(plan))
        r = self.run_cli("validate", str(p), check=False)
        self.assertFalse(r["ok"])
        self.assertEqual(sorted(r["errors"]), sorted([
            "b: same-wave dep a is on another track (A)",
            "wave 0: hot group h spans tracks A and B",
            "c: unknown dep zz"]))
        plan["waves"][0]["lanes"][0]["implModel"] = ""
        p.write_text(json.dumps(plan))
        r = self.run_cli("validate", str(p), check=False)
        self.assertIn("a: implModel must be a model name or alias", r["errors"])
        plan["waves"][0]["lanes"][0]["implModel"] = "sonnet"
        plan["waves"][0]["lanes"][0]["implEffort"] = "huge"
        p.write_text(json.dumps(plan))
        r = self.run_cli("validate", str(p), check=False)
        self.assertIn("a: implEffort must be low, medium, high, xhigh or max", r["errors"])
        plan["waves"][0]["lanes"][0]["implEffort"] = "high"
        plan["waves"][0]["lanes"][0]["slug"] = "Bad Slug"
        p.write_text(json.dumps(plan))
        r = self.run_cli("validate", str(p), check=False)
        self.assertTrue(any("bad slug" in e for e in r["errors"]))

    def test_bad_ids_are_refused(self):
        r = subprocess.run([sys.executable, str(SCRIPT), "state", "--run", "../x", "--lane", "a"],
                           capture_output=True, text=True, env=self.env)
        self.assertNotEqual(r.returncode, 0)

    def test_threads_counts_unresolved_and_unanswered_including_bots(self):
        nodes = [
            {"id": "T1", "isResolved": False, "isOutdated": False, "path": "a.cs", "line": 3,
             "comments": {"totalCount": 1, "nodes": [{"author": {"login": "github-advanced-security"},
                                                      "body": "## CodeQL / Generic catch clause\n\nmore", "url": "u1"}]}},
            {"id": "T2", "isResolved": False, "isOutdated": True, "path": "b.cs", "line": None,
             "comments": {"totalCount": 2, "nodes": [{"author": {"login": "fatal10110"}, "body": "[must-fix] x"},
                                                     {"author": {"login": "fatal10110"}, "body": "Fixed in abc"}]}},
            {"id": "T3", "isResolved": True, "comments": {"totalCount": 1, "nodes": []}},
        ]
        fake = Path(self.tmp.name) / "gh"
        fake.write_text("#!/usr/bin/env python3\nimport json\nprint(json.dumps({'data': {'repository': {'pullRequest': "
                        "{'reviewThreads': {'pageInfo': {'hasNextPage': False, 'endCursor': None}, 'nodes': %r}}}}}))\n" % nodes)
        fake.chmod(0o755)
        self.env["WAVES_GH"] = str(fake)
        r = self.run_cli("threads", "--pr", "7")
        self.assertEqual((r["unresolved"], r["unanswered"]), (2, 1))
        bot = r["threads"][0]
        self.assertEqual((bot["id"], bot["bot"], bot["replies"], bot["title"]), ("T1", True, 0, "CodeQL / Generic catch clause"))
        self.assertEqual((r["threads"][1]["bot"], r["threads"][1]["outdated"], r["threads"][1]["replies"]), (False, True, 1))

    def test_code_scanning_state_uses_newest_run_per_name(self):
        sys.path.insert(0, str(SCRIPT.parent))
        import wave_mail
        runs = [{"id": 1, "name": "analyze (csharp)", "status": "completed", "completed_at": "2026-10-30T22:19:22Z"},
                {"id": 2, "name": "CodeQL", "status": "completed", "completed_at": "2026-10-30T22:17:35Z"},
                {"id": 3, "name": "ci", "status": "in_progress"}]
        self.assertEqual(wave_mail.code_scanning_state(runs), ("done", "2026-10-30T22:19:22Z"))
        runs.append({"id": 4, "name": "analyze (csharp)", "status": "in_progress"})
        self.assertEqual(wave_mail.code_scanning_state(runs)[0], "pending")
        self.assertEqual(wave_mail.code_scanning_state([{"id": 5, "name": "ci", "status": "completed"}])[0], "absent")


    FAKE_GH = r"""#!/usr/bin/env python3
import json, os, sys
args = sys.argv[1:]
with open(os.environ["FAKE_GH_LOG"], "a") as f:
    f.write(json.dumps(args) + "\n")
fields = {a.split("=", 1)[0]: a.split("=", 1)[1] for a in args if "=" in a}
q = fields.get("query", "")
fail = os.environ.get("FAKE_GH_FAIL", "")
def page(nodes, nxt, cur):
    return {"data": {"repository": {"pullRequest": {"reviewThreads": {
        "pageInfo": {"hasNextPage": nxt, "endCursor": cur}, "nodes": nodes}}}}}
def node(i):
    return {"id": i, "isResolved": False, "isOutdated": False, "path": "a.cs", "line": None,
            "comments": {"totalCount": 1, "nodes": [{"author": {"login": "github-advanced-security"},
                         "body": "## CodeQL / X\n[more](https://github.com/o/r/security/code-scanning/772)"}]}}
if "reviewThreads" in q:
    print(json.dumps(page([node("T2")], False, None) if fields.get("c") == "CUR1" else page([node("T1")], True, "CUR1")))
elif "addPullRequestReviewThreadReply" in q:
    if fail == "reply": sys.exit(1)
    print(json.dumps({"data": {"addPullRequestReviewThreadReply": {"comment": {"url": "https://x/r1"}}}}))
elif "resolveReviewThread" in q:
    if fail == "resolve": sys.exit(1)
    print(json.dumps({"data": {"resolveReviewThread": {"thread": {"isResolved": True}}}}))
elif "node(id" in q:
    first = node("x")["comments"]["nodes"][0]
    if fail == "noalert": first = {"body": "a human comment"}
    print(json.dumps({"data": {"node": {"comments": {"nodes": [first]}}}}))
elif "-X" in args and "PATCH" in args:
    if fail == "patch": sys.exit(1)
    print("{}")
else:
    sys.exit(2)
"""

    def fake_gh(self, fail=""):
        gh = Path(self.tmp.name) / "fakegh"
        gh.write_text(self.FAKE_GH)
        gh.chmod(0o755)
        log = Path(self.tmp.name) / "gh.log"
        log.write_text("")
        self.env.update(WAVES_GH=str(gh), FAKE_GH_LOG=str(log), FAKE_GH_FAIL=fail)
        return log

    @staticmethod
    def calls(log):
        return [json.loads(line) for line in log.read_text().splitlines()]

    def test_threads_paginate_from_a_null_cursor_with_typed_arguments(self):
        log = self.fake_gh()
        r = self.run_cli("threads", "--pr", "7")
        self.assertEqual([t["id"] for t in r["threads"]], ["T1", "T2"])
        first, second = self.calls(log)
        self.assertIn("n=7", first[first.index("-F") + 1:])  # the PR number goes as an int (-F)
        self.assertFalse(any(a.startswith("c=") for a in first))  # no cursor on the first page
        self.assertEqual(second[second.index("c=CUR1") - 1], "-f")
        self.assertEqual((r["threads"][0]["where"], r["threads"][0]["alert"]), ("a.cs", 772))  # null line: no ":None"

    def test_resolve_thread_never_resolves_after_a_failed_reply(self):
        log = self.fake_gh(fail="reply")
        r = self.run_cli("resolve-thread", "--id", "T1", "--reply", "x")
        self.assertIn("error", r)
        self.assertFalse(any("resolveReviewThread" in " ".join(c) for c in self.calls(log)))

    def test_resolve_thread_keep_open_only_replies(self):
        log = self.fake_gh()
        r = self.run_cli("resolve-thread", "--id", "T1", "--reply", "x", "--keep-open")
        self.assertEqual(r["reply_url"], "https://x/r1")
        self.assertNotIn("resolved", r)
        self.assertFalse(any("resolveReviewThread" in " ".join(c) for c in self.calls(log)))

    def test_resolve_thread_keeps_reply_url_when_the_resolve_fails(self):
        self.fake_gh(fail="resolve")
        r = self.run_cli("resolve-thread", "--id", "T1", "--reply", "x")
        self.assertEqual(r["reply_url"], "https://x/r1")
        self.assertIn("error", r)

    def test_resolve_thread_dismiss_dismisses_the_linked_alert(self):
        log = self.fake_gh()
        r = self.run_cli("resolve-thread", "--id", "T1", "--reply", "intended", "--dismiss", "used in tests")
        self.assertEqual((r["resolved"], r["dismissed_alert"]), (True, 772))
        patch = [c for c in self.calls(log) if "PATCH" in c][0]
        self.assertIn("repos/fatal10110/turboHTTP/code-scanning/alerts/772", patch)
        self.assertIn("dismissed_reason=used in tests", patch)
        self.assertIn("state=dismissed", patch)
        self.assertIn("dismissed_comment=intended", patch)

    def test_resolve_thread_dismiss_without_an_alert_posts_nothing(self):
        log = self.fake_gh(fail="noalert")
        r = self.run_cli("resolve-thread", "--id", "T1", "--reply", "x", "--dismiss", "false positive")
        self.assertIn("nothing was posted", r["error"])
        self.assertFalse(any("addPullRequestReviewThreadReply" in " ".join(c) or "resolveReviewThread" in " ".join(c)
                             for c in self.calls(log)))

    def test_failed_dismissal_is_retried_without_a_second_reply(self):
        self.fake_gh(fail="patch")
        r = self.run_cli("resolve-thread", "--id", "T1", "--reply", "x", "--dismiss", "won't fix")
        self.assertEqual((r["reply_url"], r["resolved"]), ("https://x/r1", True))
        self.assertIn("error", r)
        log = self.fake_gh()
        r = self.run_cli("resolve-thread", "--id", "T1", "--dismiss", "won't fix")
        self.assertEqual(r["dismissed_alert"], 772)
        self.assertFalse(any("addPullRequestReviewThreadReply" in " ".join(c) for c in self.calls(log)))

    def test_replies_carry_the_agent_marker_and_threads_classify_authors(self):
        log = self.fake_gh()
        self.run_cli("resolve-thread", "--id", "T1", "--reply", "fixed")
        reply = [c for c in self.calls(log) if "addPullRequestReviewThreadReply" in " ".join(c)][0]
        self.assertIn("b=fixed\n\n<!-- waves -->", reply)
        sys.path.insert(0, str(SCRIPT.parent))
        import wave_mail
        mk = lambda i, login, body: {"id": i, "isResolved": False, "comments": {"totalCount": 1, "nodes": [{"author": {"login": login}, "body": body}]}}
        r = wave_mail.summarize_threads([mk("B", "github-advanced-security", "x"), mk("A", "fatal10110", "[nit] y\n<!-- waves -->"),
                                         mk("H", "fatal10110", "please rename")])
        self.assertEqual([(t["id"], t["bot"], t["agent"], t["human"]) for t in r["threads"]],
                         [("B", True, False, False), ("A", False, True, False), ("H", False, False, True)])
        self.assertEqual(r["human"], 1)

    def test_scanning_wait_state_never_reports_done_inside_the_grace(self):
        sys.path.insert(0, str(SCRIPT.parent))
        import wave_mail
        done = [{"id": 1, "name": "analyze (csharp)", "status": "completed", "completed_at": "2026-10-30T22:19:22Z"}]
        t = 1793398762.0  # 2026-10-30T22:19:22Z
        self.assertEqual(wave_mail.scanning_wait_state(done, t + 5, grace=60), "settling")
        self.assertEqual(wave_mail.scanning_wait_state(done, t + 61, grace=60), "done")
        self.assertEqual(wave_mail.scanning_wait_state([], t), "pending")  # right after a push
        self.assertEqual(wave_mail.scanning_wait_state([{"id": 2, "name": "lint", "status": "completed"}], t), "absent")


if __name__ == "__main__":
    unittest.main()
