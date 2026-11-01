#!/usr/bin/env python3
"""Repository invariants that Unity, the asmdef graph or packaging rely on.

Checks (all on git-tracked files):
  meta      every asset Unity imports has a sibling .meta; no orphan .meta; GUIDs unique
  asmdef    assembly dependency graph follows the module rules in Development/docs/agents/architecture.md
  json      every .json/.asmdef parses; package.json has the required UPM fields
  unitynunit  tests use only what Unity 2021.3's NUnit (com.unity.ext.nunit 1.0.6) provides; PR CI
            cannot compile the Unity test assemblies without a license, so this is the only guard
  ignores   every skipped test says why: Assert.Ignore/Assert.Inconclusive/[Ignore] carry an issue reference
            (#123) or the [env] marker (TestRequirements.IgnoreEnvironment adds it) in their message (#190)
  reflect   no runtime code names a TurboHTTP type by assembly-qualified string (a by-name lookup
            that managed stripping breaks); modules reach each other through registration APIs (#186)
  coreapi   the public types declared under Runtime/Core match Tools~/ci/api-baseline/core-public-types.txt
            and stay within the cap (#77): a new public Core type fails until the baseline lists it

Usage: Tools~/ci/check_repo.py [meta|asmdef|json|unitynunit|ignores|reflect|coreapi ...]   (default: all)
"""
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# Top-level entries Unity would import but that are not meant to ship. Remove an entry
# once the folder is renamed with a `~` suffix or moved out (issue REL-06).
META_EXEMPT_TOP = {"Development"}

# Allowed references per assembly (Development/docs/agents/architecture.md, "Assemblies and allowed references"). Anything not listed here may
# reference only TurboHTTP.Core. Expanding an entry needs a documented architecture decision.
ALLOWED_REFS = {
    "TurboHTTP.Core": set(),
    "TurboHTTP.Transport.BouncyCastle": {"TurboHTTP.Core", "TurboHTTP.Transport", "TurboHTTP.SecureProtocol"},
    "TurboHTTP.SecureProtocol": set(),  # vendored BouncyCastle (Runtime/Transport/BouncyCastle/Lib), self-contained
    "TurboHTTP.WebSocket.Transport": {"TurboHTTP.Core", "TurboHTTP.WebSocket", "TurboHTTP.Transport"},
    "TurboHTTP.Unity.WebSocket": {"TurboHTTP.Core", "TurboHTTP.WebSocket", "TurboHTTP.Unity"},
    "TurboHTTP.UniTask": {"TurboHTTP.Core", "TurboHTTP.WebSocket", "UniTask"},
    "TurboHTTP.Editor": {"TurboHTTP.Core", "TurboHTTP.Observability"},
    "TurboHTTP.Complete": None,  # aggregation assembly: any TurboHTTP.* runtime module
    "TurboHTTP.Tests.Runtime": None,
    "TurboHTTP.Tests.Editor": None,
}
UNSAFE_ALLOWED = {"TurboHTTP.Transport"}
ENGINE_FREE = {"TurboHTTP.Transport", "TurboHTTP.SecureProtocol", "TurboHTTP.JSON", "TurboHTTP.WebSocket", "TurboHTTP.WebSocket.Transport"}


def tracked():
    out = subprocess.run(["git", "ls-files", "-z"], cwd=ROOT, capture_output=True, check=True).stdout
    return [p for p in out.decode().split("\0") if p and os.path.exists(os.path.join(ROOT, p))]


def unity_imported(path):
    parts = path.split("/")
    if parts[0] in META_EXEMPT_TOP:
        return False
    return not any(p.startswith(".") or p.endswith("~") for p in parts)


def check_meta(files):
    errors = []
    files_set = set(files)
    assets = set()
    for f in files:
        if not unity_imported(f) or f.endswith(".meta"):
            continue
        assets.add(f)
        parts = f.split("/")
        for i in range(1, len(parts)):
            assets.add("/".join(parts[:i]))
    for a in sorted(assets):
        if a + ".meta" not in files_set:
            errors.append(f"meta: missing {a}.meta (commit the .meta Unity generated, or run Tools~/ci/gen_meta.py)")
    guids = {}
    for f in files:
        if not f.endswith(".meta") or not unity_imported(f):
            continue
        if f[:-5] not in assets:
            errors.append(f"meta: orphan {f} (asset removed or renamed without its .meta)")
        m = re.search(r"^guid: ([0-9a-f]{32})$", open(os.path.join(ROOT, f)).read(), re.M)
        if not m:
            errors.append(f"meta: {f} has no guid")
        elif m.group(1) in guids:
            errors.append(f"meta: duplicate guid {m.group(1)} in {f} and {guids[m.group(1)]}")
        else:
            guids[m.group(1)] = f
    return errors


