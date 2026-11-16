#!/usr/bin/env python3
"""Repository invariants that Unity, the asmdef graph or packaging rely on.

Checks (all on git-tracked files):
  meta      every asset Unity imports has a sibling .meta; no orphan .meta; GUIDs unique
  asmdef    assembly dependency graph follows the module rules in Development~/docs/agents/architecture.md;
            each assembly keeps its expected autoReferenced, defineConstraints and include/excludePlatforms
            (ASMDEF_EXPECTED), TurboHTTP.Complete references exactly COMPLETE_REFS, every Tests/ asmdef is
            constrained to UNITY_INCLUDE_TESTS, a TURBOHTTP_UNITASK constraint comes with its versionDefines
            entry, and an assembly builds on no platform an assembly it references excludes (#877)
  json      every .json/.asmdef parses; package.json has the required UPM fields
  unitynunit  tests use only what Unity 2021.3's NUnit (com.unity.ext.nunit 1.0.6) provides; PR CI
            cannot compile the Unity test assemblies without a license, so this is the only guard
  ignores   every skipped test says why: Assert.Ignore/Assert.Inconclusive/[Ignore] carry an issue reference
            (#123) or the [env] marker (TestRequirements.IgnoreEnvironment adds it) in their message (#190)
  reflect   no runtime code names a TurboHTTP type by assembly-qualified string (a by-name lookup
            that managed stripping breaks); modules reach each other through registration APIs (#186)
  coreapi   the public types declared under Runtime/Core match Tools~/ci/api-baseline/core-public-types.txt
            and stay within the cap (#77): a new public Core type fails until the baseline lists it
  docs      README.md and Documentation~ name no removed middleware API (*Middleware types, Middlewares,
            the request-level SendAsync()), their fenced csharp blocks parse (snippet directives), and every
            Samples~ folder ships an asmdef that package.json lists (#232, #98); the snippets and samples
            compile in the shadow build
  il2cppfilter  in a try statement with an exception filter (`catch ... when`), only the last catch clause
            throws: IL2CPP (Unity 2021.3) emits the clauses of such a try as nested C++ try blocks, so an
            exception thrown or rethrown by an earlier clause is caught by a later clause of the same try (#320).
            A clause whose body awaits is exempt (the compiler runs its body after the try)
  native    every DllImport("__Internal") resolves to a native plugin under Plugins/, and on each player
            platform the plugin importer (platforms + define constraints) includes the plugin exactly when
            the managed #if guard compiles its callers (#176): a caller without the plugin fails the
            Xcode link, a plugin without a caller ships dead native code. Unity 2021.3 ignores a native
            plugin's importer define constraints, so a plugin (.androidlib folders included) with one must
            be listed in GatedPluginPaths of Editor/TurboHttpBackgroundPluginGate.cs, which applies exactly
            its Define, and every listed path must be such a plugin (#877)

Usage: Tools~/ci/check_repo.py [meta|asmdef|json|unitynunit|ignores|reflect|il2cppfilter|coreapi|native|docs ...]   (default: all)
"""
import json
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import unitask_pin  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# Top-level entries Unity would import but that are not meant to ship. Empty since #6 renamed
# the internal docs folder to `Development~` (hidden from Unity like every `~` or dot folder).
META_EXEMPT_TOP = set()

