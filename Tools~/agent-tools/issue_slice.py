#!/usr/bin/env python3
"""Print the part of a turboHTTP issue that a lane needs, cached across agents.

turboHTTP issues follow one template (Summary, Location, Problem, Spec reference,
Suggested fix, Acceptance criteria, Context); phase issues carry a scope checklist
and the phase document path. This prints:

  issue_slice.py <n>              title, state, milestone, labels, the body, the newest
                                  comments, the referenced issues (#N: state + title) and
                                  the repository docs the issue names (paths only)
  issue_slice.py <n> --section S  one section in full (S matches a heading, ignoring case)
  issue_slice.py <n> --full       the whole body and every comment
  issue_slice.py <n> --no-refs    skip the referenced-issue lookups

Results are cached for --ttl minutes (default 30) under the waves root, so the
implementation and the review agent of a lane fetch an issue once. --refresh refetches.
"""

import argparse
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import wave_mail  # noqa: E402

REPO = wave_mail.GH_REPO
CACHE = Path(os.environ.get("WAVES_ROOT", wave_mail.DEFAULT_ROOT)) / ".cache"
GH = os.environ.get("WAVES_GH", "gh")
COMMENT_CHARS = 900
SEED_MARKER = re.compile(r"<!-- seed-key: [^>]+ -->\n?")


def gh_json(args):
    r = subprocess.run([GH, *args], capture_output=True, text=True, timeout=120)
    if r.returncode != 0:
        raise SystemExit(f"gh {' '.join(args)}: {(r.stderr or r.stdout).strip()[-300:]}")
    return json.loads(r.stdout or "null")


def cached(name, ttl, refresh, fetch):
    p = CACHE / name
    if not refresh and p.exists() and time.time() - p.stat().st_mtime < ttl * 60:
        return json.loads(p.read_text(encoding="utf-8"))
    data = fetch()
    CACHE.mkdir(parents=True, exist_ok=True)
    tmp = p.with_name(f".{p.name}.{os.getpid()}")
    tmp.write_text(json.dumps(data), encoding="utf-8")
    os.replace(tmp, p)
    return data


def fetch_issue(n, ttl, refresh):
    return cached(f"issue-{n}.json", ttl, refresh, lambda: gh_json(
        ["issue", "view", str(n), "-R", REPO, "--json", "number,title,state,milestone,labels,body,comments"]))


def sections(body):
    """[(heading, [lines])] split on '## ' headings; the preamble has heading ''."""
    out = [("", [])]
    for line in body.split("\n"):
        if line.startswith("## "):
            out.append((line[3:].strip(), []))
        else:
            out[-1][1].append(line)
    return out


def comments_tail(comments, n, full):
    if full:
        return [f"--- comment {c.get('createdAt', '')[:10]} {c.get('author', {}).get('login', '')}:\n{c.get('body', '')}" for c in comments]
    out = []
    if len(comments) > n:
        out.append(f"({len(comments) - n} older comments not shown; --full shows all)")
    for c in comments[-n:] if n else []:
        b = c.get("body", "")
        cut = b if len(b) <= COMMENT_CHARS else b[:COMMENT_CHARS] + f" ... [{len(b) - COMMENT_CHARS} more chars: --full]"
        out.append(f"--- comment {c.get('createdAt', '')[:10]}:\n{cut}")
    return out


def referenced(body, own):
    return sorted({int(x) for x in re.findall(r"(?<![\w/])#(\d+)\b", body)} - {own})


def doc_paths(body):
    return sorted(set(re.findall(r"`((?:Development|Runtime|Tests|Editor|Documentation~|Samples~|Plugins)/[^`\s:]+)", body)))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("issue", type=int)
    ap.add_argument("--section")
    ap.add_argument("--full", action="store_true")
    ap.add_argument("--no-refs", action="store_true")
    ap.add_argument("--comments", type=int, default=3, help="newest comments to show")
    ap.add_argument("--ttl", type=float, default=30)
    ap.add_argument("--refresh", action="store_true")
    a = ap.parse_args(argv)

    iss = fetch_issue(a.issue, a.ttl, a.refresh)
    body = SEED_MARKER.sub("", iss.get("body") or "")
    comments = iss.get("comments") or []
    ms = (iss.get("milestone") or {}).get("title", "")
    labels = ", ".join(l.get("name", "") for l in iss.get("labels") or [])
    print(f"#{iss['number']} [{iss.get('state')}] {iss.get('title')}  ({ms})" + (f"  labels: {labels}" if labels else ""))

    if a.full:
        print(body)
        print("\n".join(comments_tail(comments, 0, True)))
        return
    if a.section:
        hit = [(h, l) for h, l in sections(body) if a.section.lower() in h.lower()]
        if not hit:
            print(f"no section matching {a.section!r}; sections: {', '.join(h for h, _ in sections(body) if h)}")
        for h, l in hit:
            print(f"## {h}\n" + "\n".join(l).strip())
        return

    print(body.strip())
    tail = comments_tail(comments, a.comments, False)
    if tail:
        print("## Newest comments\n" + "\n".join(tail))
    docs = doc_paths(body)
    if docs:
        print("## Repository paths named\n" + "\n".join(docs))
    if a.no_refs:
        return
    refs = referenced(body, a.issue)[:15]
    if refs:
        print("## Referenced issues")
        for n in refs:
            try:
                r = fetch_issue(n, a.ttl, a.refresh)
                print(f"#{n} [{r.get('state')}] {r.get('title')}")
            except SystemExit as e:
                print(f"#{n} (lookup failed: {e})")


if __name__ == "__main__":
    main()