def check_asmdef(files):
    errors = []
    for f in files:
        if not f.endswith(".asmdef"):
            continue
        a = json.load(open(os.path.join(ROOT, f), encoding="utf-8-sig"))
        name, refs = a["name"], a.get("references") or []
        for r in refs:
            if r.startswith("GUID:"):
                errors.append(f"asmdef: {f} uses a GUID reference ({r}); use the assembly name")
        allowed = ALLOWED_REFS.get(name, {"TurboHTTP.Core"})
        if allowed is not None:
            for r in refs:
                if r not in allowed and r not in {"UnityEngine.TestRunner", "UnityEditor.TestRunner"}:
                    errors.append(f"asmdef: {name} must not reference {r} (allowed: {sorted(allowed) or 'nothing'}; "
                                  "see Development/docs/agents/architecture.md)")
        if a.get("allowUnsafeCode") and name not in UNSAFE_ALLOWED:
            errors.append(f"asmdef: {name} enables allowUnsafeCode; only {sorted(UNSAFE_ALLOWED)} may")
        if name in ENGINE_FREE and not a.get("noEngineReferences"):
            errors.append(f"asmdef: {name} must keep noEngineReferences: true")
    return errors


def check_json(files):
    errors = []
    for f in files:
        if not f.endswith((".json", ".asmdef", ".asmref")):
            continue
        try:
            data = json.load(open(os.path.join(ROOT, f), encoding="utf-8-sig"))
        except ValueError as e:
            errors.append(f"json: {f}: {e}")
            continue
        if f == "package.json":
            for key in ("name", "version", "displayName", "unity", "description"):
                if not data.get(key):
                    errors.append(f"json: package.json is missing '{key}'")
            if not re.match(r"^[a-z0-9.-]+$", data.get("name", "")):
                errors.append("json: package.json name must be a lowercase reverse-domain id")
            for s in data.get("samples", []):
                if not os.path.isdir(os.path.join(ROOT, s.get("path", ""))):
                    errors.append(f"json: package.json sample path {s.get('path')} does not exist")
    return errors


# APIs missing from Unity 2021.3's NUnit (checked against com.unity.ext.nunit 1.0.6's nunit.framework.dll).
UNITY_NUNIT_MISSING = [
    # Any position in an attribute list: [NonParallelizable], [TestFixture, NonParallelizable], [...Attribute].
    (re.compile(r"\[[^\]]*?\b(?:[\w.]+\.)?NonParallelizable(?:Attribute)?\b"), "[NonParallelizable] (Unity runs tests serially; drop it)"),
    (re.compile(r"\bAssert\.(?:ThrowsAsync|CatchAsync|ThatAsync)\b"), "Assert.*Async (use AssertAsync.ThrowsAsync)"),
    (re.compile(r"\bAssert\.Multiple\b"), "Assert.Multiple"),
]
ASYNC_TASK_METHOD = re.compile(
    # Task, Task<T> (nested generics and tuples included) and ValueTask returns; generic methods too.
    r"(?:(?:public|internal|private|protected|static|virtual|override|new)\s+)*async\s+(?:Value)?Task(?:<[\w\s,.<>\[\]?()]*>)?\s+\w+\s*(?:<[^>(]*>)?\s*\(")
TEST_ATTRIBUTE = re.compile(r"\[(?:[^\]]*[,\s(])?(?:Test|TestCase|TestCaseSource)(?:Attribute)?\b")


