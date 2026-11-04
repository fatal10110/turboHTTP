"""Tests for the `ignores` check in Tools~/ci/check_repo.py (#190)."""
import importlib.util
import os
import tempfile
import unittest
from pathlib import Path

CHECK_REPO = Path(__file__).resolve().parent.parent / "ci" / "check_repo.py"


def load_module():
    spec = importlib.util.spec_from_file_location("check_repo", CHECK_REPO)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class IgnoresCheckTest(unittest.TestCase):
    def setUp(self):
        self.module = load_module()
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.module.ROOT = self.tmp.name

    def run_check(self, source, name="Tests/Runtime/Sample/SampleTests.cs"):
        path = os.path.join(self.tmp.name, name)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        Path(path).write_text(source, encoding="utf-8")
        return self.module.check_ignores([name])

    def test_unreferenced_assert_ignore_fails(self):
        errors = self.run_check('class T { void A() { Assert.Ignore("x"); } }')
        self.assertEqual(1, len(errors))
        self.assertIn("SampleTests.cs:1", errors[0])

    def test_issue_reference_passes(self):
        self.assertEqual([], self.run_check('class T { void A() { Assert.Ignore("needs #152 harness"); } }'))

    def test_env_marker_passes(self):
        self.assertEqual([], self.run_check('class T { void A() { Assert.Ignore("[env] no network"); } }'))

    def test_prefix_constant_passes(self):
        self.assertEqual([], self.run_check('class T { void A() { Assert.Ignore(EnvironmentIgnorePrefix + reason); } }'))

    def test_environment_helper_call_passes(self):
        self.assertEqual([], self.run_check('class T { void A() { TestRequirements.IgnoreEnvironment("no network"); } }'))

    def test_reference_inside_interpolation_and_after_nested_parens(self):
        src = 'class T { void A() { Assert.Ignore($"{Foo(1)} see #12"); Assert.Ignore(string.Format("a)b {0}", 1)); } }'
        errors = self.run_check(src)
        self.assertEqual(1, len(errors))  # only the second call lacks a reference

    def test_inconclusive_and_ignore_attribute_are_checked(self):
        errors = self.run_check('class T { [Test, Ignore("later")] void A() { Assert.Inconclusive("maybe"); } }')
        self.assertEqual(2, len(errors))
        self.assertEqual([], self.run_check('class T { [Ignore("tracked in #5")] [Test] void A() { } }'))

    def test_assume_is_checked(self):
        errors = self.run_check('class T { void A() { Assume.That(x, Is.EqualTo(0), "window open"); Assume.True(y); } }')
        self.assertEqual(2, len(errors))
        self.assertEqual([], self.run_check('class T { void A() { Assume.That(x, Is.EqualTo(0), "[env] window open"); Assume.True(y, "see #12"); } }'))

    def test_bare_ignore_attribute_fails(self):
        self.assertEqual(1, len(self.run_check('class T { [Ignore] [Test] void A() { } }')))

    def test_ignore_property_on_test_case_is_checked(self):
        self.assertEqual(1, len(self.run_check('class T { [TestCase(1, Ignore = "later")] void A(int x) { } }')))
        self.assertEqual([], self.run_check('class T { [TestCase(1, Ignore = "tracked in #9")] void A(int x) { } }'))

    def test_comments_and_non_test_files_are_ignored(self):
        self.assertEqual([], self.run_check('// Assert.Ignore("x");\nclass T { }'))
        self.assertEqual([], self.run_check('class T { void A() { Assert.Ignore("x"); } }', name="Runtime/Core/T.cs"))


if __name__ == "__main__":
    unittest.main()
