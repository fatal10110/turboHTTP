#!/usr/bin/env python3
"""Print the CHANGELOG.md section of a release tag, after checking the tag against package.json.

Used by .github/workflows/release.yml (#101) and by the release checklist
(Development~/docs/release-checklist.md) before a tag is pushed:

    python3 'Tools~/ci/release_notes.py' v1.0.0-pre.1 > notes.md

Fails (exit 1, reason on stderr) when the tag is not `v<semver>`, when it differs from the
`version` in package.json, or when CHANGELOG.md has no non-empty `## [<version>]` section.
"""
import argparse
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# SemVer 2.0.0 core plus an optional pre-release; build metadata is not used for tags.
TAG_RE = re.compile(r"^v(\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?)$")
SECTION_RE = re.compile(r"^## \[([^\]]+)\]")
LINK_REF_RE = re.compile(r"^\[[^\]]+\]:\s")


class ReleaseError(Exception):
    pass


def version_from_tag(tag):
    m = TAG_RE.match(tag)
    if not m:
        raise ReleaseError(f"tag {tag!r} is not v<major>.<minor>.<patch>[-<pre-release>]")
    return m.group(1)


def changelog_section(text, version):
    """Body of the `## [version]` section, up to the next `## ` heading or the link references."""
    lines = text.splitlines()
    start = None
    for i, line in enumerate(lines):
        m = SECTION_RE.match(line)
        if m and m.group(1) == version:
            start = i + 1
            break
    if start is None:
        raise ReleaseError(f"CHANGELOG.md has no '## [{version}]' section")
    body = []
    for line in lines[start:]:
        if line.startswith("## ") or LINK_REF_RE.match(line):
            break
        body.append(line)
    notes = "\n".join(body).strip()
    if not notes:
        raise ReleaseError(f"CHANGELOG.md section '## [{version}]' is empty")
    return notes + "\n"


def release_notes(tag, root=ROOT):
    version = version_from_tag(tag)
    with open(os.path.join(root, "package.json"), encoding="utf-8") as f:
        package_version = json.load(f).get("version")
    if package_version != version:
        raise ReleaseError(f"tag {tag} does not match package.json version {package_version!r}")
    with open(os.path.join(root, "CHANGELOG.md"), encoding="utf-8") as f:
        return changelog_section(f.read(), version)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("tag", help="release tag, e.g. v1.0.0-pre.1")
    parser.add_argument("--root", default=ROOT, help="package root (default: this repository)")
    args = parser.parse_args(argv)
    try:
        sys.stdout.write(release_notes(args.tag, args.root))
    except (ReleaseError, OSError, ValueError) as e:
        print(f"release_notes: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
