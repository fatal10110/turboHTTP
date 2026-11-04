#!/usr/bin/env python3
"""Regression test for compiling the Tests/ asmdefs in the shadow build (#297).

Runs the real generator (shadow_build.generate / run_variants) on scratch test asmdefs shaped like
Tests/Runtime (UNITY_INCLUDE_TESTS constraint, UnityEngine/UnityEditor.TestRunner references,
nunit.framework.dll as a precompiled reference) against the NUnit and Test Framework packages Unity
2021.3 bundles. It checks that a plain compile error, Assert.Multiple and Assert.ThrowsAsync (absent
from Unity's NUnit) and an unresolvable reference each fail the build, which fails check.sh, and that
a test using Assert.Throws, [UnityTest] and LogAssert compiles in the editor and in a player variant.
Before #297 the shadow build never compiled Tests/, so none of these failures surfaced.

Needs the .NET SDK and the fetched Unity DLLs + packages (Tools~/ci/fetch-unity-dlls.sh). Skips when
they are missing locally; fails under CI, where check.sh --full always has them.
Builds in .build/shadow-tests-selftest.
"""
import json
import os
import shutil
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import shadow_build  # noqa: E402

WORK = os.path.join(shadow_build.ROOT, ".build", "shadow-tests-selftest")

HEADER = """using System;
using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ShadowBuildSelfTest
{
    public class Fixture
    {
"""
FOOTER = """    }
}
"""

BAD = {
    "CompileError": """        [Test]
        public void Undefined() { Assert.AreEqual(1, notDeclaredAnywhere); }
""",
    "AssertMultiple": """        [Test]
        public void Multiple() { Assert.Multiple(() => { Assert.AreEqual(1, 1); }); }
""",
    "AssertThrowsAsync": """        [Test]
        public void ThrowsAsync() { Assert.ThrowsAsync<InvalidOperationException>(async () => await Task.Yield()); }
""",
}
# Must fail with the rule, in the file named after the case.
EXPECTED = {"CompileError": "CS0103", "AssertMultiple": "CS0117", "AssertThrowsAsync": "CS0117"}

VALID = """        [Test]
        public void Throws()
        {
            Assert.Throws<InvalidOperationException>(() => throw new InvalidOperationException());
            Assert.That(1, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Logs()
        {
            LogAssert.Expect(LogType.Log, "shadow");
            Debug.Log("shadow");
            yield return null;
            LogAssert.NoUnexpectedReceived();
        }
"""


def write_case(src, name, body, references=("UnityEngine.TestRunner", "UnityEditor.TestRunner")):
    d = os.path.join(src, "Tests", name)
    os.makedirs(d)
    asmdef = {"name": name, "references": list(references), "includePlatforms": [], "excludePlatforms": [],
              "overrideReferences": True, "precompiledReferences": ["nunit.framework.dll"],
              "autoReferenced": False, "defineConstraints": ["UNITY_INCLUDE_TESTS"]}
    with open(os.path.join(d, name + ".asmdef"), "w") as f:
        json.dump(asmdef, f)
    with open(os.path.join(d, name + ".cs"), "w") as f:
        f.write(HEADER + body + FOOTER)


class ShadowBuildTestsTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        missing = None
        if shutil.which("dotnet") is None:
            missing = "dotnet not on PATH"
        else:
            try:
                cls.dlls = shadow_build.unity_dlls()
            except SystemExit as e:
                missing = str(e)
            else:
                cls.framework, cls.precompiled = shadow_build.test_framework(cls.dlls)
                if cls.framework is None:
                    missing = "Unity test packages not fetched (Tools~/ci/fetch-unity-dlls.sh)"
        if missing:
            if os.environ.get("CI"):
                raise AssertionError("CI must run this test: " + missing)
            raise unittest.SkipTest(missing)

    def build(self, label, cases, variants=("editor",)):
        src = os.path.join(WORK, label, "src")
        shutil.rmtree(src, ignore_errors=True)
        for args in cases:
            write_case(src, *args)
        asmdefs = shadow_build.load_asmdefs(src, ["Tests"])
        self.assertTrue(all(a["_tests"] for a in asmdefs.values()))
        asmdefs.update(self.framework)
        return shadow_build.run_variants(list(variants), asmdefs, self.dlls, out=os.path.join(WORK, label, "out"),
                                         precompiled=self.precompiled, docs=False)

    def test_compile_error_and_missing_nunit_apis_fail(self):
        cases = [(n, b) for n, b in BAD.items()] + [("UnresolvedReference", VALID, ("UnityEngine.TestRunner", "Not.Built"))]
        failed, _, errors = self.build("bad", cases)
        self.assertTrue(failed, "the shadow build (and so check.sh) must fail")
        for name, rule in EXPECTED.items():
            hits = [e for e in errors if os.path.basename(e["file"]) == name + ".cs" and e["rule"] == rule]
            self.assertTrue(hits, f"{name}: expected {rule}, got {errors}")
        self.assertIn({"file": "", "line": "0", "rule": "DROPPED", "msg": "UnresolvedReference"}, errors)

    def test_unity_nunit_and_test_runner_apis_compile(self):
        # ios: the editor-only UnityEditor.TestRunner reference is ignored, as in a Unity player build.
        failed, _, errors = self.build("good", [("ValidTests", VALID)], variants=("editor", "ios"))
        self.assertFalse(failed, errors)
        self.assertEqual([], errors)


if __name__ == "__main__":
    unittest.main()
