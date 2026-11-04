#!/usr/bin/env python3
"""Shadow build: compile every runtime/editor/sample/test asmdef with the .NET SDK and analyzers.

Unity compiles TurboHTTP itself; this build exists so CI and agents get compiler
errors, Roslyn analyzers (.editorconfig) and banned-API checks (BannedSymbols.txt)
without a Unity install or license. Projects are generated from the asmdef files,
so references, unsafe code, define constraints and platform filters stay in sync.

Warnings from TurboHTTP sources are a ratchet: any warning not listed in
Tools~/ci/analyzer-baseline.txt fails the build. Fix it, or (for a deliberate
exception) run with --update-baseline and justify the new line in review.

The Tests/ asmdefs (#297) compile with UNITY_INCLUDE_TESTS against the NUnit and Test Framework
packages Unity 2021.3 bundles (fetch-unity-dlls.sh copies them next to the DLLs), so a compile
error or an NUnit API Unity's NUnit lacks fails here. Tests are outside the analyzer ratchet.

Usage:
  Tools~/ci/shadow_build.py                     # editor variant
  Tools~/ci/shadow_build.py --variant all       # editor + ios + android
  Tools~/ci/shadow_build.py --no-tests          # runtime assemblies only (tools that need the DLLs)
  Tools~/ci/shadow_build.py --update-baseline
"""
import argparse
import collections
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
from xml.sax.saxutils import escape

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import doc_snippets  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CI = os.path.join(ROOT, "Tools~", "ci")
OUT = os.path.join(ROOT, ".build", "shadow")
BASELINE = os.path.join(CI, "analyzer-baseline.txt")
# Samples~ is compiled like a runtime module (each sample ships its own asmdef), so a sample that no
# longer compiles against the API fails here instead of in a customer project (#98).
SOURCE_DIRS = ["Runtime", "Editor", "Samples~", "Tests"]
# Test assemblies (#297): built with UNITY_INCLUDE_TESTS, analyzers off, warnings outside the ratchet;
# compile errors still fail. Unity's own NUnit (com.unity.ext.nunit 1.0.6, not NuGet NUnit) is the
# reference, so Assert.Multiple / Assert.ThrowsAsync and other APIs it lacks fail to compile.
TEST_DIRS = ("Tests",)
# The packages Unity 2021.3 bundles in Editor/Data/Resources/PackageManager/Editor; keep in sync with
# fetch-unity-dlls.sh and the com.unity.test-framework pin in Tools~/unity/*.sh.
NUNIT_PACKAGE = "com.unity.ext.nunit-1.0.6.tgz"
TEST_FRAMEWORK_PACKAGE = "com.unity.test-framework-1.1.33.tgz"
# The Test Framework package ships these as source; they are generated as projects like any asmdef.
TEST_RUNNER_ASMDEFS = ("UnityEngine.TestRunner", "UnityEditor.TestRunner")
PACKAGES_OUT = os.path.join(ROOT, ".build", "unity-packages")
DOC_ASSEMBLY = "TurboHTTP.DocSnippets"
# Third-party code compiled but never held to TurboHTTP's analyzer policy.
VENDORED = ("Runtime/Transport/BouncyCastle/Lib/",)

UNITY_VERSIONS = ["5_3", "5_4", "5_5", "5_6", "2017_1", "2017_2", "2017_3", "2017_4", "2018_1", "2018_2",
                  "2018_3", "2018_4", "2019_1", "2019_2", "2019_3", "2019_4", "2020_1", "2020_2", "2020_3",
                  "2021_1", "2021_2", "2021_3"]
COMMON_DEFINES = ["UNITY_" + v + "_OR_NEWER" for v in UNITY_VERSIONS] + [
    "UNITY_2021", "UNITY_2021_3", "NET_STANDARD_2_1", "NET_STANDARD", "NETSTANDARD2_1", "NETSTANDARD", "CSHARP_7_3_OR_NEWER", "TRACE",
    # Opt-in BouncyCastle (#196): defined so its glue and vendored library stay compiled and analyzed.
    # Nothing else references them, so the other assemblies build the same with or without it.
    "TURBOHTTP_BOUNCYCASTLE",
    # Opt-in background networking (#106): defined so the iOS/Android native bridge code paths compile
    # and are analyzed in the ios/android variants.
    "TURBOHTTP_BACKGROUND_NETWORKING"]
