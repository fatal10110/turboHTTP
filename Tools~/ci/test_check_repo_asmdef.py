#!/usr/bin/env python3
"""Tests for the asmdef settings and native plugin gate invariants of check_repo.py (#877).

PR CI has no Unity license, so Tests/Editor/ModuleAutoReferenceTests.cs and
BackgroundPluginPackagingTests.cs never run there; check_repo.py mirrors them. Each test copies the
repository's asmdefs, plugins, the plugin gate and the managed native callers into a scratch root,
breaks one setting and expects check_asmdef / check_native to report it. Before #877, dropping the
BouncyCastle defineConstraints, flipping autoReferenced or listing a plugin outside the gate passed.
"""
import importlib.util
import json
import os
import re
import shutil
import tempfile
import unittest
import warnings

HERE = os.path.dirname(os.path.abspath(__file__))
BOUNCY = "Runtime/Transport/BouncyCastle/TurboHTTP.Transport.BouncyCastle.asmdef"
SECURE_PROTOCOL = "Runtime/Transport/BouncyCastle/Lib/TurboHTTP.SecureProtocol.asmdef"
COMPLETE = "Runtime/TurboHTTP.Complete.asmdef"
TESTS_RUNTIME = "Tests/Runtime/TurboHTTP.Tests.Runtime.asmdef"
UNITASK = "Runtime/UniTask/TurboHTTP.UniTask.asmdef"
JSON_ASMDEF = "Runtime/JSON/TurboHTTP.JSON.asmdef"
TESTING = "Runtime/Testing/TurboHTTP.Testing.asmdef"
ANDROIDLIB = "Plugins/Android/TurboHttpBackground.androidlib"
IOS_BRIDGE = "Plugins/iOS/TurboHttpBackgroundTaskBridge.mm"
IOS_TRUST = "Plugins/iOS/TurboHttpSecTrustVerifier.mm"
GATE = "Editor/TurboHttpBackgroundPluginGate.cs"


def read(path, encoding="utf-8"):
    with open(path, encoding=encoding, errors="replace") as f:
        return f.read()


