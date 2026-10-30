import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("issue_search.py")


class IssueSearchTest(unittest.TestCase):
    def setUp(self):
        self.tempdir = tempfile.TemporaryDirectory()
        self.cache = Path(self.tempdir.name) / "issues.json"
        self.cache.write_text(
            json.dumps(
                [
                    {
                        "number": 10,
                        "title": "Collision radius follow-up",
                        "state": "OPEN",
                        "body": "Visibility still treats creatures as points.",
                        "comments": [],
                        "labels": [{"name": "enhancement"}],
                        "milestone": {"title": "M5 - World runtime"},
                        "url": "https://example.test/issues/10",
                    },
                    {
                        "number": 11,
                        "title": "Deferred timer work",
                        "state": "OPEN",
                        "body": "Follow-up details are in discussion.",
                        "comments": [
                            {
                                "body": "The missing behavior is panic recovery around the timer callback.",
                                "author": {"login": "reviewer"},
                            }
                        ],
                        "labels": [{"name": "bug"}],
                        "milestone": {"title": "M6 - Skills & effects engine"},
                        "url": "https://example.test/issues/11",
                    },
                    {
                        "number": 12,
                        "title": "Panic recovery for scheduled callbacks",
                        "state": "CLOSED",
                        "body": "Completed elsewhere.",
                        "comments": [],
                        "labels": [{"name": "bug"}],
                        "milestone": {"title": "M6 - Skills & effects engine"},
                        "url": "https://example.test/issues/12",
                    },
                ]
            ),
            encoding="utf-8",
        )
        self.gh = Path(self.tempdir.name) / "gh"
        self.gh.write_text(
            "#!/bin/sh\n"
            "if [ \"$3\" = 12 ]; then\n"
            "  printf '%s\\n' '{\"number\":12,\"title\":\"Panic recovery for scheduled callbacks\",\"state\":\"CLOSED\",\"body\":\"Completed elsewhere.\",\"comments\":[],\"labels\":[{\"name\":\"bug\"}],\"milestone\":{\"title\":\"M6 - Skills & effects engine\"},\"url\":\"https://example.test/issues/12\"}'\n"
            "else\n"
            "  printf '%s\\n' '{\"number\":11,\"title\":\"Deferred timer work\",\"state\":\"OPEN\",\"body\":\"Follow-up details are in discussion.\",\"comments\":[{\"body\":\"The missing behavior is panic recovery around the timer callback.\",\"author\":{\"login\":\"reviewer\"}}],\"labels\":[{\"name\":\"bug\"}],\"milestone\":{\"title\":\"M6 - Skills & effects engine\"},\"url\":\"https://example.test/issues/11\"}'\n"
            "fi\n",
            encoding="utf-8",
        )
        self.gh.chmod(0o755)

    def tearDown(self):
        self.tempdir.cleanup()

    def run_search(self, *args):
        return subprocess.run(
            [sys.executable, str(SCRIPT), *args, "--cache", str(self.cache)],
            capture_output=True,
            text=True,
            env={**os.environ, "PATH": f"{self.tempdir.name}:{os.environ['PATH']}"},
        )

    def test_refreshes_matching_issue_state_and_comments_from_github(self):
        self.gh.write_text(
            "#!/bin/sh\n"
            "printf '%s\\n' '{\"number\":11,\"title\":\"Deferred timer work\",\"state\":\"CLOSED\",\"body\":\"Follow-up details are in discussion.\",\"comments\":[{\"body\":\"Remote timer callback fix is merged.\",\"author\":{\"login\":\"maintainer\"}}],\"labels\":[{\"name\":\"bug\"}],\"milestone\":{\"title\":\"M6 - Skills & effects engine\"},\"url\":\"https://example.test/issues/11\"}'\n",
            encoding="utf-8",
        )
        self.gh.chmod(0o755)
        environment = {**os.environ, "PATH": f"{self.tempdir.name}:{os.environ['PATH']}"}

        result = subprocess.run(
            [sys.executable, str(SCRIPT), "timer callback", "--state", "open", "--cache", str(self.cache)],
            capture_output=True,
            text=True,
            env=environment,
        )

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, "no cached issues matched\n")
        refreshed = next(issue for issue in json.loads(self.cache.read_text()) if issue["number"] == 11)
        self.assertEqual(refreshed["state"], "CLOSED")
        self.assertEqual(refreshed["comments"][0]["author"]["login"], "maintainer")

    def test_finds_terms_present_only_in_comments(self):
        result = self.run_search("timer callback", "--state", "open")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("#11 [OPEN]", result.stdout)
        self.assertIn("comment by reviewer", result.stdout)

    def test_applies_milestone_and_label_filters(self):
        result = self.run_search(
            "panic recovery",
            "--state",
            "open",
            "--milestone",
            "M6",
            "--label",
            "bug",
        )

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("#11 [OPEN]", result.stdout)
        self.assertNotIn("#12", result.stdout)

    def test_ranks_title_match_above_comment_match_and_honors_limit(self):
        result = self.run_search("panic recovery", "--limit", "1")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("#12 [CLOSED]", result.stdout)
        self.assertNotIn("#11", result.stdout)

    def test_sync_builds_a_missing_cache(self):
        self.gh.write_text(
            "#!/bin/sh\n"
            "if [ \"$2\" = list ]; then\n"
            "  printf '%s\\n' '[{\"number\":7,\"title\":\"HPACK desync\",\"state\":\"OPEN\",\"body\":\"x\",\"comments\":[],\"labels\":[],\"milestone\":null,\"url\":\"u\"}]'\n"
            "else\n"
            "  printf '%s\\n' '{\"number\":7,\"title\":\"HPACK desync\",\"state\":\"OPEN\",\"body\":\"x\",\"comments\":[],\"labels\":[],\"milestone\":null,\"url\":\"u\"}'\n"
            "fi\n",
            encoding="utf-8",
        )
        self.cache.unlink()
        result = self.run_search("hpack")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("#7 [OPEN] No milestone", result.stdout)
        self.assertTrue(self.cache.exists())


if __name__ == "__main__":
    unittest.main()
