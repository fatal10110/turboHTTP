#!/usr/bin/env python3
"""Several small code lookups in one call, with capped output.

Every tool call of a long-lived agent re-sends its whole context, so ten
one-line lookups cost ten times what one batched call costs. Give all the
lookups you need now as arguments:

  FILE:A-B                   lines A..B of FILE (numbered)
  cs:PATH:Name[,Name...]     C# declarations by name in a file or directory (recursive *.cs):
                             a type (class/struct/interface/enum/record/delegate), or a member
                             (method, constructor, property, indexer `this`, field, event, const);
                             Type.Member scopes the member to that type. Doc comments (///) and
                             attributes are included; bodies by brace matching, `=>` bodies to `;`.
  rg:PATH:PATTERN            rg -n matches of PATTERN under PATH (at most --rg-max lines)
  rgl:PATH:PATTERN           files under PATH matching PATTERN, with match counts
  rgg:GLOB:PATH:PATTERN      rg -n restricted to GLOB

Relative paths resolve against --root (default: the current directory).
Prints "### STOP" first when the run that owns the worktree is stopping.
"""

import argparse
import os
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import wave_mail  # noqa: E402


def cap(lines, n, hint):
    if len(lines) <= n:
        return lines
    return lines[:n] + [f"... ({len(lines) - n} more lines cut; {hint})"]


def read_lines(path):
    return Path(path).read_text(encoding="utf-8-sig", errors="replace").split("\n")


def do_range(path, a, b, maxl):
    try:
        text = read_lines(path)
    except OSError as e:
        return [f"error: {e}"]
    a = max(1, a)
    b = min(len(text), b)
    rows = [f"{i}\t{text[i - 1]}" for i in range(a, b + 1)]
    return cap(rows, maxl, f"read a narrower range than {a}-{b}")


MODS = r"(?:(?:public|private|protected|internal|static|readonly|virtual|override|abstract|sealed|async|unsafe|extern|new|partial|const|volatile|event|ref|fixed)\s+)*"
TYPE_DECL = r"^\s*" + MODS + r"(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+{name}\b"
DELEGATE_DECL = r"^\s*" + MODS + r"delegate\s+[\w<>\[\],.?() ]+\s+{name}\s*[<(]"
# A constructor: with a modifier, or bare (implicitly private) only when the line has no ';'
# (a bare `Name(...);` line is a call).
CTOR_DECL = r"^\s*(?:(?:public|private|protected|internal|static|unsafe|extern)\s+)+{name}\s*\("
BARE_CTOR_DECL = r"^\s*{name}\s*\([^;]*$"
MEMBER_DECL = r"^\s*" + MODS + r"(?!return\b|await\b|throw\b|yield\b|var\b|else\b|case\b|goto\b)[\w<>\[\],.?()]+(?:\s*[\[\]?]+)?\s+(?:[\w<>.]+\.)?{name}\s*(?:<[^>()]*>)?\s*(?:\(|\{{|=>|=(?!=)|;|$)"
STATEMENT = re.compile(r"^\s*(?:return|await|throw|yield|if|while|for|foreach|switch|using|lock|else|case|var)\b")


def strip_code(line):
    """Drop string/char literals and // comments so brace counting ignores them."""
    return re.sub(r'@"(?:""|[^"])*"|\$?"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|//.*', "", line)


def decl_end(text, i):
    """Last line of the declaration starting at line i."""
    depth, seen = 0, False
    for j in range(i, len(text)):
        code = strip_code(text[j])
        if not seen:
            arrow, brace, semi = code.find("=>"), code.find("{"), code.find(";")
            if arrow >= 0 and (brace < 0 or arrow < brace):
                for k in range(j, len(text)):  # expression body: up to the terminating ';'
                    if strip_code(text[k]).rstrip().endswith(";"):
                        return k
                return j
            if semi >= 0 and brace < 0:
                return j  # abstract/interface member, field, event, auto-less declaration
        depth += code.count("{") - code.count("}")
        seen = seen or "{" in code
        if seen and depth <= 0:
            return j
    return len(text) - 1


def doc_start(text, i):
    start = i
    while start > 0 and text[start - 1].strip().startswith(("///", "[", "//")):
        start -= 1
    return start


def find_decls(text, name, within=None):
    lo, hi = within or (0, len(text) - 1)
    pats = [re.compile(p.format(name=re.escape(name))) for p in (TYPE_DECL, DELEGATE_DECL, CTOR_DECL, BARE_CTOR_DECL, MEMBER_DECL)]
    if name == "this":  # indexer
        pats = [re.compile(r"^\s*" + MODS + r"[\w<>\[\],.?]+\s+this\s*\[")]
    hits = []
    for i in range(lo, hi + 1):
        line = text[i]
        if STATEMENT.match(line) or line.strip().startswith(("//", "*", "/*")):
            continue
        if any(p.search(line) for p in pats):
            hits.append(i)
    return hits