def load_check_repo():
    spec = importlib.util.spec_from_file_location("check_repo", os.path.join(HERE, "check_repo.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class Fixture(unittest.TestCase):
    def setUp(self):
        # check_repo.py reads with bare open() calls, as a one-shot script may; keep the test output readable.
        warnings.simplefilter("ignore", ResourceWarning)
        self.check = load_check_repo()
        real_root = self.check.ROOT
        wanted = []
        for f in self.check.tracked():
            if f.endswith(".asmdef") or f.startswith("Plugins/") or f == GATE:
                wanted.append(f)
            elif f.startswith("Runtime/") and f.endswith(".cs") and not f.startswith("Runtime/Transport/BouncyCastle/Lib/"):
                text = read(os.path.join(real_root, f))
                if '"__Internal"' in text or any(f == pair[2] for pair in self.check.RESULT_CODE_PAIRS):
                    wanted.append(f)
        self.tmp = tempfile.mkdtemp(prefix="check-repo-asmdef-")
        self.addCleanup(shutil.rmtree, self.tmp, True)
        for f in wanted:
            os.makedirs(os.path.dirname(os.path.join(self.tmp, f)), exist_ok=True)
            shutil.copyfile(os.path.join(real_root, f), os.path.join(self.tmp, f))
        self.files = wanted
        self.check.ROOT = self.tmp

    def edit_asmdef(self, path, **changes):
        full = os.path.join(self.tmp, path)
        data = json.loads(read(full, "utf-8-sig"))
        for k, v in changes.items():
            if v is None:
                data.pop(k, None)
            else:
                data[k] = v
        with open(full, "w", encoding="utf-8") as f:
            json.dump(data, f)

    def edit_text(self, path, old, new):
        full = os.path.join(self.tmp, path)
        text = read(full)
        self.assertIn(old, text, path)
        with open(full, "w", encoding="utf-8") as f:
            f.write(text.replace(old, new, 1))

    def asmdef_errors(self):
        return self.check.check_asmdef(self.files)

    def native_errors(self):
        return self.check.check_native(self.files)

    def assertReported(self, errors, *needles):
        self.assertTrue([e for e in errors if all(n in e for n in needles)], f"expected {needles} in {errors}")


class AsmdefSettingsTests(Fixture):
    def test_repository_asmdefs_pass(self):
        self.assertEqual([], self.asmdef_errors())

    def test_bouncycastle_losing_its_define_constraint_fails(self):
        # Without TURBOHTTP_BOUNCYCASTLE the glue and the vendored library would compile into every project (#196).
        self.edit_asmdef(BOUNCY, defineConstraints=[])
        self.assertReported(self.asmdef_errors(), "TurboHTTP.Transport.BouncyCastle", "defineConstraints")

    def test_vendored_library_losing_its_define_constraint_fails(self):
        # Unconstrained, TurboHTTP.SecureProtocol compiles in every project and a Mono player at Managed
        # Stripping Level Disabled copies it to Managed/ although nothing can call it (#870).
        self.edit_asmdef(SECURE_PROTOCOL, defineConstraints=[])
        self.assertReported(self.asmdef_errors(), "TurboHTTP.SecureProtocol", "defineConstraints")

    def test_auto_referenced_flips_fail(self):
        self.edit_asmdef(JSON_ASMDEF, autoReferenced=False)
        self.edit_asmdef(TESTING, autoReferenced=None)  # Unity's default is true
        errors = self.asmdef_errors()
        self.assertReported(errors, "TurboHTTP.JSON", "autoReferenced: true")
        self.assertReported(errors, "TurboHTTP.Testing", "autoReferenced: false")

    def test_platform_excludes_are_pinned(self):
        self.edit_asmdef("Runtime/Transport/TurboHTTP.Transport.asmdef", excludePlatforms=[])
        self.assertReported(self.asmdef_errors(), "TurboHTTP.Transport ", "excludePlatforms ['WebGL']")

    def test_reference_to_an_excluded_platform_assembly_fails(self):
        # TurboHTTP.Complete references Transport (no WebGL); both keep the exclusion. A new assembly that
        # references Transport without excluding WebGL is reported even before it gets a table entry.
        self.check.ASMDEF_EXPECTED["TurboHTTP.Complete"] = (True, [], [], [])
        self.edit_asmdef(COMPLETE, excludePlatforms=[])
        self.assertReported(self.asmdef_errors(), "TurboHTTP.Complete references TurboHTTP.Transport", "WebGL")

    def test_complete_references_are_exact(self):
        refs = json.loads(read(os.path.join(self.tmp, COMPLETE), "utf-8-sig"))["references"]
        self.edit_asmdef(COMPLETE, references=refs + ["TurboHTTP.Transport.BouncyCastle"])
        self.assertReported(self.asmdef_errors(), "TurboHTTP.Complete must reference exactly",
                            "extra ['TurboHTTP.Transport.BouncyCastle']")
        self.edit_asmdef(COMPLETE, references=[r for r in refs if r != "TurboHTTP.Testing"])
        self.assertReported(self.asmdef_errors(), "TurboHTTP.Complete must reference exactly", "missing ['TurboHTTP.Testing']")

    def test_every_tests_asmdef_needs_unity_include_tests(self):
        self.edit_asmdef(TESTS_RUNTIME, defineConstraints=[])
        self.assertReported(self.asmdef_errors(), TESTS_RUNTIME, "UNITY_INCLUDE_TESTS")
        # Also for a test assembly the table does not know yet.
        os.makedirs(os.path.join(self.tmp, "Tests", "Extra"))
        with open(os.path.join(self.tmp, "Tests", "Extra", "TurboHTTP.Tests.Extra.asmdef"), "w") as f:
            json.dump({"name": "TurboHTTP.Tests.Extra", "references": ["TurboHTTP.Core"]}, f)
        self.files.append("Tests/Extra/TurboHTTP.Tests.Extra.asmdef")
        errors = self.asmdef_errors()
        self.assertReported(errors, "Tests/Extra/TurboHTTP.Tests.Extra.asmdef", "UNITY_INCLUDE_TESTS")
        self.assertReported(errors, "TurboHTTP.Tests.Extra", "no ASMDEF_EXPECTED entry")

    def test_unitask_constraint_needs_its_version_define(self):
        self.edit_asmdef(UNITASK, versionDefines=[])
        self.assertReported(self.asmdef_errors(), "TurboHTTP.UniTask ", "versionDefines", "com.cysharp.unitask")


class PluginGateTests(Fixture):
    def test_repository_plugins_pass(self):
        self.assertEqual([], self.native_errors())

    def test_gate_list_is_parsed_from_the_editor_gate(self):
        define, gated = self.check.plugin_gate(self.files)
        self.assertEqual("TURBOHTTP_BACKGROUND_NETWORKING", define)
        self.assertEqual([ANDROIDLIB, IOS_BRIDGE], gated)

    def test_constrained_androidlib_outside_the_gate_fails(self):
        # The .androidlib folder is a plugin: its importer constraint alone did not keep it out of a build.
        self.edit_text(GATE, f'"{ANDROIDLIB}",', "")
        self.assertReported(self.native_errors(), ANDROIDLIB, "add it to GatedPluginPaths")

    def test_constrained_ios_plugin_outside_the_gate_fails(self):
        self.edit_text(IOS_TRUST + ".meta", "defineConstraints: []", "defineConstraints:\n  - TURBOHTTP_BACKGROUND_NETWORKING")
        self.assertReported(self.native_errors(), IOS_TRUST, "add it to GatedPluginPaths")

    def test_gated_path_must_be_a_plugin_with_the_gate_define(self):
        self.edit_text(GATE, f'"{IOS_BRIDGE}",', f'"{IOS_BRIDGE}",\n            "Plugins/iOS/Missing.mm",')
        self.edit_text(ANDROIDLIB + ".meta", "  - TURBOHTTP_BACKGROUND_NETWORKING", "  - SOMETHING_ELSE")
        errors = self.native_errors()
        self.assertReported(errors, "Plugins/iOS/Missing.mm", "not a native plugin")
        self.assertReported(errors, ANDROIDLIB, "must be exactly [TURBOHTTP_BACKGROUND_NETWORKING]")

    def test_unparsable_gate_fails(self):
        self.edit_text(GATE, "GatedPluginPaths", "PluginPaths")
        self.assertReported(self.native_errors(), "cannot parse")


class SamplesListedTests(Fixture):
    def test_sample_asmdef_missing_from_package_json_fails(self):
        shutil.copyfile(os.path.join(HERE, "..", "..", "package.json"), os.path.join(self.tmp, "package.json"))
        os.makedirs(os.path.join(self.tmp, "Samples~", "99-Unlisted"))
        path = "Samples~/99-Unlisted/TurboHTTP.Samples.Unlisted.asmdef"
        with open(os.path.join(self.tmp, path), "w") as f:
            json.dump({"name": "TurboHTTP.Samples.Unlisted", "references": ["TurboHTTP.Core"]}, f)
        self.files.append(path)
        # check_docs also scans README/Documentation~ through doc_snippets, which keeps the real ROOT.
        errors = [e for e in self.check.check_docs([f for f in self.files if f.startswith("Samples~/")])
                  if re.search(r"Samples~/99-Unlisted", e)]
        self.assertReported(errors, "Samples~/99-Unlisted", "package.json does not list it")


if __name__ == "__main__":
    unittest.main()