# Allowed references per assembly (Development~/docs/agents/architecture.md, "Assemblies and allowed references"). Anything not listed here may
# reference only TurboHTTP.Core. Expanding an entry needs a documented architecture decision.
ALLOWED_REFS = {
    "TurboHTTP.Core": set(),
    "TurboHTTP.Transport.BouncyCastle": {"TurboHTTP.Core", "TurboHTTP.Transport", "TurboHTTP.SecureProtocol"},
    "TurboHTTP.SecureProtocol": set(),  # vendored BouncyCastle (Runtime/Transport/BouncyCastle/Lib), self-contained
    "TurboHTTP.WebSocket.Transport": {"TurboHTTP.Core", "TurboHTTP.WebSocket", "TurboHTTP.Transport"},
    "TurboHTTP.Unity.WebSocket": {"TurboHTTP.Core", "TurboHTTP.WebSocket", "TurboHTTP.Unity"},
    "TurboHTTP.UniTask": {"TurboHTTP.Core", "UniTask"},
    "TurboHTTP.UniTask.WebSocket": {"TurboHTTP.Core", "TurboHTTP.WebSocket", "UniTask"},  # split out of TurboHTTP.UniTask (#116)
    "TurboHTTP.Editor": {"TurboHTTP.Core", "TurboHTTP.Observability"},
    "TurboHTTP.Complete": None,  # aggregation assembly: exactly COMPLETE_REFS (checked separately)
    "TurboHTTP.Tests.Runtime": None,
    "TurboHTTP.Tests.Editor": None,
    "TurboHTTP.Tests.UniTask": None,  # compiled only with com.cysharp.unitask installed
}
# TurboHTTP.Complete's exact references (architecture.md, TurboHTTP.Complete row): every runtime module
# except the opt-in Transport.BouncyCastle, UniTask and UniTask.WebSocket (#877).
COMPLETE_REFS = {
    "TurboHTTP.Core", "TurboHTTP.Transport", "TurboHTTP.WebSocket", "TurboHTTP.WebSocket.Transport",
    "TurboHTTP.Unity.WebSocket", "TurboHTTP.Retry", "TurboHTTP.Cache", "TurboHTTP.Auth", "TurboHTTP.Observability",
    "TurboHTTP.Files", "TurboHTTP.Unity", "TurboHTTP.Testing", "TurboHTTP.RateLimit", "TurboHTTP.JSON",
    "TurboHTTP.Middleware",
}
# Who sees an assembly and when Unity compiles it (#877): name -> (autoReferenced, defineConstraints,
# includePlatforms, excludePlatforms). PR CI has no Unity, so this mirrors Tests/Editor/ModuleAutoReferenceTests.cs
# and BackgroundPluginPackagingTests.cs: user-facing modules are visible to Assembly-CSharp, the opt-in ones
# (Testing, BouncyCastle) stay explicit, BouncyCastle and the UniTask adapters compile only with their define,
# socket assemblies skip WebGL. Every Runtime/, Editor/ and Tests/ asmdef needs an entry (the vendored
# SecureProtocol asmdef included: its settings are ours to check, its code is not). Samples~ asmdefs are
# customer copies checked by the sample rules in check_asmdef and check_docs instead.
_WEBGL = ["WebGL"]
ASMDEF_EXPECTED = {
    "TurboHTTP.Core": (True, [], [], []),
    "TurboHTTP.Transport": (True, [], [], _WEBGL),
    "TurboHTTP.Transport.BouncyCastle": (False, ["TURBOHTTP_BOUNCYCASTLE"], [], _WEBGL),
    "TurboHTTP.SecureProtocol": (False, [], [], _WEBGL),
    "TurboHTTP.WebSocket": (True, [], [], []),
    "TurboHTTP.WebSocket.Transport": (True, [], [], _WEBGL),
    "TurboHTTP.Unity.WebSocket": (True, [], [], []),
    "TurboHTTP.Unity": (True, [], [], []),
    "TurboHTTP.UniTask": (True, ["TURBOHTTP_UNITASK"], [], []),
    "TurboHTTP.UniTask.WebSocket": (True, ["TURBOHTTP_UNITASK"], [], []),
    "TurboHTTP.Auth": (True, [], [], []),
    "TurboHTTP.Cache": (True, [], [], []),
    "TurboHTTP.Files": (True, [], [], []),
    "TurboHTTP.JSON": (True, [], [], []),
    "TurboHTTP.Middleware": (True, [], [], []),
    "TurboHTTP.Observability": (True, [], [], []),
    "TurboHTTP.RateLimit": (True, [], [], []),
    "TurboHTTP.Retry": (True, [], [], []),
    "TurboHTTP.Testing": (False, [], [], []),
    "TurboHTTP.Complete": (True, [], [], _WEBGL),
    "TurboHTTP.Editor": (False, [], ["Editor"], []),
    "TurboHTTP.Tests.Editor": (False, ["UNITY_INCLUDE_TESTS"], ["Editor"], []),
    "TurboHTTP.Tests.Runtime": (False, ["UNITY_INCLUDE_TESTS"], [], _WEBGL),
    "TurboHTTP.Tests.UniTask": (False, ["UNITY_INCLUDE_TESTS", "TURBOHTTP_UNITASK"], [], _WEBGL),
}
# A define constraint a package's versionDefines must supply, or the assembly never compiles.
VERSION_DEFINES = {"TURBOHTTP_UNITASK": ("com.cysharp.unitask", "2.0.0")}
UNSAFE_ALLOWED = {"TurboHTTP.Transport"}
ENGINE_FREE = {"TurboHTTP.Transport", "TurboHTTP.SecureProtocol", "TurboHTTP.JSON", "TurboHTTP.WebSocket", "TurboHTTP.WebSocket.Transport"}


