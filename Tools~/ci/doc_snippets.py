#!/usr/bin/env python3
"""Extract the C# snippets of README.md and Documentation~/ for the shadow build (#232, #98).

Every fenced ```csharp block is compiled against the current runtime assemblies by
Tools~/ci/shadow_build.py (project TurboHTTP.DocSnippets), so a documented type or member that
does not exist fails the build with the Markdown file and line.

Two kinds of blocks:
  * declaration blocks (they declare a class/struct/interface/enum/record) are compiled as written.
    They must carry every `using` they need.
  * fragments (statements) are wrapped in `static async Task Run()` after this ambient prelude:
        using System; using System.Threading; using System.Threading.Tasks; using UnityEngine;
        using TurboHTTP.Core;
    It is exactly the script header the docs tell readers to assume (APIReference.md, Modules/Core.md);
    a doc line that states the header with other usings is a problem (#621). Anything else, module
    namespaces (TurboHTTP.JSON, TurboHTTP.Retry, ...) and System.Collections.Generic / System.IO
    included, is never ambient: the snippet says `using TurboHTTP.JSON;` itself, as a reader copying it must.

Directives are HTML comments on the line(s) right before the fence (invisible when rendered):
    <!-- snippet: with UHttpClient client = null; -->     declarations the fragment assumes
    <!-- snippet: requires unitask -->                     compiled only when the build has that optional
                                                          package (REQUIREMENTS); otherwise listed as skipped
    <!-- snippet: skip reason -->                          not compiled; the reason is mandatory
Skipped blocks are never silent: this script and the shadow build print each one with its reason.

Use another info string (`text`, `json`, `csharp-pseudo`) for illustrations that are not C#.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

DOC_ROOTS = ["README.md", "Documentation~"]
PRELUDE = ["System", "System.Threading", "System.Threading.Tasks", "UnityEngine", "TurboHTTP.Core"]
# Optional packages a snippet can require: name -> what the shadow build needs to compile it (#621).
REQUIREMENTS = {"unitask": "UniTask (the shadow build fetches it unless --no-unitask)"}
# The documented script header: "... script header (`using System; ...`)" in the docs' prose.
HEADER = re.compile(r"script header[^`]*`((?:\s*using\s+[\w.]+\s*;)+)\s*`")

FENCE = re.compile(r"^(\s*)(`{3,}|~{3,})\s*([\w#+-]*)")
DIRECTIVE = re.compile(r"^\s*<!--\s*snippet:\s*(skip|with|requires)\b\s*(.*?)\s*-->\s*$")
USING = re.compile(r"^\s*using\s+(?:static\s+)?(?:\w+\s*=\s*)?[\w.<>,\s]+;\s*$")
TYPE_DECL = re.compile(r"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|unsafe)\s+)*"
                       r"(?:class|struct|interface|enum|record)\s+\w+")


class Snippet:
    def __init__(self, path, line, lines, skip, with_decls, requires=()):
        self.path = path          # repository-relative, forward slashes
        self.line = line          # 1-based line of the first code line
        self.lines = lines
        self.skip = skip          # None or the reason
        self.with_decls = with_decls
        self.requires = list(requires)  # REQUIREMENTS keys the build must provide

    def missing(self, available):
        """Why the snippet is not compiled with the given optional packages, or None."""
        if self.skip:
            return self.skip
        absent = [r for r in self.requires if r not in available]
        return "requires " + ", ".join(REQUIREMENTS.get(r, r) for r in absent) if absent else None

    @property
    def is_declaration(self):
        return any(TYPE_DECL.match(l) for l in self.lines)


def markdown_files():
    out = []
    for entry in DOC_ROOTS:
        full = os.path.join(ROOT, entry)
        if os.path.isfile(full):
            out.append(entry)
            continue
        for dirpath, _, filenames in os.walk(full):
            out += [os.path.relpath(os.path.join(dirpath, f), ROOT).replace(os.sep, "/")
                    for f in sorted(filenames) if f.endswith(".md")]
    return sorted(out)


def extract(rel):
    """Snippets of one Markdown file; returns (snippets, problems)."""
    snippets, problems = [], []
    with open(os.path.join(ROOT, rel), encoding="utf-8") as fh:
        text = fh.read().splitlines()
    for n, line in enumerate(text, 1):
        h = HEADER.search(line)
        if h:
            usings = re.findall(r"using\s+([\w.]+)\s*;", h.group(1))
            if usings != PRELUDE:
                problems.append(f"{rel}:{n}: the documented script header {usings} differs from the snippet "
                                f"prelude {PRELUDE} in Tools~/ci/doc_snippets.py; keep them identical")
    i = 0
    pending = {"skip": None, "with": [], "requires": []}
    while i < len(text):
        m = FENCE.match(text[i])
        if not m:
            d = DIRECTIVE.match(text[i])
            if d:
                if d.group(1) == "skip":
                    if not d.group(2):
                        problems.append(f"{rel}:{i + 1}: snippet skip needs a reason")
                    pending["skip"] = d.group(2) or "(no reason)"
                elif d.group(1) == "requires":
                    for r in d.group(2).split():
                        if r not in REQUIREMENTS:
                            problems.append(f"{rel}:{i + 1}: snippet requires unknown {r!r} (known: {sorted(REQUIREMENTS)})")
                        pending["requires"].append(r)
                else:
                    pending["with"].append(d.group(2))
            elif text[i].strip():
                pending = {"skip": None, "with": [], "requires": []}
            i += 1
            continue
        fence, lang = m.group(2), m.group(3).lower()
        start = i + 1
        j = start
        while j < len(text) and not re.match(r"^\s*" + re.escape(fence[0]) + "{" + str(len(fence)) + ",}\\s*$", text[j]):
            j += 1
        if lang in ("csharp", "cs", "c#"):
            snippets.append(Snippet(rel, start + 1, text[start:j], pending["skip"], pending["with"], pending["requires"]))
        elif pending["skip"] or pending["with"] or pending["requires"]:
            problems.append(f"{rel}:{i + 1}: snippet directive before a non-csharp block")
        pending = {"skip": None, "with": [], "requires": []}
        i = j + 1
    return snippets, problems


def all_snippets():
    snippets, problems = [], []
    for rel in markdown_files():
        s, p = extract(rel)
        snippets += s
        problems += p
    return snippets, problems


def render(index, sn):
    """C# source for one snippet; #line directives map diagnostics back to the Markdown file."""
    md = os.path.join(ROOT, sn.path).replace("\\", "/")
    out = [f"// {sn.path}:{sn.line}", f"namespace DocSnippets.S{index:03d}", "{"]
    if sn.is_declaration:
        out.append(f'#line {sn.line} "{md}"')
        out += sn.lines
        out.append("}")
        return "\n".join(out) + "\n"
    k = 0
    # Leading using directives stay at namespace scope; everything after is the method body.
    while k < len(sn.lines) and (USING.match(sn.lines[k]) or not sn.lines[k].strip() or sn.lines[k].lstrip().startswith("//")):
        k += 1
    out += [f"    using {p};" for p in PRELUDE]
    out.append(f'#line {sn.line} "{md}"')
    out += sn.lines[:k]
    out += ["    static class Snippet", "    {", "        static async Task Run()", "        {"]
    out += ["            " + d for d in sn.with_decls]
    out.append(f'#line {sn.line + k} "{md}"')
    out += sn.lines[k:]
    out += ["        }", "    }", "}"]
    return "\n".join(out) + "\n"