VARIANTS = {
    # name: (Unity platform name for include/excludePlatforms, extra defines, editor assemblies visible)
    "editor": ("Editor", ["UNITY_EDITOR", "UNITY_EDITOR_64", "UNITY_EDITOR_OSX", "DEBUG", "ENABLE_MONO"], True),
    "ios": ("iOS", ["UNITY_IOS", "UNITY_IPHONE", "ENABLE_IL2CPP"], False),
    "android": ("Android", ["UNITY_ANDROID", "ENABLE_IL2CPP"], False),
}

DIAG = re.compile(r"^(?P<file>[^(\n]+?)\((?P<line>\d+),\d+\): (?P<kind>warning|error) (?P<rule>[A-Z]+\d+): (?P<msg>.*?)(?: \[[^\]]+\])?$")


def unity_dlls():
    for env in ("UNITY_DLLS",):
        if os.environ.get(env):
            return os.environ[env]
    if os.environ.get("UNITY_PATH"):
        # .../Unity.app/Contents/MacOS/Unity (mac) or .../Editor/Unity (linux/windows)
        editor = os.path.dirname(os.environ["UNITY_PATH"])
        for cand in (os.path.join(editor, "..", "Managed", "UnityEngine"),
                     os.path.join(editor, "Data", "Managed", "UnityEngine")):
            if os.path.isfile(os.path.join(cand, "UnityEngine.dll")):
                return os.path.abspath(cand)
    with open(os.path.join(CI, "unity-version")) as f:
        version = f.read().strip()
    path = os.path.join(ROOT, ".tools", "unity-managed", version)
    if not os.path.isfile(os.path.join(path, "UnityEngine.dll")):
        sys.exit(f"shadow_build: Unity managed DLLs not found at {path}; run Tools~/ci/fetch-unity-dlls.sh "
                 "or set UNITY_DLLS / UNITY_PATH")
    return path


def write_text(path, text):
    with open(path, "w") as f:
        f.write(text)


def load_asmdefs(root=ROOT, dirs=SOURCE_DIRS):
    asmdefs = {}
    for base in dirs:
        for dirpath, dirnames, filenames in os.walk(os.path.join(root, base)):
            dirnames[:] = [d for d in dirnames if not d.endswith("~") and not d.startswith(".")]
            for f in filenames:
                if f.endswith(".asmdef"):
                    with open(os.path.join(dirpath, f), encoding="utf-8-sig") as fh:
                        data = json.load(fh)
                    data["_dir"] = dirpath
                    data["_tests"] = base in TEST_DIRS
                    asmdefs[data["name"]] = data
    return asmdefs


def package_tarball_dirs(dlls):
    # fetch-unity-dlls.sh puts the tarballs in <dlls>/packages; a local editor (UNITY_PATH) has them in
    # Data/Resources/PackageManager/Editor (Unity.app/Contents/Resources/... on macOS).
    dirs = [os.path.join(dlls, "packages")]
    if os.environ.get("UNITY_PATH"):
        editor = os.path.dirname(os.environ["UNITY_PATH"])
        dirs += [os.path.join(editor, "..", "Resources", "PackageManager", "Editor"),
                 os.path.join(editor, "Data", "Resources", "PackageManager", "Editor")]
    return dirs


def extract_package(tgz, dest):
    """Extracts a package tarball once into dest (regular files and directories only, inside dest)."""
    if os.path.isfile(os.path.join(dest, ".extracted")):
        return
    tmp = dest + ".tmp"
    shutil.rmtree(tmp, ignore_errors=True)
    base = os.path.realpath(tmp) + os.sep
    with tarfile.open(tgz, "r:gz") as tar:
        members = [m for m in tar.getmembers() if m.isfile() or m.isdir()]
        for m in members:
            if not os.path.realpath(os.path.join(tmp, m.name)).startswith(base):
                sys.exit(f"shadow_build: {tgz}: entry {m.name!r} escapes the extraction directory")
        if hasattr(tarfile, "data_filter"):
            tar.extractall(tmp, members=members, filter="data")
        else:
            tar.extractall(tmp, members=members)
    write_text(os.path.join(tmp, ".extracted"), "")
    shutil.rmtree(dest, ignore_errors=True)
    os.rename(tmp, dest)