def tracked():
    # cwd=ROOT locates the repository; the hook's GIT_INDEX_FILE (partial commit) stays so the staged tree is checked.
    env = unitask_pin.clean_git_env(keep=("GIT_INDEX_FILE",))
    out = subprocess.run(["git", "ls-files", "-z"], cwd=ROOT, env=env, capture_output=True, check=True).stdout
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


def asmdef_platforms(a):
    """(includePlatforms, excludePlatforms) as sets; Unity uses one or the other."""
    return set(a.get("includePlatforms") or []), set(a.get("excludePlatforms") or [])


def check_asmdef_settings(f, a):
    """autoReferenced, defineConstraints, platforms and versionDefines of one non-sample asmdef (#877)."""
    errors = []
    name = a["name"]
    constraints = a.get("defineConstraints") or []
    if f.startswith("Tests/") and "UNITY_INCLUDE_TESTS" not in constraints:
        errors.append(f"asmdef: {f} must keep defineConstraints UNITY_INCLUDE_TESTS (test code never reaches a player)")
    for define in constraints:
        package = VERSION_DEFINES.get(define)
        if package and not any(v.get("define") == define and v.get("name") == package[0]
                               and v.get("expression") == package[1] for v in a.get("versionDefines") or []):
            errors.append(f"asmdef: {name} is constrained to {define} but has no versionDefines entry "
                          f"{{name: {package[0]}, expression: {package[1]}, define: {define}}}; it would never compile")
    expected = ASMDEF_EXPECTED.get(name)
    if expected is None:
        return errors + [f"asmdef: {f} ({name}) has no ASMDEF_EXPECTED entry in Tools~/ci/check_repo.py; add its "
                         "autoReferenced, defineConstraints and platforms there"]
    auto, want_constraints, include, exclude = expected
    if a.get("autoReferenced", True) is not auto:
        errors.append(f"asmdef: {name} must keep autoReferenced: {str(auto).lower()}")
    if sorted(constraints) != sorted(want_constraints):
        errors.append(f"asmdef: {name} must keep defineConstraints {want_constraints} (has {constraints})")
    if asmdef_platforms(a) != (set(include), set(exclude)):
        errors.append(f"asmdef: {name} must keep includePlatforms {include} and excludePlatforms {exclude} "
                      f"(has {a.get('includePlatforms') or []} and {a.get('excludePlatforms') or []})")
    return errors


def check_reference_platforms(asmdefs):
    """An assembly must not build on a platform where an assembly it references is not built (#877)."""
    errors = []
    for f, a in sorted(asmdefs.items()):
        inc, exc = asmdef_platforms(a)
        for r in a.get("references") or []:
            target = next((t for t in asmdefs.values() if t["name"] == r), None)
            if target is None:
                continue  # Unity's TestRunner assemblies, UniTask
            r_inc, r_exc = asmdef_platforms(target)
            if r_inc and not (inc and inc <= r_inc):
                errors.append(f"asmdef: {a['name']} references {r}, which builds only for {sorted(r_inc)}; "
                              f"set includePlatforms to a subset of it")
            missing = sorted((inc & r_exc) if inc else (r_exc - exc))
            if missing:
                errors.append(f"asmdef: {a['name']} references {r}, which excludes {missing}; exclude them too")
    return errors