# Types the prose calls "your own" (a DTO, a MonoBehaviour) so fragments can name them.
AMBIENT_SOURCE = """// Ambient types for documentation fragments (Tools~/ci/doc_snippets.py).
[System.Serializable]
public class MyDto { public int id; public string name; }

[System.Serializable]
public class MyData { public int id; public string name; }
"""


def write_project_sources(directory, available=()):
    """Write one .cs per compiled snippet; return (files, skipped, problems).

    available names the REQUIREMENTS the build provides; skipped lists (snippet, reason) for every block not
    written (a skip directive or a missing requirement), for the caller to report.
    """
    os.makedirs(directory, exist_ok=True)
    for f in os.listdir(directory):
        if f.endswith(".cs"):
            os.remove(os.path.join(directory, f))
    snippets, problems = all_snippets()
    files, skipped = [], []
    ambient = os.path.join(directory, "Ambient.cs")
    with open(ambient, "w", encoding="utf-8") as fh:
        fh.write(AMBIENT_SOURCE)
    files.append(ambient)
    for n, sn in enumerate(snippets, 1):
        reason = sn.missing(available)
        if reason:
            skipped.append((sn, reason))
            continue
        path = os.path.join(directory, f"S{n:03d}.cs")
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(render(n, sn))
        files.append(path)
    return files, skipped, problems


def main():
    snippets, problems = all_snippets()
    for p in problems:
        print(p)
    compiled = [s for s in snippets if not s.skip]
    optional = [s for s in compiled if s.requires]
    print(f"doc_snippets: {len(snippets)} csharp blocks, {len(compiled)} compiled ({len(optional)} only with an "
          f"optional package), {len(snippets) - len(compiled)} skipped")
    for s in snippets:
        if s.skip:
            print(f"  skip {s.path}:{s.line}: {s.skip}")
    for s in optional:
        print(f"  needs {', '.join(s.requires)} {s.path}:{s.line}: {s.missing(())}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
