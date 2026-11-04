#!/usr/bin/env python3
"""The UniTask pin for the gates that compile the UniTask adapters (#311).

TurboHTTP.UniTask, TurboHTTP.UniTask.WebSocket and TurboHTTP.Tests.UniTask compile only when the
customer project has com.cysharp.unitask. Two gates provide it, both from the one pin below:

  * the shadow build (shadow_build.py, so check.sh and CI) fetches the pinned commit's source with
    git, checks the commit id, and compiles UniTask's runtime asmdef as an external assembly;
  * Tools~/unity/run-tests.sh --with-unitask adds the git URL to the throwaway test project's
    manifest and fails when Unity's packages-lock.json resolved any other commit;
    --with-unitask=local installs the same commit from the checkout fetch() verified, for an editor
    host without git (Unity resolves git packages only with git on the host's PATH).

Bump: change TAG and SHA together (the SHA is the commit the tag points at:
`git ls-remote https://github.com/Cysharp/UniTask.git refs/tags/<tag>`), then run check.sh and the
host-editor gate (Development~/docs/agents/tooling.md, "UniTask pin"). UniTask source is never
committed to this repository; it is fetched into .tools/unitask/ (git-ignored).

CLI (used by run-tests.sh):
  unitask_pin.py url                        manifest value for com.cysharp.unitask
  unitask_pin.py sha                        the pinned commit
  unitask_pin.py check-lock <packages-lock.json> [<file: manifest value of the local copy>]
  unitask_pin.py check-results <results.xml>
  unitask_pin.py fetch                      fetch/verify the source, print the package directory
"""
import json
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

PACKAGE = "com.cysharp.unitask"
REPO = "https://github.com/Cysharp/UniTask.git"
PACKAGE_PATH = "src/UniTask/Assets/Plugins/UniTask"
# The pin: a release tag and the commit it resolves to. Change both together.
TAG = "2.5.11"
SHA = "2e993ff18f28c931602a07292df0b0804eebef99"
# The NUnit namespace of Tests/Runtime/UniTask (rootNamespace of TurboHTTP.Tests.UniTask).
TEST_NAMESPACE = "TurboHTTP.Tests.UniTaskAdapter."

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CACHE = os.path.join(ROOT, ".tools", "unitask")
SHA_RE = re.compile(r"^[0-9a-f]{40}$")


class PinError(Exception):
    pass


def manifest_url(tag=TAG):
    return f"{REPO}?path={PACKAGE_PATH}#{tag}"


def _same_file_url(a, b):
    norm = lambda v: str(v).replace("\\", "/").rstrip("/").casefold()
    return norm(a) == norm(b)


def check_lock(path, sha=SHA, tag=TAG, local=None):
    """Raises PinError unless packages-lock.json resolved com.cysharp.unitask to the pinned commit.

    local: the "file:" manifest value of a copy of fetch()'s verified checkout (--with-unitask=local);
    the lock must then name that copy (source "local") instead of the git URL.
    """
    try:
        with open(path, encoding="utf-8-sig") as fh:
            lock = json.load(fh)
    except (OSError, ValueError) as e:
        raise PinError(f"cannot read {path}: {e}") from e
    entry = (lock.get("dependencies") or {}).get(PACKAGE)
    if not isinstance(entry, dict):
        raise PinError(f"{path}: no {PACKAGE} entry (did the package resolve?)")
    if local is not None:
        if entry.get("source") != "local" or not _same_file_url(entry.get("version"), local):
            raise PinError(f"{path}: {PACKAGE} is {entry.get('source')!r} {entry.get('version')!r}, "
                           f"expected the local copy {local!r} of {sha}")
        return entry
    if entry.get("source") != "git":
        raise PinError(f"{path}: {PACKAGE} source is {entry.get('source')!r}, expected 'git'")
    if entry.get("version") != manifest_url(tag):
        raise PinError(f"{path}: {PACKAGE} version is {entry.get('version')!r}, expected {manifest_url(tag)!r}")
    if entry.get("hash") != sha:
        raise PinError(f"{path}: {PACKAGE} resolved commit {entry.get('hash')!r}, pinned {sha} ({tag}); "
                       "the tag moved or the pin is stale (tooling.md, 'UniTask pin')")
    return entry


def adapter_cases(path):
    """(executed, total) test cases of the UniTask adapter fixtures in an NUnit3 results file."""
    root = ET.parse(path).getroot()
    cases = [c for c in root.iter("test-case") if (c.get("fullname") or "").startswith(TEST_NAMESPACE)]
    executed = [c for c in cases if c.get("result") in ("Passed", "Failed")]
    return len(executed), len(cases)