def cs_files(path):
    p = Path(path)
    if p.is_file():
        return [p]
    if not p.is_dir():
        return []
    return sorted(x for x in p.rglob("*.cs") if "/Library/" not in str(x) and "/obj/" not in str(x))


def candidate_files(path, word):
    """Files under path mentioning word (rg when available, else a Python scan)."""
    files = cs_files(path)
    if Path(path).is_file():
        return files
    try:
        r = subprocess.run(["rg", "-l", "-w", "-g", "*.cs", "-e", word, str(path)], capture_output=True, text=True)
        if r.returncode in (0, 1):
            return sorted(Path(x) for x in r.stdout.split("\n") if x)
    except FileNotFoundError:
        pass
    rx = re.compile(r"\b" + re.escape(word) + r"\b")
    return [f for f in files if rx.search(f.read_text(encoding="utf-8", errors="replace"))]


def do_cs(path, names, maxl):
    out = []
    for name in names:
        type_name, _, member = name.rpartition(".")
        found = 0
        for f in candidate_files(path, member):
            try:
                text = read_lines(f)
            except OSError as e:
                out.append(f"error: {e}")
                continue
            scopes = [None]
            if type_name:
                scopes = [(t, decl_end(text, t)) for t in find_decls(text, type_name.split(".")[-1])
                          if re.search(TYPE_DECL.format(name=re.escape(type_name.split(".")[-1])), text[t])]
            for scope in scopes:
                for i in find_decls(text, member, scope):
                    if scope and i == scope[0]:
                        continue
                    start, end = doc_start(text, i), decl_end(text, i)
                    found += 1
                    out.append(f"== {f}:{start + 1}-{end + 1} ({name})")
                    out += cap([f"{k + 1}\t{text[k]}" for k in range(start, end + 1)], maxl,
                               f"read {f}:{start + 1 + maxl}-{end + 1}")
        if not found:
            out.append(f"== {name}: no declaration under {path}")
    return out


def do_rg(path, pattern, glob, files_only, maxl):
    args = ["rg", "--no-heading", "--color", "never"]
    args += ["-c"] if files_only else ["-n", "-m", "20"]
    if glob:
        args += ["-g", glob]
    args += ["-e", pattern, path]
    r = subprocess.run(args, capture_output=True, text=True)
    lines = [l[:300] for l in r.stdout.rstrip("\n").split("\n") if l]
    if not lines:
        return [r.stderr.strip() or "(no matches)"]
    return cap(lines, maxl, "narrow PATH or PATTERN, or use rgl: first")


def resolve(root, p):
    return p if os.path.isabs(p) else str(Path(root) / p)


def run_spec(spec, root, a):
    m = re.fullmatch(r"(.+):(\d+)-(\d+)", spec)
    kind, _, rest = spec.partition(":")
    if kind == "cs" and rest:
        path, _, names = rest.rpartition(":")
        return do_cs(resolve(root, path), [n for n in names.split(",") if n], a.max)
    if kind in ("rg", "rgl") and rest:
        path, _, pattern = rest.partition(":")
        return do_rg(resolve(root, path), pattern, None, kind == "rgl", a.rg_max)
    if kind == "rgg" and rest:
        glob, _, rest2 = rest.partition(":")
        path, _, pattern = rest2.partition(":")
        return do_rg(resolve(root, path), pattern, glob, False, a.rg_max)
    if m:
        return do_range(resolve(root, m.group(1)), int(m.group(2)), int(m.group(3)), a.max)
    return [f"error: unknown spec {spec!r} (see --help)"]


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("specs", nargs="+")
    p.add_argument("--root", default=os.getcwd())
    p.add_argument("--max", type=int, default=150, help="line cap per range or declaration")
    p.add_argument("--rg-max", type=int, default=40, help="line cap per rg spec")
    p.add_argument("--total", type=int, default=600, help="line cap for the whole output")
    a = p.parse_args(argv)
    lines = []
    stop = wave_mail.stop_banner([a.root] + re.findall(r"(/[^:\s]+)", " ".join(a.specs)))
    if stop:
        lines.append(stop)
    for spec in a.specs:
        lines.append(f"### {spec}")
        lines += run_spec(spec, a.root, a)
    print("\n".join(cap(lines, a.total, "split the lookups over two calls")))


if __name__ == "__main__":
    main()