def check_asmdef(files):
    errors = []
    settings_checked = {}
    for f in files:
        if not f.endswith(".asmdef"):
            continue
        a = json.load(open(os.path.join(ROOT, f), encoding="utf-8-sig"))
        name, refs = a["name"], a.get("references") or []
        if not f.startswith("Samples~/"):
            errors += check_asmdef_settings(f, a)
            settings_checked[f] = a
        if name == "TurboHTTP.Complete" and (set(refs) != COMPLETE_REFS or len(refs) != len(set(refs))):
            errors.append(f"asmdef: TurboHTTP.Complete must reference exactly {sorted(COMPLETE_REFS)} "
                          f"(missing {sorted(COMPLETE_REFS - set(refs))}, extra {sorted(set(refs) - COMPLETE_REFS)}; "
                          "see the TurboHTTP.Complete row of Development~/docs/agents/architecture.md)")
        for r in refs:
            if r.startswith("GUID:"):
                errors.append(f"asmdef: {f} uses a GUID reference ({r}); use the assembly name")
        allowed = ALLOWED_REFS.get(name, {"TurboHTTP.Core"})
        if f.startswith("Samples~/"):
            # Samples show customer code: any shipped runtime module, never tests or editor tooling.
            allowed = None
            if not name.startswith("TurboHTTP.Samples."):
                errors.append(f"asmdef: {f} must be named TurboHTTP.Samples.<Name> (Package Manager import copies it into Assets)")
            for r in refs:
                if r.startswith("TurboHTTP.Tests") or r == "TurboHTTP.Editor" or r.startswith("UnityEngine.TestRunner"):
                    errors.append(f"asmdef: sample {name} must not reference {r}")
        if allowed is not None:
            for r in refs:
                if r not in allowed and r not in {"UnityEngine.TestRunner", "UnityEditor.TestRunner"}:
                    errors.append(f"asmdef: {name} must not reference {r} (allowed: {sorted(allowed) or 'nothing'}; "
                                  "see Development~/docs/agents/architecture.md)")
        if a.get("allowUnsafeCode") and name not in UNSAFE_ALLOWED:
            errors.append(f"asmdef: {name} enables allowUnsafeCode; only {sorted(UNSAFE_ALLOWED)} may")
        if name in ENGINE_FREE and not a.get("noEngineReferences"):
            errors.append(f"asmdef: {name} must keep noEngineReferences: true")
    for name in sorted(set(ASMDEF_EXPECTED) - {a["name"] for a in settings_checked.values()}):
        errors.append(f"asmdef: ASMDEF_EXPECTED lists {name}, but no asmdef declares it (remove or rename the entry)")
    return errors + check_reference_platforms(settings_checked)


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
            raw = json.dumps(data).lower()
            for placeholder in ("yourcompany", "your name", "custom/dual"):
                if placeholder in raw:
                    errors.append(f"json: package.json still has the placeholder '{placeholder}'")
            try:
                with open(os.path.join(ROOT, "CHANGELOG.md"), encoding="utf-8") as cl:
                    headings = re.findall(r"^## \[([^\]]+)\]", cl.read(), re.M)
            except OSError:
                headings = []
            # "Unreleased" is the Keep a Changelog heading for changes after the packaged version; the
            # first versioned heading must still match package.json.
            versioned = [h for h in headings if h.lower() != "unreleased"]
            if versioned and versioned[0] != data.get("version"):
                errors.append(
                    f"json: package.json version {data.get('version')} differs from the top versioned CHANGELOG entry {versioned[0]}")
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
_IGNORE_CALL = re.compile(r"\b(?:Assert\.Inconclusive|Assume\.\w+)\s*\(|(?<!\w)(?:\w+\.)*Ignore(?:Attribute)?\s*\(|\bIgnore(?:Reason)?\s*=(?!=)")
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


