#!/usr/bin/env python3
"""Write a .meta for each given asset that lacks one (new random GUID).

Unity generates .meta files when it imports a file; use this only when adding files
without opening Unity (agents, CI). Supports folders and .cs/.md/.json/.txt files;
anything else (plugins, textures, asmdefs) should get its .meta from Unity.

Usage: Tools~/ci/gen_meta.py <path> [<path> ...]
"""
import os
import sys
import uuid

FOLDER = "fileFormatVersion: 2\nguid: {guid}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
MONO = ("fileFormatVersion: 2\nguid: {guid}\nMonoImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n"
        "  defaultReferences: []\n  executionOrder: 0\n  icon: {{instanceID: 0}}\n  userData: \n"
        "  assetBundleName: \n  assetBundleVariant: \n")
TEXT = "fileFormatVersion: 2\nguid: {guid}\nTextScriptImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
TEMPLATES = {".cs": MONO, ".md": TEXT, ".json": TEXT, ".txt": TEXT}


def main(paths):
    status = 0
    for p in paths:
        p = p.rstrip("/")
        if os.path.exists(p + ".meta"):
            continue
        if os.path.isdir(p):
            template = FOLDER
        else:
            template = TEMPLATES.get(os.path.splitext(p)[1])
        if template is None:
            print(f"gen_meta: {p}: unsupported type; let Unity generate this .meta", file=sys.stderr)
            status = 1
            continue
        with open(p + ".meta", "w") as f:
            f.write(template.format(guid=uuid.uuid4().hex))
        print(p + ".meta")
    return status


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
