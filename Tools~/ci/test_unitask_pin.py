#!/usr/bin/env python3
"""Tests for the UniTask pin (#311): unitask_pin.py and the shadow build's UniTask wiring.

No network: the fetch runs against a local git repository over file://.
Run: python3 Tools~/ci/test_unitask_pin.py
"""
import contextlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import shadow_build  # noqa: E402
import unitask_pin  # noqa: E402


def git(cwd, *args):
    env = dict(os.environ, GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@t", GIT_COMMITTER_NAME="t",
               GIT_COMMITTER_EMAIL="t@t")
    return subprocess.run(["git", "-c", "init.defaultBranch=main", "-c", "commit.gpgsign=false", "-c",
                           "tag.gpgsign=false"] + list(args), cwd=cwd, env=env, check=True,
                          capture_output=True, text=True).stdout.strip()


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as fh:
        fh.write(text)


def cli(*args):
    """unitask_pin.main with its messages captured; returns the exit code."""
    with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
        return unitask_pin.main(["unitask_pin.py"] + list(args))


def asmdef(name, **kw):
    data = {"name": name, "references": [], "_dir": "/nonexistent/" + name}
    data.update(kw)
    return data


class Pin(unittest.TestCase):
    def test_pin_is_a_tag_and_a_full_commit_id(self):
        self.assertRegex(unitask_pin.SHA, r"^[0-9a-f]{40}$")
        self.assertRegex(unitask_pin.TAG, r"^2\.\d+\.\d+$")

    def test_manifest_url_points_at_the_package_folder_and_tag(self):
        self.assertEqual(unitask_pin.manifest_url(),
                         "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask#"
                         + unitask_pin.TAG)