def _blank_cs_literals(src):
    """Blanks comments, string and char literals (keeping newlines and offsets) so brace matching and
    keyword searches only see code."""
    out, i, n = [], 0, len(src)
    while i < n:
        c = src[i]
        if src.startswith("//", i):
            j = src.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
            continue
        if src.startswith("/*", i):
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append(re.sub(r"[^\n]", " ", src[i:j]))
            i = j
            continue
        verbatim = re.match(r'\$?@\$?"', src[i:i + 3])
        if verbatim:
            j = i + len(verbatim.group(0))
            while j < n and not (src[j] == '"' and src[j + 1:j + 2] != '"'):
                j += 2 if src[j] == '"' else 1
            out.append(re.sub(r"[^\n]", " ", src[i:j + 1]))
            i = j + 1
            continue
        if c == '"' or (c == "$" and src[i + 1:i + 2] == '"'):
            interpolated = c == "$"
            j, depth = i + (2 if interpolated else 1), 0
            while j < n:
                if src[j] == "\\":
                    j += 2
                    continue
                if interpolated and src[j] == "{":
                    if src[j + 1:j + 2] == "{":
                        j += 2
                        continue
                    depth += 1
                elif interpolated and src[j] == "}" and depth:
                    depth -= 1
                elif src[j] == '"' and not depth:
                    break
                j += 1
            out.append(" " * (j + 1 - i))
            i = j + 1
            continue
        if c == "'":
            char = re.match(r"'(\\u[0-9a-fA-F]{4}|\\.|[^\\'])'", src[i:i + 8])
            if char:
                out.append(" " * len(char.group(0)))
                i += len(char.group(0))
                continue
        out.append(c)
        i += 1
    return "".join(out)


def _closing(code, i, open_ch, close_ch):
    depth = 0
    for j in range(i, len(code)):
        if code[j] == open_ch:
            depth += 1
        elif code[j] == close_ch:
            depth -= 1
            if depth == 0:
                return j
    return -1


def _skip_ws(code, i):
    while i < len(code) and code[i].isspace():
        i += 1
    return i


_THROWS = re.compile(r"\bthrow\b|\.Throw\(\)")
_AWAITS = re.compile(r"\bawait\b")


def il2cpp_filter_hazards(src):
    """(line, clause) of every catch clause that throws while a later clause of the same try statement
    can catch it under IL2CPP: the try has a `when` filter and the clause is not the last one."""
    code, hazards = _blank_cs_literals(src), []
    for m in re.finditer(r"\btry\b", code):
        i = _skip_ws(code, m.end())
        if i >= len(code) or code[i] != "{":
            continue
        k = _skip_ws(code, _closing(code, i, "{", "}") + 1)
        clauses = []
        while code.startswith("catch", k) and not re.match(r"\w", code[k + 5:k + 6]):
            start, k, filtered = k, _skip_ws(code, k + 5), False
            if code[k] == "(":
                k = _skip_ws(code, _closing(code, k, "(", ")") + 1)
            if code.startswith("when", k) and not re.match(r"\w", code[k + 4:k + 5]):
                k = _skip_ws(code, k + 4)
                k = _skip_ws(code, _closing(code, k, "(", ")") + 1)
                filtered = True
            if k >= len(code) or code[k] != "{":
                break
            end = _closing(code, k, "{", "}")
            if end < 0:
                break
            clauses.append((start, code[k:end + 1], filtered))
            k = _skip_ws(code, end + 1)
        if not any(filtered for _, _, filtered in clauses):
            continue
        for start, body, _ in clauses[:-1]:
            if _THROWS.search(body) and not _AWAITS.search(body):
                hazards.append((src.count("\n", 0, start) + 1, " ".join(src[start:src.find("{", start)].split())))
    return hazards


def check_il2cpp_filters(files):
    errors = []
    for f in files:
        if not (f.startswith(("Runtime/", "Tests/", "Editor/", "Samples~/")) and f.endswith(".cs")) or \
                f.startswith("Runtime/Transport/BouncyCastle/Lib/"):
            continue
        path = os.path.join(ROOT, f)
        if not os.path.exists(path):
            continue
        for line, clause in il2cpp_filter_hazards(open(path, encoding="utf-8-sig").read()):
            errors.append(f"il2cppfilter: {f}:{line}: `{clause}` throws, but its try has a `when` filter and a "
                          "later catch clause, which catches that exception under IL2CPP (#320); use one catch "
                          "clause (dispatch with if) or fold the pass-through into the next clause's filter")
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


# Player platforms a native plugin importer can target, with the symbols Unity defines for them.
# tvOS defines UNITY_TVOS, not UNITY_IOS, which is why plugins guarded by UNITY_IOS must not ship to tvOS.
NATIVE_PLATFORMS = {"iPhone": {"UNITY_IOS", "UNITY_IPHONE"}, "tvOS": {"UNITY_TVOS"}, "Android": {"UNITY_ANDROID"}}
NATIVE_SOURCES = (".mm", ".m", ".c", ".cpp")
# Android library folders are plugins too: Unity imports the folder (its .meta holds the PluginImporter).
NATIVE_FOLDERS = (".androidlib",)
# Unity 2021.3 ignores a native plugin importer's defineConstraints (an Android build without the define
# still packaged the .androidlib), so this editor gate is what keeps a constrained plugin out of a build.
PLUGIN_GATE = "Editor/TurboHttpBackgroundPluginGate.cs"