def test_framework(dlls):
    """Unity's test packages: (asmdefs of the TestRunner assemblies, {precompiled dll name: path}).

    Returns (None, None) when the tarballs are not found next to the DLLs or in the UNITY_PATH editor.
    """
    roots = {}
    for name in (NUNIT_PACKAGE, TEST_FRAMEWORK_PACKAGE):
        tgz = next((os.path.join(d, name) for d in package_tarball_dirs(dlls)
                    if os.path.isfile(os.path.join(d, name))), None)
        if tgz is None:
            return None, None
        dest = os.path.join(PACKAGES_OUT, name[:-len(".tgz")])
        extract_package(tgz, dest)
        roots[name] = os.path.join(dest, "package")
    asmdefs = {}
    for name in TEST_RUNNER_ASMDEFS:
        path = os.path.join(roots[TEST_FRAMEWORK_PACKAGE], name, name + ".asmdef")
        with open(path, encoding="utf-8-sig") as fh:
            data = json.load(fh)
        data["_dir"] = os.path.dirname(path)
        data["_external"] = True
        asmdefs[name] = data
    nunit = os.path.join(roots[NUNIT_PACKAGE], "net35", "unity-custom", "nunit.framework.dll")
    return asmdefs, {"nunit.framework.dll": nunit}


def sources(asmdef_dir):
    files = []
    for dirpath, dirnames, filenames in os.walk(asmdef_dir):
        dirnames[:] = sorted(d for d in dirnames if not d.endswith("~") and not d.startswith(".")
                             and not any(x.endswith((".asmdef", ".asmref"))
                                         for x in os.listdir(os.path.join(dirpath, d))))
        files += [os.path.join(dirpath, f) for f in sorted(filenames) if f.endswith(".cs")]
    return files


def constraint_ok(expr, defines):
    # defineConstraints entries: "SYMBOL", "!SYMBOL", or "A || B".
    return any((s.strip()[1:] not in defines) if s.strip().startswith("!") else (s.strip() in defines)
               for s in expr.split("||"))


def platform_ok(asm, platform):
    inc, exc = asm.get("includePlatforms") or [], asm.get("excludePlatforms") or []
    return (not inc or platform in inc) and platform not in exc


def included(asm, platform, defines):
    return platform_ok(asm, platform) and all(constraint_ok(c, defines) for c in asm.get("defineConstraints") or [])


def generate(variant, asmdefs, dlls, out=OUT, precompiled=None, tests=True, docs=True):
    """Writes <out>/<variant>/All.csproj; returns (path, projects, skipped, dropped).

    dropped lists (name, missing references) for asmdefs that pass their platform and define
    constraints but reference an assembly the build cannot provide; main() fails on them.
    """
    platform, extra, editor_visible = VARIANTS[variant]
    precompiled = precompiled or {}
    defines = set(COMMON_DEFINES + extra + (["UNITY_INCLUDE_TESTS"] if tests else []))
    candidates = {n: a for n, a in asmdefs.items() if tests or not (a.get("_tests") or a.get("_external"))}
    chosen = {n: a for n, a in candidates.items() if included(a, platform, defines)}

    # Like Unity, ignore a reference to an assembly that is not built for this platform (the runtime
    # tests' UnityEditor.TestRunner reference in a player build). Any other unsatisfied reference drops
    # the asmdef, transitively.
    def unresolved(a):
        return [r for r in a.get("references") or []
                if r not in chosen and not (r in candidates and not platform_ok(candidates[r], platform))]

    dropped = []
    changed = True
    while changed:
        changed = False
        for n, a in list(chosen.items()):
            missing = unresolved(a)
            if missing:
                del chosen[n]
                dropped.append((n, missing))
                changed = True

    engine = sorted(f for f in os.listdir(dlls) if f.startswith("UnityEngine") and f.endswith(".dll"))
    editor = sorted(f for f in os.listdir(dlls) if f.startswith("UnityEditor") and f.endswith(".dll"))
    vdir = os.path.join(out, variant)
    projects = []
    for name, a in sorted(chosen.items()):
        pdir = os.path.join(vdir, name)
        os.makedirs(pdir, exist_ok=True)
        refs = []
        if not a.get("noEngineReferences"):
            refs = [os.path.join(dlls, d) for d in engine + (editor if editor_visible else [])]
        # Only the precompiled DLLs the build knows (nunit.framework.dll); the Test Framework's unused
        # Mono.Cecil entries have no counterpart and nothing references them.
        refs += [precompiled[p] for p in a.get("precompiledReferences") or [] if p in precompiled]
        lines = [
            '<Project Sdk="Microsoft.NET.Sdk">',
            f'  <Import Project="{escape(os.path.join(CI, "Shadow.props"))}" />',
            "  <PropertyGroup>",
            f"    <AssemblyName>{name}</AssemblyName>",
            f"    <AllowUnsafeBlocks>{'true' if a.get('allowUnsafeCode') else 'false'}</AllowUnsafeBlocks>",
            f"    <DefineConstants>$(DefineConstants);{';'.join(sorted(defines))}</DefineConstants>",
        ]
        if a.get("_tests") or a.get("_external"):
            # Tests and Unity's Test Framework source: compiler errors only, no analyzer policy.
            lines.append("    <RunAnalyzers>false</RunAnalyzers>")
        lines += ["  </PropertyGroup>", "  <ItemGroup>"]
        lines += [f'    <Compile Include="{escape(s)}" />' for s in sources(a["_dir"])]
        lines += [f'    <ProjectReference Include="../{r}/{r}.csproj" />' for r in a.get("references") or [] if r in chosen]
        lines += [f'    <Reference Include="{escape(r)}" Private="false" />' for r in refs]
        lines += ["  </ItemGroup>", "</Project>", ""]
        write_text(os.path.join(pdir, name + ".csproj"), "\n".join(lines))
        projects.append(name)

    if docs:
        projects.append(generate_doc_snippets(vdir, chosen, defines, dlls, engine + (editor if editor_visible else [])))

    lines = ['<Project Sdk="Microsoft.NET.Sdk">',
             "  <PropertyGroup><TargetFramework>netstandard2.1</TargetFramework>"
             "<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>",
             "  <ItemGroup>"]
    lines += [f'    <ProjectReference Include="{p}/{p}.csproj" />' for p in projects]
    lines += ["  </ItemGroup>", "</Project>", ""]
    os.makedirs(vdir, exist_ok=True)
    write_text(os.path.join(vdir, "All.csproj"), "\n".join(lines))
    skipped = sorted(set(candidates) - set(chosen))
    return os.path.join(vdir, "All.csproj"), projects, skipped, dropped


