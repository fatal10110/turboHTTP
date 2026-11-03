"""Tests for Tools~/ci/release_notes.py (#101): tag/version check and CHANGELOG extraction."""
import contextlib
import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent.parent / "ci" / "release_notes.py"

CHANGELOG = """# Changelog

Intro text.

## [Unreleased]

### Fixed
- not yet released

## [1.0.0-pre.2] - 2026-11-20

### Fixed
- second pre-release fix

## [1.0.0-pre.1] - 2026-11-02

First pre-release.

### Modules
- Core

## [0.9.0]

[1.0.0-pre.2]: https://github.com/fatal10110/turboHTTP/compare/v1.0.0-pre.1...v1.0.0-pre.2
"""


def load_module():
    spec = importlib.util.spec_from_file_location("release_notes", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ReleaseNotesTest(unittest.TestCase):
    def setUp(self):
        self.m = load_module()
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.write("1.0.0-pre.2", CHANGELOG)

    def write(self, version, changelog):
        (self.root / "package.json").write_text(json.dumps({"name": "com.turbohttp.complete", "version": version}))
        (self.root / "CHANGELOG.md").write_text(changelog)

    def notes(self, tag):
        return self.m.release_notes(tag, str(self.root))

    def test_extracts_only_the_tagged_section(self):
        notes = self.notes("v1.0.0-pre.2")
        self.assertEqual("### Fixed\n- second pre-release fix\n", notes)

    def test_section_keeps_subheadings_and_stops_at_next_release(self):
        self.write("1.0.0-pre.1", CHANGELOG)
        notes = self.notes("v1.0.0-pre.1")
        self.assertTrue(notes.startswith("First pre-release."))
        self.assertIn("### Modules\n- Core", notes)
        self.assertNotIn("0.9.0", notes)

    def test_stops_at_link_references(self):
        self.write("0.9.0", CHANGELOG.replace("## [0.9.0]\n", "## [0.9.0]\n- old\n"))
        self.assertEqual("- old\n", self.notes("v0.9.0"))

    def test_tag_must_match_package_version(self):
        with self.assertRaisesRegex(self.m.ReleaseError, "does not match package.json"):
            self.notes("v1.0.0-pre.1")

    def test_tag_must_be_semver_with_v_prefix(self):
        for tag in ("1.0.0", "v1.0", "v1.0.0-", "v1.0.0+build", "release-1.0.0", "v1.0.0-pre..1"):
            with self.assertRaises(self.m.ReleaseError, msg=tag):
                self.m.version_from_tag(tag)
        self.assertEqual("1.0.0", self.m.version_from_tag("v1.0.0"))
        self.assertEqual("1.0.0-pre.10", self.m.version_from_tag("v1.0.0-pre.10"))

    def test_missing_or_empty_section_fails(self):
        self.write("1.0.1", CHANGELOG)
        with self.assertRaisesRegex(self.m.ReleaseError, "no '## \\[1.0.1\\]' section"):
            self.notes("v1.0.1")
        self.write("0.9.0", CHANGELOG)
        with self.assertRaisesRegex(self.m.ReleaseError, "is empty"):
            self.notes("v0.9.0")

    def test_unreleased_is_never_a_tag(self):
        with self.assertRaises(self.m.ReleaseError):
            self.m.version_from_tag("vUnreleased")

    def test_cli_exit_codes(self):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            self.assertEqual(0, self.m.main(["v1.0.0-pre.2", "--root", str(self.root)]))
            self.assertEqual(1, self.m.main(["v9.9.9", "--root", str(self.root)]))
        self.assertEqual("### Fixed\n- second pre-release fix\n", out.getvalue())
        self.assertIn("does not match package.json", err.getvalue())

    def test_repository_changelog_has_section_for_package_version(self):
        root = SCRIPT.parent.parent.parent
        version = json.loads((root / "package.json").read_text(encoding="utf-8"))["version"]
        self.assertTrue(self.m.release_notes("v" + version, str(root)).strip())


if __name__ == "__main__":
    unittest.main()