def plugin_gate(files):
    """(Define, [package-relative GatedPluginPaths]) parsed from PLUGIN_GATE, or None when it is missing."""
    if PLUGIN_GATE not in files:
        return None
    text = open(os.path.join(ROOT, PLUGIN_GATE), encoding="utf-8").read()
    define = re.search(r'\bconst\s+string\s+Define\s*=\s*"(\w+)"\s*;', text)
    paths = re.search(r"\bGatedPluginPaths\s*=\s*(?:new\s*(?:string\s*)?\[\s*\]\s*)?\{(.*?)\}\s*;", text, re.S)
    if not define or not paths:
        return define and define.group(1), None
    return define.group(1), re.findall(r'"([^"]+)"', paths.group(1))


def plugin_importer(meta_text):
    """(enabled platforms, define constraints) of a PluginImporter .meta, or None for other importers."""
    if "PluginImporter:" not in meta_text:
        return None
    enabled = set(re.findall(r"^      (\w+): \w*\n    second:\n      enabled: 1$", meta_text, re.M))
    m = re.search(r"^  defineConstraints:(.*)\n((?:  - .*\n)*)", meta_text, re.M)
    constraints = []
    if m:
        constraints = [c.strip() for c in m.group(2).replace("  - ", "").splitlines() if c.strip()]
    return enabled, constraints


def preprocessor_guards(text):
    """Yield (line index, active #if condition) for every line of a C# file."""
    stack = []  # per level: [conditions of earlier branches, current branch condition]
    for i, line in enumerate(text.splitlines()):
        d = re.match(r"\s*#\s*(if|elif|else|endif)\b(.*)", line)
        if d:
            kind, expr = d.group(1), re.sub(r"//.*", "", d.group(2)).strip()
            if kind == "if":
                stack.append([[], expr])
            elif kind == "elif" and stack:
                stack[-1][0].append(stack[-1][1])
                stack[-1][1] = expr
            elif kind == "else" and stack:
                stack[-1][0].append(stack[-1][1])
                stack[-1][1] = "true"
            elif kind == "endif" and stack:
                stack.pop()
        parts = []
        for earlier, current in stack:
            parts += [f"!({e})" for e in earlier] + [f"({current})"]
        yield i, " && ".join(parts) or "true"


def eval_guard(expr, defined):
    tokens = re.findall(r"&&|\|\||==|!=|!|\(|\)|\w+", expr)
    out = []
    for t in tokens:
        out.append({"&&": " and ", "||": " or ", "!": " not ", "==": " == ", "!=": " != ", "(": "(", ")": ")",
                    "true": " True ", "false": " False "}.get(t, f" {t in defined} "))
    return bool(eval("".join(out) or "True", {"__builtins__": {}}))


def guard_symbols(expr):
    return {t for t in re.findall(r"\w+", expr) if t not in ("true", "false")}