def generate_doc_snippets(vdir, chosen, defines, dlls, unity_refs):

    # Documentation snippets (README.md, Documentation~): one generated file per fenced csharp block,
    # compiled against every module so a documented API that does not exist fails the build (#232).
    files, _, _ = doc_snippets.write_project_sources(os.path.join(vdir, DOC_ASSEMBLY, "src"))
    pdir = os.path.join(vdir, DOC_ASSEMBLY)
    doc_refs = sorted(n for n, a in chosen.items()
                      if not n.startswith("TurboHTTP.Samples.") and not a.get("_tests") and not a.get("_external"))
    lines = [
        '<Project Sdk="Microsoft.NET.Sdk">',
        f'  <Import Project="{escape(os.path.join(CI, "Shadow.props"))}" />',
        "  <PropertyGroup>",
        f"    <AssemblyName>{DOC_ASSEMBLY}</AssemblyName>",
        f"    <DefineConstants>$(DefineConstants);{';'.join(sorted(defines))}</DefineConstants>",
        # Snippets are illustrations: unused locals and async methods without await are fine.
        "    <RunAnalyzers>false</RunAnalyzers>",
        "    <NoWarn>$(NoWarn);CS0168;CS0219;CS1998;CS8321;CS0162;CS0649;CS0414</NoWarn>",
        "  </PropertyGroup>",
        "  <ItemGroup>",
    ]
    lines += [f'    <Compile Include="{escape(s)}" />' for s in files]
    lines += [f'    <ProjectReference Include="../{r}/{r}.csproj" />' for r in doc_refs]
    lines += [f'    <Reference Include="{escape(os.path.join(dlls, d))}" Private="false" />' for d in unity_refs]
    lines += ["  </ItemGroup>", "</Project>", ""]
    write_text(os.path.join(pdir, DOC_ASSEMBLY + ".csproj"), "\n".join(lines))
    return DOC_ASSEMBLY


def build(project):
    proc = subprocess.run(["dotnet", "build", project, "-nologo", "-v:q", "-clp:NoSummary",
                           "-p:TreatWarningsAsErrors=false"], capture_output=True, text=True)
    diags = []
    for line in (proc.stdout + proc.stderr).splitlines():
        m = DIAG.match(line.strip())
        if m:
            diags.append(m.groupdict())
    return proc.returncode, diags, proc.stdout + proc.stderr


def baseline_key(d):
    path = os.path.relpath(os.path.abspath(d["file"]), ROOT)
    try:
        with open(os.path.join(ROOT, path), encoding="utf-8-sig") as f:
            text = f.read().splitlines()[int(d["line"]) - 1].strip()
    except (OSError, IndexError):
        text = ""
    return f"{d['rule']}|{path}|{text}"