def _async_task_tests(text):
    """Offsets of async Task methods carrying a test attribute: the attributes are the text between the
    previous member boundary (';', '{' or '}' outside the attribute brackets) and the signature."""
    for m in ASYNC_TASK_METHOD.finditer(text):
        i, depth = m.start() - 1, 0
        while i >= 0:
            ch = text[i]
            if ch == "]":
                depth += 1
            elif ch == "[":
                depth -= 1
            elif ch in ";{}" and depth <= 0:
                break
            i -= 1
        if TEST_ATTRIBUTE.search(text[i + 1:m.start()]):
            yield m.start()


def check_unity_nunit(files):
    errors = []
    for f in files:
        if not (f.startswith("Tests/") and f.endswith(".cs")):
            continue
        text = open(os.path.join(ROOT, f), encoding="utf-8-sig").read()
        line = lambda pos: text.count("\n", 0, pos) + 1
        for pattern, what in UNITY_NUNIT_MISSING:
            for m in pattern.finditer(text):
                errors.append(f"unitynunit: {f}:{line(m.start())}: {what} does not exist in Unity's NUnit")
        for pos in _async_task_tests(text):
            errors.append(f"unitynunit: {f}:{line(pos)}: async Task test methods do not run in "
                          "Unity's NUnit; write a void test that wraps the body in AssertAsync.Run")
    return errors


# Skipped tests must say why (#190): an issue reference ("#152"), the environment marker that
# TestRequirements.IgnoreEnvironment prefixes ("[env]"), or the constant that holds it.
_IGNORE_CALL = re.compile(r"\bAssert\.Inconclusive\s*\(|(?<!\w)(?:\w+\.)*Ignore(?:Attribute)?\s*\(|\bIgnore(?:Reason)?\s*=(?!=)")
_IGNORE_BARE_ATTRIBUTE = re.compile(r"\[\s*(?:[\w.]+\.)?Ignore(?:Attribute)?\s*\]")
_IGNORE_REASON = re.compile(r"#\d+|\[env\]|\bEnvironmentIgnorePrefix\b")


def _ignore_argument(text, pos):
    """The source text of the call/attribute argument starting at pos (after the opening paren, or after '=')."""
    i, depth, n = pos, 1, len(text)
    in_attr_assign = text[pos - 1] != "("
    while i < n:
        c = text[i]
        if c == '"':
            verbatim = i > 0 and (text[i - 1] == "@" or text[i - 2:i] in ("$@", "@$"))
            i += 1
            while i < n:
                if text[i] == '"':
                    if verbatim and i + 1 < n and text[i + 1] == '"':
                        i += 2
                        continue
                    break
                if text[i] == "\\" and not verbatim:
                    i += 1
                i += 1
        elif c == "'":
            i += 1
            while i < n and text[i] != "'":
                i += 2 if text[i] == "\\" else 1
        elif in_attr_assign and c in ",;]":
            return text[pos:i]
        elif c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0 and not in_attr_assign:
                return text[pos:i]
        i += 1
    return text[pos:]


def check_ignores(files):
    errors = []
    for f in files:
        if not (f.startswith("Tests/") and f.endswith(".cs")):
            continue
        with open(os.path.join(ROOT, f), encoding="utf-8-sig") as fh:
            text = fh.read()
        line = lambda pos: text.count("\n", 0, pos) + 1
        for m in _IGNORE_CALL.finditer(text):
            start = text.rfind("\n", 0, m.start()) + 1
            if text[start:m.start()].lstrip().startswith("//"):
                continue
            if not _IGNORE_REASON.search(_ignore_argument(text, m.end())):
                errors.append(f"ignores: {f}:{line(m.start())}: skipped test without an issue reference (#123) or the [env] "
                              "marker; use TestRequirements.IgnoreEnvironment for a missing capability (#190)")
        for m in _IGNORE_BARE_ATTRIBUTE.finditer(text):
            errors.append(f"ignores: {f}:{line(m.start())}: [Ignore] without a reason (#190)")
    return errors


# "TurboHTTP.Some.Type, TurboHTTP.Assembly": the string Type.GetType needs to cross an assembly.
_QUALIFIED_TURBO_TYPE = re.compile(r'"TurboHTTP\.[A-Za-z0-9_.+]+,\s*TurboHTTP[A-Za-z0-9_.]*')