def check_native(files):
    errors = []
    exports = {}  # native symbol -> plugin path
    importers = {}
    folders = sorted({f[:-len(".meta")] for f in files if f.startswith("Plugins/") and f.endswith(".meta")
                      and f[:-len(".meta")].endswith(NATIVE_FOLDERS)})
    for f in list(files) + folders:
        is_folder = f in folders
        if not f.startswith("Plugins/") or not (is_folder or f.endswith(NATIVE_SOURCES)) or f + ".meta" not in files:
            continue
        info = plugin_importer(open(os.path.join(ROOT, f + ".meta"), encoding="utf-8").read())
        if info is None:
            errors.append(f"native: {f}.meta is not a PluginImporter")
            continue
        importers[f] = info
        if is_folder:
            continue  # Java/Kotlin: reached through AndroidJavaObject, not DllImport
        src = open(os.path.join(ROOT, f), encoding="utf-8", errors="replace").read()
        for name in re.findall(r'extern\s+"C"\s+[^;{(]*?\b(\w+)\s*\(', src):
            exports[name] = f
    callers = {}  # plugin -> [(cs file, line, guard)]
    for f in files:
        if not f.endswith(".cs") or not f.startswith("Runtime/") or f.startswith("Runtime/Transport/BouncyCastle/Lib/"):
            continue
        text = open(os.path.join(ROOT, f), encoding="utf-8", errors="replace").read()
        if '"__Internal"' not in text:
            continue
        lines = text.splitlines()
        guards = dict(preprocessor_guards(text))
        for i, line in enumerate(lines):
            if '"__Internal"' not in line:
                continue
            decl = next((l for l in lines[i + 1:i + 4] if " extern " in l), "")
            m = re.search(r"\bextern\s+[\w<>\[\],. ]+?\s+(\w+)\s*\(", decl)
            if not m:
                continue
            name = m.group(1)
            if name not in exports:
                errors.append(f"native: {f}:{i + 1}: DllImport(\"__Internal\") {name} has no extern \"C\" definition under Plugins/")
                continue
            callers.setdefault(exports[name], []).append((f, i + 1, guards[i]))
    for plugin, (enabled, constraints) in sorted(importers.items()):
        uses = callers.get(plugin, [])
        custom = set(constraints)
        for _, _, guard in uses:
            custom |= {s for s in guard_symbols(guard) if not s.startswith(("UNITY_", "ENABLE_"))}
        custom = sorted(custom)
        for platform, symbols in NATIVE_PLATFORMS.items():
            for mask in range(1 << len(custom)):
                user = {c for b, c in enumerate(custom) if mask >> b & 1}
                defined = symbols | user
                included = platform in enabled and all(eval_guard(c, defined) for c in constraints)
                called = [(cf, ln) for cf, ln, guard in uses if eval_guard(guard, defined)]
                where = f"{platform}" + (f" with {', '.join(sorted(user))}" if user else "")
                if called and not included:
                    cf, ln = called[0]
                    errors.append(f"native: {cf}:{ln} calls {plugin} on {where}, but its importer does not include it there")
                elif included and not called and uses:
                    errors.append(f"native: {plugin} ships to {where}, but no managed caller compiles there "
                                  "(disable that platform or the define constraint in its .meta, or extend the #if)")
    return errors + check_plugin_gate(files, importers) + check_result_codes(files)


def check_plugin_gate(files, importers):
    """The importer define constraints check_native evaluates take effect only through PLUGIN_GATE (#877)."""
    gate = plugin_gate(files)
    if gate is None:
        return [f"native: {PLUGIN_GATE} is missing; it gates the plugins whose importer has define constraints"]
    define, gated = gate
    if not define or gated is None:
        return [f"native: cannot parse the Define const and the GatedPluginPaths list of {PLUGIN_GATE}"]
    errors = []
    for path in gated:
        if path not in importers:
            errors.append(f"native: {PLUGIN_GATE} gates {path}, which is not a native plugin under Plugins/")
        elif importers[path][1] != [define]:
            errors.append(f"native: {path} is gated by {define}; its .meta defineConstraints must be exactly "
                          f"[{define}] (has {importers[path][1]})")
    for plugin, (_, constraints) in sorted(importers.items()):
        if constraints and plugin not in gated:
            errors.append(f"native: {plugin}.meta has defineConstraints {constraints}, which Unity 2021.3 ignores for "
                          f"native plugins; add it to GatedPluginPaths in {PLUGIN_GATE} or drop the constraint")
    return errors


# Native bridge result codes mirrored in C# (#278): same names and values on both sides, 0 reserved for
# "not set" (a rejection), success non-zero. (native source, enum prefix, C# file, const prefix)
RESULT_CODE_PAIRS = [("Plugins/iOS/TurboHttpSecTrustVerifier.mm", "kTurboTrust",
                      "Runtime/Transport/Tls/AppleTrustVerifier.cs", "Result")]


def _int_literal(text):
    return int(text, 16) if text.lower().startswith("0x") else int(text)


