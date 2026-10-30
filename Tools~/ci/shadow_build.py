#!/usr/bin/env python3
"""Shadow build: compile every runtime/editor asmdef with the .NET SDK and analyzers.

Unity compiles TurboHTTP itself; this build exists so CI and agents get compiler
errors, Roslyn analyzers (.editorconfig) and banned-API checks (BannedSymbols.txt)
without a Unity install or license. Projects are generated from the asmdef files,
so references, unsafe code, define constraints and platform filters stay in sync.

Warnings from TurboHTTP sources are a ratchet: any warning not listed in
Tools~/ci/analyzer-baseline.txt fails the build. Fix it, or (for a deliberate
exception) run with --update-baseline and justify the new line in review.

Usage:
  Tools~/ci/shadow_build.py                     # editor variant
  Tools~/ci/shadow_build.py --variant all       # editor + ios + android
  Tools~/ci/shadow_build.py --update-baseline
"""
import argparse
import collections
import json
import os
import re
import subprocess
import sys
from xml.sax.saxutils import escape

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CI = os.path.join(ROOT, "Tools~", "ci")
OUT = os.path.join(ROOT, ".build", "shadow")
BASELINE = os.path.join(CI, "analyzer-baseline.txt")
SOURCE_DIRS = ["Runtime", "Editor"]
# Third-party code compiled but never held to TurboHTTP's analyzer policy.
VENDORED = ("Runtime/Transport/BouncyCastle/Lib/",)

UNITY_VERSIONS = ["5_3", "5_4", "5_5", "5_6", "2017_1", "2017_2", "2017_3", "2017_4", "2018_1", "2018_2",
                  "2018_3", "2018_4", "2019_1", "2019_2", "2019_3", "2019_4", "2020_1", "2020_2", "2020_3",
                  "2021_1", "2021_2", "2021_3"]
COMMON_DEFINES = ["UNITY_" + v + "_OR_NEWER" for v in UNITY_VERSIONS] + [
    "UNITY_2021", "UNITY_2021_3", "NET_STANDARD_2_1", "NET_STANDARD", "NETSTANDARD2_1", "NETSTANDARD", "CSHARP_7_3_OR_NEWER", "TRACE"]
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
    version = open(os.path.join(CI, "unity-version")).read().strip()
    path = os.path.join(ROOT, ".tools", "unity-managed", version)
    if not os.path.isfile(os.path.join(path, "UnityEngine.dll")):
        sys.exit(f"shadow_build: Unity managed DLLs not found at {path}; run Tools~/ci/fetch-unity-dlls.sh "
                 "or set UNITY_DLLS / UNITY_PATH")
    return path


def load_asmdefs():
    asmdefs = {}
    for base in SOURCE_DIRS:
        for dirpath, dirnames, filenames in os.walk(os.path.join(ROOT, base)):
            dirnames[:] = [d for d in dirnames if not d.endswith("~") and not d.startswith(".")]
            for f in filenames:
                if f.endswith(".asmdef"):
                    data = json.load(open(os.path.join(dirpath, f), encoding="utf-8-sig"))
                    data["_dir"] = dirpath
                    asmdefs[data["name"]] = data
    return asmdefs


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


def included(asm, platform, defines):
    inc, exc = asm.get("includePlatforms") or [], asm.get("excludePlatforms") or []
    if inc and platform not in inc:
        return False
    if platform in exc:
        return False
    return all(constraint_ok(c, defines) for c in asm.get("defineConstraints") or [])


def generate(variant, asmdefs, dlls):
    platform, extra, editor_visible = VARIANTS[variant]
    defines = set(COMMON_DEFINES + extra)
    chosen = {n: a for n, a in asmdefs.items() if included(a, platform, defines)}
    # Drop asmdefs whose references cannot be satisfied (e.g. third-party UniTask), transitively.
    changed = True
    while changed:
        changed = False
        for n, a in list(chosen.items()):
            if any(r not in chosen for r in a.get("references") or []):
                del chosen[n]
                changed = True

    engine = sorted(f for f in os.listdir(dlls) if f.startswith("UnityEngine") and f.endswith(".dll"))
    editor = sorted(f for f in os.listdir(dlls) if f.startswith("UnityEditor") and f.endswith(".dll"))
    vdir = os.path.join(OUT, variant)
    projects = []
    for name, a in sorted(chosen.items()):
        pdir = os.path.join(vdir, name)
        os.makedirs(pdir, exist_ok=True)
        refs = []
        if not a.get("noEngineReferences"):
            refs += engine + (editor if editor_visible else [])
        lines = [
            '<Project Sdk="Microsoft.NET.Sdk">',
            f'  <Import Project="{escape(os.path.join(CI, "Shadow.props"))}" />',
            "  <PropertyGroup>",
            f"    <AssemblyName>{name}</AssemblyName>",
            f"    <AllowUnsafeBlocks>{'true' if a.get('allowUnsafeCode') else 'false'}</AllowUnsafeBlocks>",
            f"    <DefineConstants>$(DefineConstants);{';'.join(sorted(defines))}</DefineConstants>",
            "  </PropertyGroup>",
            "  <ItemGroup>",
        ]
        lines += [f'    <Compile Include="{escape(s)}" />' for s in sources(a["_dir"])]
        lines += [f'    <ProjectReference Include="../{r}/{r}.csproj" />' for r in a.get("references") or []]
        lines += [f'    <Reference Include="{escape(os.path.join(dlls, d))}" Private="false" />' for d in refs]
        lines += ["  </ItemGroup>", "</Project>", ""]
        open(os.path.join(pdir, name + ".csproj"), "w").write("\n".join(lines))
        projects.append(name)

    lines = ['<Project Sdk="Microsoft.NET.Sdk">',
             "  <PropertyGroup><TargetFramework>netstandard2.1</TargetFramework>"
             "<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>",
             "  <ItemGroup>"]
    lines += [f'    <ProjectReference Include="{p}/{p}.csproj" />' for p in projects]
    lines += ["  </ItemGroup>", "</Project>", ""]
    open(os.path.join(vdir, "All.csproj"), "w").write("\n".join(lines))
    skipped = sorted(set(asmdefs) - set(chosen))
    return os.path.join(vdir, "All.csproj"), projects, skipped


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
        text = open(os.path.join(ROOT, path), encoding="utf-8-sig").read().splitlines()[int(d["line"]) - 1].strip()
    except (OSError, IndexError):
        text = ""
    return f"{d['rule']}|{path}|{text}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--variant", default="editor", choices=list(VARIANTS) + ["all"])
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()

    dlls = unity_dlls()
    asmdefs = load_asmdefs()
    variants = list(VARIANTS) if args.variant == "all" else [args.variant]
    found = collections.Counter()
    failed = False
    for v in variants:
        project, projects, skipped = generate(v, asmdefs, dlls)
        print(f"shadow_build[{v}]: {len(projects)} assemblies (skipped: {', '.join(skipped) or 'none'})")
        code, diags, output = build(project)
        errors = [d for d in diags if d["kind"] == "error"]
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
            if rel.startswith("..") or rel.startswith(VENDORED) or rel.startswith(".build"):
                continue
            # MSBuild prints each diagnostic once per project that reports it; dedupe exact repeats.
            ident = (d["file"], d["line"], d["rule"], d["msg"])
            if ident in seen:
                continue
            seen.add(ident)
            variant_keys[baseline_key(d)] += 1
        for k, n in variant_keys.items():
            found[k] = max(found[k], n)

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