class Lock(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.path = os.path.join(self.dir, "packages-lock.json")

    def tearDown(self):
        shutil.rmtree(self.dir)

    def lock(self, entry):
        deps = {"com.unity.test-framework": {"version": "1.1.33", "source": "registry"}}
        if entry is not None:
            deps[unitask_pin.PACKAGE] = entry
        write(self.path, json.dumps({"dependencies": deps}))

    def entry(self, **kw):
        e = {"version": unitask_pin.manifest_url(), "depth": 0, "source": "git", "dependencies": {},
             "hash": unitask_pin.SHA}
        e.update(kw)
        return e

    def test_the_pinned_commit_passes(self):
        self.lock(self.entry())
        self.assertEqual(unitask_pin.check_lock(self.path)["hash"], unitask_pin.SHA)

    def test_another_commit_fails(self):
        self.lock(self.entry(hash="0" * 40))
        with self.assertRaisesRegex(unitask_pin.PinError, "resolved commit"):
            unitask_pin.check_lock(self.path)

    def test_a_missing_entry_fails(self):
        self.lock(None)
        with self.assertRaisesRegex(unitask_pin.PinError, "no com.cysharp.unitask"):
            unitask_pin.check_lock(self.path)

    def test_a_registry_install_fails(self):
        self.lock(self.entry(source="registry", version=unitask_pin.TAG))
        with self.assertRaisesRegex(unitask_pin.PinError, "source"):
            unitask_pin.check_lock(self.path)

    def test_cli_exit_codes(self):
        self.lock(self.entry(hash="1" * 40))
        self.assertEqual(cli("check-lock", self.path), 2)
        self.lock(self.entry())
        self.assertEqual(cli("check-lock", self.path), 0)
        self.assertEqual(cli("check-lock", self.path, "file:C:/cache/unitask"), 2)

    def test_the_local_copy_passes_only_when_named(self):
        copy = "file:C:/home/u/AppData/Local/turbohttp/unitask-" + unitask_pin.SHA
        # Unity may write the path with backslashes or another drive-letter case.
        self.lock({"version": copy.replace("/", "\\").replace("file:C", "file:c"), "depth": 0,
                   "source": "local", "dependencies": {}})
        self.assertEqual(unitask_pin.check_lock(self.path, local=copy)["source"], "local")
        self.assertEqual(cli("check-lock", self.path, copy), 0)
        with self.assertRaisesRegex(unitask_pin.PinError, "local copy"):
            unitask_pin.check_lock(self.path, local="file:C:/elsewhere/unitask")
        # A git install does not pass the local check, nor a local install the git check.
        with self.assertRaisesRegex(unitask_pin.PinError, "source"):
            unitask_pin.check_lock(self.path)
        self.lock(self.entry())
        with self.assertRaisesRegex(unitask_pin.PinError, "local copy"):
            unitask_pin.check_lock(self.path, local=copy)


class Results(unittest.TestCase):
    def test_counts_only_executed_adapter_cases(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "r.xml")
            ns = unitask_pin.TEST_NAMESPACE
            write(path, f"""<test-run><test-suite>
  <test-case fullname="{ns}UniTaskCancellationTests.A" result="Passed" />
  <test-case fullname="{ns}UniTaskCancellationTests.B" result="Skipped" />
  <test-case fullname="TurboHTTP.Tests.Core.Other" result="Passed" />
</test-suite></test-run>""")
            self.assertEqual(unitask_pin.adapter_cases(path), (1, 2))
            self.assertEqual(cli("check-results", path), 0)
            write(path, '<test-run><test-case fullname="TurboHTTP.Tests.Core.Other" result="Passed" /></test-run>')
            self.assertEqual(cli("check-results", path), 4)


class Fetch(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.remote = os.path.join(self.dir, "remote")
        os.makedirs(self.remote)
        git(self.remote, "init", "-q")
        write(os.path.join(self.remote, unitask_pin.PACKAGE_PATH, "Runtime", "UniTask.asmdef"),
              json.dumps({"name": "UniTask", "references": []}))
        write(os.path.join(self.remote, "README.md"), "outside the package\n")
        git(self.remote, "add", "-A")
        git(self.remote, "commit", "-q", "-m", "one")
        self.sha = git(self.remote, "rev-parse", "HEAD")
        git(self.remote, "tag", "2.0.0")
        write(os.path.join(self.remote, "later.txt"), "x\n")
        git(self.remote, "add", "-A")
        git(self.remote, "commit", "-q", "-m", "two")
        self.later = git(self.remote, "rev-parse", "HEAD")
        self.url = "file://" + self.remote
        self.cache = os.path.join(self.dir, "cache")

    def tearDown(self):
        shutil.rmtree(self.dir)

    def test_fetches_and_verifies_the_pinned_commit_once(self):
        pkg = unitask_pin.fetch(self.cache, self.url, self.sha, "2.0.0")
        self.assertTrue(os.path.isfile(os.path.join(pkg, "Runtime", "UniTask.asmdef")))
        self.assertEqual(git(os.path.join(self.cache, self.sha), "rev-parse", "HEAD"), self.sha)
        # Cached: no remote needed the second time.
        shutil.rmtree(self.remote)
        self.assertEqual(unitask_pin.fetch(self.cache, self.url, self.sha, "2.0.0"), pkg)

    def test_a_tag_that_moved_away_from_the_sha_fails(self):
        with self.assertRaisesRegex(unitask_pin.PinError, "tag 2.0.0 is"):
            unitask_pin.fetch(self.cache, self.url, self.later, "2.0.0")
        self.assertFalse(os.path.exists(os.path.join(self.cache, self.later)))

    def test_an_unknown_commit_fails(self):
        with self.assertRaises(unitask_pin.PinError):
            unitask_pin.fetch(self.cache, self.url, "f" * 40, "2.0.0", check_tag=False)
        self.assertFalse(os.path.exists(os.path.join(self.cache, "f" * 40)))

    def test_a_short_sha_is_rejected(self):
        with self.assertRaisesRegex(unitask_pin.PinError, "40-hex"):
            unitask_pin.fetch(self.cache, self.url, self.sha[:12], "2.0.0")


class ShadowBuild(unittest.TestCase):
    def test_version_expressions(self):
        vm = shadow_build.version_matches
        self.assertTrue(vm("2.5.11", "2.0.0"))
        self.assertFalse(vm("1.9.9", "2.0.0"))
        self.assertTrue(vm("2.5.11", "[2.0,3.0)"))
        self.assertFalse(vm("3.0.0", "[2.0,3.0)"))
        self.assertFalse(vm("2.0.0", "(2.0,3.0]"))
        self.assertTrue(vm("2.5.11", "[2.5.11]"))
        self.assertTrue(vm("2.5.11", ""))

    def test_unitask_asmdef_loads_as_external(self):
        with tempfile.TemporaryDirectory() as d:
            write(os.path.join(d, "Runtime", "UniTask.asmdef"), json.dumps({"name": "UniTask"}))
            loaded = shadow_build.unitask_package(d)
        self.assertEqual(list(loaded), ["UniTask"])
        self.assertTrue(loaded["UniTask"]["_external"])

    def test_adapters_build_only_with_the_package_and_get_their_version_define(self):
        version_define = [{"name": unitask_pin.PACKAGE, "expression": "2.0.0", "define": "TURBOHTTP_UNITASK"}]
        asmdefs = {
            "TurboHTTP.Core": asmdef("TurboHTTP.Core"),
            "UniTask": asmdef("UniTask", _external=True),
            "TurboHTTP.UniTask": asmdef("TurboHTTP.UniTask", references=["TurboHTTP.Core", "UniTask"],
                                        defineConstraints=["TURBOHTTP_UNITASK"], versionDefines=version_define),
        }
        with tempfile.TemporaryDirectory() as d:
            dlls = os.path.join(d, "dlls")
            os.makedirs(dlls)
            out = os.path.join(d, "out")
            _, projects, skipped, dropped = shadow_build.generate("editor", asmdefs, dlls, out, docs=False)
            self.assertNotIn("TurboHTTP.UniTask", projects)
            self.assertIn("TurboHTTP.UniTask", skipped)
            _, projects, skipped, dropped = shadow_build.generate(
                "editor", asmdefs, dlls, out, docs=False, packages={unitask_pin.PACKAGE: unitask_pin.TAG})
            self.assertEqual(dropped, [])
            self.assertIn("TurboHTTP.UniTask", projects)
            self.assertIn("UniTask", projects)

            def proj(name):
                with open(os.path.join(out, "editor", name, name + ".csproj")) as fh:
                    return fh.read()
            self.assertIn("TURBOHTTP_UNITASK", proj("TurboHTTP.UniTask"))
            # Unity scopes a versionDefine to its own asmdef.
            self.assertNotIn("TURBOHTTP_UNITASK", proj("TurboHTTP.Core"))
            self.assertIn("<RunAnalyzers>false</RunAnalyzers>", proj("UniTask"))
            # No tests (interop/soak/fuzz builds): the external UniTask and the adapters stay out.
            _, projects, _, dropped = shadow_build.generate(
                "editor", asmdefs, dlls, out, tests=False, docs=False)
            self.assertEqual(dropped, [])
            self.assertNotIn("TurboHTTP.UniTask", projects)


if __name__ == "__main__":
    unittest.main()
