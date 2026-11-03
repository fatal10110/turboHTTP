"""Tests for which paths Tools~/ci/check_repo.py treats as Unity-imported (#6)."""
import importlib.util
import unittest
from pathlib import Path

CHECK_REPO = Path(__file__).resolve().parent.parent / "ci" / "check_repo.py"


def load_module():
    spec = importlib.util.spec_from_file_location("check_repo", CHECK_REPO)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class UnityImportedTest(unittest.TestCase):
    def setUp(self):
        self.m = load_module()

    def test_internal_docs_folder_is_hidden_from_unity(self):
        self.assertFalse(self.m.unity_imported("Development~/docs/README.md"))
        self.assertFalse(self.m.unity_imported("Development~/docs/implementation-journal/2026-11-x.md"))

    def test_folder_without_tilde_is_imported_and_needs_meta(self):
        # Before #6 `Development/` was exempted by name; nothing at the top level is exempt now,
        # so a stray `Development/` (or any new folder) must carry .meta files like Runtime/.
        self.assertEqual(set(), self.m.META_EXEMPT_TOP)
        self.assertTrue(self.m.unity_imported("Development/docs/README.md"))
        self.assertTrue(self.m.unity_imported("Runtime/Core/UHttpClient.cs"))

    def test_hidden_and_tilde_folders_are_not_imported(self):
        for path in ("Tools~/ci/check.sh", "Samples~/Basic/A.cs", ".github/workflows/release.yml", "Runtime/.x/a.cs"):
            self.assertFalse(self.m.unity_imported(path), path)


if __name__ == "__main__":
    unittest.main()