def run_variants(variants, asmdefs, dlls, out=OUT, precompiled=None, tests=True, docs=True):
    """Generates and builds each variant; returns (failed, ratchet warning keys, errors)."""
    found = collections.Counter()
    failed = False
    all_errors = []
    for v in variants:
        project, projects, skipped, dropped = generate(v, asmdefs, dlls, out, precompiled, tests, docs)
        print(f"shadow_build[{v}]: {len(projects)} assemblies (skipped: {', '.join(skipped) or 'none'})")
        for name, missing in dropped:
            # A silently skipped assembly would hide every error in it (e.g. all of Tests/Runtime).
            failed = True
            all_errors.append({"file": "", "line": "0", "rule": "DROPPED", "msg": name})
            print(f"  error DROPPED {name}: references not built in [{v}]: {', '.join(missing)}")
        code, diags, output = build(project)
        errors = [d for d in diags if d["kind"] == "error"]
        all_errors += errors
        if code != 0 or errors:
            failed = True
            shown = errors or [{"file": "", "line": "0", "rule": "BUILD", "msg": output[-3000:]}]
            for d in shown[:50]:
                print(f"  error {d['rule']} {os.path.relpath(d['file'], ROOT) if d['file'] else ''}:{d['line']} {d['msg']}")
        variant_keys = collections.Counter()
        seen = set()
        for d in diags:
            if d["kind"] != "warning":
                continue
            rel = os.path.relpath(os.path.abspath(d["file"]), ROOT)
            if rel.startswith("..") or rel.startswith(VENDORED) or rel.startswith(".build") or rel.endswith(".md"):
                continue
            if rel.startswith(tuple(t + os.sep for t in TEST_DIRS)):
                continue  # tests: compile errors only (#297), no warning ratchet
            # MSBuild prints each diagnostic once per project that reports it; dedupe exact repeats.
            ident = (d["file"], d["line"], d["rule"], d["msg"])
            if ident in seen:
                continue
            seen.add(ident)
            variant_keys[baseline_key(d)] += 1
        for k, n in variant_keys.items():
            found[k] = max(found[k], n)
    return failed, found, all_errors


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--variant", default="editor", choices=list(VARIANTS) + ["all"])
    ap.add_argument("--update-baseline", action="store_true")
    ap.add_argument("--no-tests", action="store_true",
                    help="skip the Tests/ asmdefs (for tools that only need the runtime assemblies)")
    args = ap.parse_args()

    dlls = unity_dlls()
    asmdefs = load_asmdefs()
    precompiled = None
    if not args.no_tests:
        framework, precompiled = test_framework(dlls)
        if framework is None:
            sys.exit(f"shadow_build: {NUNIT_PACKAGE} / {TEST_FRAMEWORK_PACKAGE} not found in "
                     f"{' or '.join(package_tarball_dirs(dlls))}; run Tools~/ci/fetch-unity-dlls.sh "
                     "(it completes an older cache), or pass --no-tests")
        asmdefs.update(framework)
    variants = list(VARIANTS) if args.variant == "all" else [args.variant]
    failed, found, _ = run_variants(variants, asmdefs, dlls, precompiled=precompiled, tests=not args.no_tests)

    if args.update_baseline:
        with open(BASELINE, "w") as f:
            f.write("# Accepted analyzer warnings (rule|file|source line). Generated by\n"
                    "# Tools~/ci/shadow_build.py --update-baseline; shrink it, never grow it casually.\n")
            for k in sorted(found.elements()):
                f.write(k + "\n")
        print(f"shadow_build: baseline updated ({sum(found.values())} entries)")
        return 1 if failed else 0

    base = collections.Counter(l.rstrip("\n") for l in open(BASELINE) if l.strip() and not l.startswith("#")) \
        if os.path.exists(BASELINE) else collections.Counter()
    new = found - base
    stale = base - found
    for k in sorted(new.elements()):
        rule, path, text = k.split("|", 2)
        print(f"  new warning {rule} {path}: {text}")
    if stale and args.variant == "all":
        print(f"shadow_build: {sum(stale.values())} baseline entries no longer occur; "
              "run with --update-baseline to shrink the baseline")
    if new:
        print(f"shadow_build: {sum(new.values())} new analyzer warning(s); fix them "
              "(see .editorconfig / Tools~/ci/BannedSymbols.txt for why each rule exists)")
    return 1 if failed or new else 0


if __name__ == "__main__":
    sys.exit(main())