def check_result_codes(files):
    errors = []
    for native, native_prefix, managed, managed_prefix in RESULT_CODE_PAIRS:
        if native not in files or managed not in files:
            errors.append(f"native: result-code pair {native} / {managed} is missing")
            continue
        src = open(os.path.join(ROOT, native), encoding="utf-8", errors="replace").read()
        cs = open(os.path.join(ROOT, managed), encoding="utf-8", errors="replace").read()
        native_codes = {n: _int_literal(v) for n, v in
                        re.findall(rf"\b{native_prefix}(\w+)\s*=\s*(0x[0-9A-Fa-f]+|-?\d+)\s*,", src)}
        managed_codes = {n: _int_literal(v) for n, v in
                         re.findall(rf"\bconst\s+int\s+{managed_prefix}(\w+)\s*=\s*(0x[0-9A-Fa-f]+|-?\d+)\s*;", cs)}
        if native_codes != managed_codes:
            errors.append(f"native: result codes differ between {native} {sorted(native_codes.items())} "
                          f"and {managed} {sorted(managed_codes.items())}")
        for where, codes in ((native, native_codes), (managed, managed_codes)):
            if codes.get("Trusted", 0) == 0:
                errors.append(f"native: {where}: the Trusted result code must exist and be non-zero (#278)")
            if [n for n, v in codes.items() if v == 0] != ["NotSet"]:
                errors.append(f"native: {where}: 0 must be the NotSet code and nothing else (#278)")
    return errors


# Removed pipeline vocabulary (#232): the interceptor API replaced *Middleware types and Options.Middlewares;
# the request-level SendAsync() became SendBufferedAsync()/SendStreamingAsync(). The TurboHTTP.Middleware
# module/namespace still exists, so its name is allowed; a line that says "formerly" is a history note.
STALE_DOC_NAME = re.compile(r"\b\w*Middleware\w*\b|\bMiddlewares\b|\bSendAsync\(\s*\)|`SendAsync\(request\)`")
STALE_DOC_ALLOWED = re.compile(r"TurboHTTP\.Middleware\b|\(formerly [^)]*\)")


def check_docs(files):
    errors = []
    sys.path.insert(0, os.path.join(ROOT, "Tools~", "ci"))
    import doc_snippets
    _, problems = doc_snippets.all_snippets()
    errors += [f"docs: {p}" for p in problems]
    for f in files:
        if not (f == "README.md" or (f.startswith(("Documentation~/", "Samples~/")) and f.endswith((".md", ".cs")))):
            continue
        for n, line in enumerate(open(os.path.join(ROOT, f), encoding="utf-8").read().splitlines(), 1):
            for m in STALE_DOC_NAME.finditer(STALE_DOC_ALLOWED.sub("", line)):
                errors.append(f"docs: {f}:{n}: '{m.group(0)}' names an API that does not exist "
                              "(interceptors replaced middleware; use SendBufferedAsync/SendStreamingAsync)")
    samples = {s.get("path") for s in json.load(open(os.path.join(ROOT, "package.json"), encoding="utf-8-sig")).get("samples", [])}
    folders = {os.path.dirname(f) for f in files if f.startswith("Samples~/") and f.endswith(".asmdef")}
    for folder in sorted(samples - folders):
        errors.append(f"docs: package.json sample {folder} has no .asmdef (each sample compiles in the shadow build)")
    for folder in sorted({os.path.dirname(f) for f in files if f.startswith("Samples~/") and f.endswith(".cs")} - folders):
        errors.append(f"docs: {folder} has C# files but no .asmdef")
    for folder in sorted(folders - samples):
        errors.append(f"docs: {folder} has an .asmdef but package.json does not list it under samples (#877)")
    return errors


def main():
    wanted = sys.argv[1:] or ["meta", "asmdef", "json", "unitynunit", "ignores", "reflect", "il2cppfilter", "coreapi",
                              "native", "docs"]
    files = tracked()
    checks = {"meta": check_meta, "asmdef": check_asmdef, "json": check_json, "unitynunit": check_unity_nunit,
              "ignores": check_ignores,
              "reflect": check_reflect, "il2cppfilter": check_il2cpp_filters, "coreapi": check_core_api, "native": check_native, "docs": check_docs}
    errors = []
    for w in wanted:
        errors += checks[w](files)
    for e in errors:
        print(e)
    print(f"check_repo: {len(errors)} problem(s) in {', '.join(wanted)}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
