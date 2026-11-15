#!/usr/bin/env python3
"""Tests of check_unity_modules.py (#878): engine types in Runtime/ code map to the built-in module
packages package.json must declare; comments, strings and the vendored BouncyCastle library do not
count, and the repository's own package.json passes."""
import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_repo  # noqa: E402
import check_unity_modules  # noqa: E402

AUDIO_HANDLER = """using UnityEngine;
using UnityEngine.Networking;
class H {
    AudioClip Decode(string path) {
        using (var r = UnityWebRequestMultimedia.GetAudioClip(path, AudioType.WAV))
            return DownloadHandlerAudioClip.GetContent(r);
    }
}
"""


class ModulesUsedByTests(unittest.TestCase):
    def test_engine_types_map_to_their_modules(self):
        self.assertEqual(
            {"com.unity.modules.audio", "com.unity.modules.unitywebrequest",
             "com.unity.modules.unitywebrequestaudio"},
            check_unity_modules.modules_used_by(AUDIO_HANDLER))
        self.assertEqual({"com.unity.modules.imageconversion"},
                         check_unity_modules.modules_used_by("class T { void M(Texture2D t, byte[] b) { t.LoadImage(b); } }"))
        self.assertEqual({"com.unity.modules.androidjni"},
                         check_unity_modules.modules_used_by("class A { void M() { AndroidJNI.AttachCurrentThread(); } }"))
        self.assertEqual({"com.unity.modules.jsonserialize"},
                         check_unity_modules.modules_used_by("class J { string M(object o) => JsonUtility.ToJson(o); }"))

    def test_comments_and_strings_do_not_count(self):
        self.assertEqual(set(), check_unity_modules.modules_used_by(
            "/// Wraps <c>JsonUtility</c>, like AudioClip handling.\n"
            "class C { /* UnityWebRequest */ string s = \"AndroidJNI\"; }\n"))

    def test_core_engine_types_need_no_declaration(self):
        self.assertEqual(set(), check_unity_modules.modules_used_by(
            "class C : MonoBehaviour { Texture2D t; void M() { Debug.Log(Application.platform); } }"))


class CheckUnityModulesTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        os.makedirs(os.path.join(self.root, "Runtime", "Unity"))
        os.makedirs(os.path.join(self.root, "Runtime", "Transport", "BouncyCastle", "Lib"))
        self.files = ["Runtime/Unity/AudioClipHandler.cs", "Runtime/Transport/BouncyCastle/Lib/Vendored.cs"]
        self.write(self.files[0], AUDIO_HANDLER)
        self.write(self.files[1], "class V { void M() { JsonUtility.ToJson(this); } }")

    def tearDown(self):
        shutil.rmtree(self.root)

    def write(self, rel, text):
        with open(os.path.join(self.root, rel), "w", encoding="utf-8") as fh:
            fh.write(text)

    def package(self, dependencies):
        self.write("package.json", json.dumps({"name": "com.example.pkg", "dependencies": dependencies}))

    def test_missing_module_fails(self):
        # The state before #878: "dependencies": {}.
        self.package({})
        errors = check_unity_modules.check_unity_modules(self.root, self.files)
        self.assertEqual(3, len(errors), errors)
        self.assertTrue(any("com.unity.modules.unitywebrequestaudio" in e for e in errors), errors)
        self.assertFalse(any("jsonserialize" in e for e in errors), "the vendored library is not scanned")

    def test_declared_modules_pass(self):
        self.package({"com.unity.modules.audio": "1.0.0", "com.unity.modules.unitywebrequest": "1.0.0",
                      "com.unity.modules.unitywebrequestaudio": "1.0.0"})
        self.assertEqual([], check_unity_modules.check_unity_modules(self.root, self.files))

    def test_wrong_module_version_fails(self):
        self.package({"com.unity.modules.audio": "2.0.0", "com.unity.modules.unitywebrequest": "1.0.0",
                      "com.unity.modules.unitywebrequestaudio": "1.0.0"})
        errors = check_unity_modules.check_unity_modules(self.root, self.files)
        self.assertEqual(1, len(errors), errors)
        self.assertIn("1.0.0", errors[0])

    def test_repository_package_json_declares_every_used_module(self):
        self.assertEqual([], check_unity_modules.check_unity_modules(check_repo.ROOT, check_repo.tracked()))


if __name__ == "__main__":
    unittest.main()