def _git(args, cwd=None):
    proc = subprocess.run(["git"] + args, cwd=cwd, capture_output=True, text=True)
    if proc.returncode != 0:
        raise PinError(f"git {' '.join(args)}: {(proc.stderr or proc.stdout).strip()[-500:]}")
    return proc.stdout.strip()


def fetch(cache=CACHE, repo=REPO, sha=SHA, tag=TAG, check_tag=True):
    """Returns the UniTask package directory of the pinned commit, fetching it once into cache/<sha>.

    git checks every object id it receives, so HEAD == sha means the files are the pinned commit's.
    The tag is checked against the SHA on the first fetch, so the Unity gate (which installs by tag)
    and the shadow build (by SHA) cannot silently compile different code.
    """
    if not SHA_RE.match(sha):
        raise PinError(f"pinned SHA {sha!r} is not a 40-hex commit id")
    dest = os.path.join(cache, sha)
    marker = os.path.join(dest, ".turbohttp-verified")
    if os.path.isfile(marker) and _git(["rev-parse", "HEAD"], cwd=dest) == sha:
        return os.path.join(dest, PACKAGE_PATH)
    if check_tag:
        refs = _git(["ls-remote", repo, f"refs/tags/{tag}", f"refs/tags/{tag}^{{}}"])
        ids = {ref: oid for oid, ref in (line.split("\t", 1) for line in refs.splitlines() if "\t" in line)}
        tagged = ids.get(f"refs/tags/{tag}^{{}}") or ids.get(f"refs/tags/{tag}")
        if tagged != sha:
            raise PinError(f"{repo} tag {tag} is {tagged or 'missing'}, pinned {sha}; "
                           "change TAG and SHA together (tooling.md, 'UniTask pin')")
    tmp = dest + ".tmp"
    shutil.rmtree(tmp, ignore_errors=True)
    os.makedirs(tmp)
    _git(["init", "-q"], cwd=tmp)
    _git(["remote", "add", "origin", repo], cwd=tmp)
    _git(["sparse-checkout", "set", PACKAGE_PATH], cwd=tmp)
    _git(["fetch", "-q", "--depth", "1", "--filter=blob:none", "origin", sha], cwd=tmp)
    if _git(["rev-parse", "FETCH_HEAD"], cwd=tmp) != sha:
        raise PinError(f"{repo}: fetched commit is not {sha}")
    _git(["-c", "advice.detachedHead=false", "checkout", "-q", "FETCH_HEAD"], cwd=tmp)
    if _git(["rev-parse", "HEAD"], cwd=tmp) != sha:
        raise PinError(f"{repo}: checked-out commit is not {sha}")
    if not os.path.isdir(os.path.join(tmp, PACKAGE_PATH)):
        raise PinError(f"{repo}@{sha}: no {PACKAGE_PATH}")
    with open(os.path.join(tmp, ".turbohttp-verified"), "w") as fh:
        fh.write(f"{tag} {sha}\n")
    shutil.rmtree(dest, ignore_errors=True)
    os.rename(tmp, dest)
    return os.path.join(dest, PACKAGE_PATH)


def main(argv):
    if len(argv) == 2 and argv[1] in ("url", "sha"):
        print(manifest_url() if argv[1] == "url" else SHA)
        return 0
    try:
        if len(argv) in (3, 4) and argv[1] == "check-lock":
            local = argv[3] if len(argv) == 4 else None
            entry = check_lock(argv[2], local=local)
            print(f"unitask_pin: {PACKAGE} {TAG} resolved to "
                  f"{entry['hash'] if local is None else 'the verified local copy of ' + SHA}")
            return 0
        if len(argv) == 3 and argv[1] == "check-results":
            executed, total = adapter_cases(argv[2])
            print(f"unitask_pin: {executed} of {total} UniTask adapter test(s) ran")
            if executed == 0:
                print(f"unitask_pin: no {TEST_NAMESPACE}* test ran; the UniTask tests are PlayMode tests "
                      "(testing.md, 'Running Unity tests')", file=sys.stderr)
                return 4
            return 0
        if len(argv) == 2 and argv[1] == "fetch":
            print(fetch())
            return 0
    except PinError as e:
        print(f"unitask_pin: {e}", file=sys.stderr)
        return 2
    print(__doc__.strip().split("CLI (used by run-tests.sh):", 1)[1], file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
