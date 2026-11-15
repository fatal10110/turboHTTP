#!/usr/bin/env python3
"""Built-in engine modules the package uses must be declared in package.json (#878).

Unity's built-in modules (Audio, UnityWebRequest, ImageConversion, AndroidJNI, JSONSerialize, ...)
are packages a project can disable in the Package Manager. A UPM package that uses one without
listing its com.unity.modules.* id under "dependencies" compiles only by accident, and fails with
missing-type errors in a project that disabled the module. This check scans the code (comments and
string literals blanked) of every tracked Runtime/ C# file except the vendored BouncyCastle library
for the types each module provides, and fails when package.json does not declare a module that is
used, or declares a com.unity.modules.* entry with a version other than "1.0.0" (the only version
built-in modules have).

Kept apart from check_repo.py so the rule can evolve without touching the shared invariants script;
check.sh runs it next to check_repo.py, and test_unity_modules.py is its fixture test.
"""
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_repo  # noqa: E402

MODULE_VERSION = "1.0.0"

# Module id -> the engine types (or member calls) only that module provides. Matched on code with
# comments and strings blanked, as whole identifiers.
MODULE_PATTERNS = {
    "com.unity.modules.unitywebrequest": re.compile(
        r"\bUnityEngine\.Networking\b|\bUnityWebRequest\b|\bDownloadHandler(?:Buffer|File|Script)?\b"
        r"|\bUploadHandler\w*|\bCertificateHandler\b"),
    "com.unity.modules.unitywebrequestaudio": re.compile(
        r"\bUnityWebRequestMultimedia\b|\bDownloadHandlerAudioClip\b"),
    "com.unity.modules.unitywebrequesttexture": re.compile(
        r"\bUnityWebRequestTexture\b|\bDownloadHandlerTexture\b"),
    "com.unity.modules.unitywebrequestassetbundle": re.compile(
        r"\bUnityWebRequestAssetBundle\b|\bDownloadHandlerAssetBundle\b"),
    "com.unity.modules.audio": re.compile(
        r"\bAudioClip\b|\bAudioType\b|\bAudioSource\b|\bAudioSettings\b|\bAudioListener\b"
        r"|\bAudioDataLoadState\b|\bAudioMixer\w*"),
    "com.unity.modules.imageconversion": re.compile(
        r"\bImageConversion\b|\.LoadImage\s*\(|\.EncodeTo(?:PNG|JPG|TGA|EXR)\s*\("),
    "com.unity.modules.androidjni": re.compile(
        r"\bAndroidJNI(?:Helper)?\b|\bAndroidJava(?:Object|Class|Proxy|Runnable|Exception)\b"),
    "com.unity.modules.jsonserialize": re.compile(r"\bJsonUtility\b"),
    "com.unity.modules.assetbundle": re.compile(r"\bAssetBundle(?:CreateRequest|Request)?\b"),
    "com.unity.modules.physics": re.compile(r"\bPhysics\b|\bRigidbody\b|\bRaycastHit\b"),
    "com.unity.modules.video": re.compile(r"\bVideoPlayer\b|\bVideoClip\b"),
}


def scanned(path):
    return path.startswith("Runtime/") and path.endswith(".cs") and \
        not path.startswith("Runtime/Transport/BouncyCastle/Lib/")


def modules_used_by(src):
    """Module ids whose types appear in the code of one C# source text."""
    code = check_repo._blank_cs_literals(src)
    return {module for module, pattern in MODULE_PATTERNS.items() if pattern.search(code)}


def check_unity_modules(root, files):
    try:
        with open(os.path.join(root, "package.json"), encoding="utf-8-sig") as fh:
            declared = json.load(fh).get("dependencies") or {}
    except (OSError, ValueError) as e:
        return [f"unitymodules: cannot read package.json: {e}"]

    errors = []
    for module, version in sorted(declared.items()):
        if module.startswith("com.unity.modules.") and version != MODULE_VERSION:
            errors.append(f"unitymodules: package.json declares {module} {version}; built-in modules are {MODULE_VERSION}")

    users = {}
    for f in files:
        if not scanned(f):
            continue
        with open(os.path.join(root, f), encoding="utf-8-sig") as fh:
            for module in modules_used_by(fh.read()):
                users.setdefault(module, []).append(f)
    for module in sorted(users):
        if module not in declared:
            more = f" (and {len(users[module]) - 1} more)" if len(users[module]) > 1 else ""
            errors.append(f"unitymodules: {users[module][0]}{more} uses {module}, "
                          f"which package.json does not declare under \"dependencies\" (#878)")
    return errors


def main():
    errors = check_unity_modules(check_repo.ROOT, check_repo.tracked())
    for e in errors:
        print(e)
    print(f"check_unity_modules: {len(errors)} problem(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