def check_reflect(files):
    errors = []
    for f in files:
        if not (f.startswith("Runtime/") and f.endswith(".cs")) or f.startswith("Runtime/Transport/BouncyCastle/Lib/"):
            continue
        path = os.path.join(ROOT, f)
        if not os.path.exists(path):
            continue
        for n, line in enumerate(open(path, encoding="utf-8-sig"), 1):
            if _QUALIFIED_TURBO_TYPE.search(line):
                errors.append(f"reflect: {f}:{n} names a TurboHTTP type by assembly-qualified string; "
                              "use a registration API instead (#186)")
    return errors


CORE_API_BASELINE = "Tools~/ci/api-baseline/core-public-types.txt"
CORE_PUBLIC_TYPE_CAP = 60  # #77 acceptance criterion; raising it needs an architecture decision
_PUBLIC_TYPE = re.compile(
    r"^\s*public\s+(?:(?:static|sealed|abstract|readonly|partial|unsafe|new|ref)\s+)*"
    r"(class|struct|enum|interface|delegate|record)\s+(.*)$")
_NAMESPACE = re.compile(r"^\s*namespace\s+([\w.]+)")


def core_public_types(files):
    """`Namespace.Name` (generic types as `Name`N`) for every line in Runtime/Core/**/*.cs that declares a
    public type, top-level or nested, whatever #if guards it. Same rule as the baseline header."""
    found = []
    for path in files:
        if not (path.startswith("Runtime/Core/") and path.endswith(".cs")):
            continue
        namespace = ""
        with open(os.path.join(ROOT, path), encoding="utf-8-sig") as fh:
            for line in fh:
                ns = _NAMESPACE.match(line)
                if ns:
                    namespace = ns.group(1)
                    continue
                m = _PUBLIC_TYPE.match(line)
                if not m:
                    continue
                kind, rest = m.groups()
                if kind == "delegate":
                    name = re.search(r"(\w+)\s*(<[^>]*>)?\s*\(", rest)
                else:
                    name = re.match(r"(\w+)\s*(<[^>]*>)?", rest)
                if not name:
                    continue
                arity = name.group(2).count(",") + 1 if name.group(2) else 0
                found.append((f"{namespace}.{name.group(1)}" + (f"`{arity}" if arity else ""), path))
    return found


def check_core_api(files):
    errors = []
    declared = core_public_types(files)
    names = [n for n, _ in declared]
    baseline_path = os.path.join(ROOT, CORE_API_BASELINE)
    if not os.path.exists(baseline_path):
        return [f"coreapi: {CORE_API_BASELINE} is missing"]
    with open(baseline_path, encoding="utf-8") as fh:
        lines = [l.strip() for l in fh]
    listed = [l for l in lines if l and not l.startswith("#")]
    for dup in sorted({n for n in listed if listed.count(n) > 1}):
        errors.append(f"coreapi: {CORE_API_BASELINE} lists {dup} more than once")
    for name, path in declared:
        if name not in listed:
            errors.append(f"coreapi: {path}: public type {name} is not in {CORE_API_BASELINE}; make it internal, "
                          "or add it to the baseline in the same PR (new public Core API needs a PR mention)")
    for name in sorted(set(listed) - set(names)):
        errors.append(f"coreapi: {CORE_API_BASELINE} lists {name}, which Runtime/Core no longer declares public; "
                      "remove the line")
    count = [l for l in lines if l.startswith("# count:")]
    if not count or count[0].split(":", 1)[1].strip() != str(len(listed)):
        errors.append(f"coreapi: {CORE_API_BASELINE} needs a '# count: {len(listed)}' header matching its entries")
    if len(names) > CORE_PUBLIC_TYPE_CAP:
        errors.append(f"coreapi: Runtime/Core declares {len(names)} public types; the cap is {CORE_PUBLIC_TYPE_CAP} (#77)")
    return errors


def main():
    wanted = sys.argv[1:] or ["meta", "asmdef", "json", "unitynunit", "ignores", "reflect", "coreapi"]
    files = tracked()
    checks = {"meta": check_meta, "asmdef": check_asmdef, "json": check_json, "unitynunit": check_unity_nunit,
              "ignores": check_ignores,
              "reflect": check_reflect, "coreapi": check_core_api}
    errors = []
    for w in wanted:
        errors += checks[w](files)
    for e in errors:
        print(e)
    print(f"check_repo: {len(errors)} problem(s) in {', '.join(wanted)}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
