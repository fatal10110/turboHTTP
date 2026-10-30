#!/usr/bin/env python3
"""Seed GitHub labels, milestones, projects and issues from .github/issue-seed/issues/.

Idempotent: re-running skips labels/milestones/projects/issues that already exist.
Issues are matched by the hidden `<!-- seed-key: KEY -->` marker in their body.

Usage:
  python3 .github/issue-seed/create_issues.py --dry-run
  python3 .github/issue-seed/create_issues.py
  python3 .github/issue-seed/create_issues.py --only TLS-01 H2-01
"""
import argparse
import glob
import json
import os
import re
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))

MILESTONES = {
    "v1.0-alpha: Release blockers":
        "Security, bootstrap, packaging and legal blockers. Nothing ships until these are closed.",
    "v1.0-beta: Protocol & correctness hardening":
        "Critical/high protocol, security and data-loss bugs in HTTP/1.1, HTTP/2, WebSocket and modules.",
    "v1.0 GA: Docs, validation & release":
        "Phase 8 docs/samples, Phase 9 device validation, Phase 17 CI/release, open validation gates.",
    "v1.1: Post-release hardening":
        "Medium/low correctness and robustness fixes, architecture cleanups, experimental features made solid.",
    "v1.2: Features & performance":
        "Deferred features (rate limiting, parallel helpers, content handlers, API ergonomics) and zero-alloc work.",
    "v2.0: Advanced protocols":
        "HTTP/2 proxy leg (22d), gRPC, WebGL, GraphQL, WebSocket over HTTP/2.",
}

PROJECT_RELEASE = "TurboHTTP v1.0 Release"
PROJECT_SECURITY = "TurboHTTP Protocol & Security"
PROJECT_ROADMAP = "TurboHTTP Post-1.0 Roadmap"
PROJECTS = {
    PROJECT_RELEASE: "Everything gating v1.0: release blockers, protocol hardening, docs, device validation, CI and release.",
    PROJECT_SECURITY: "Protocol correctness (RFC 9110/9112/9113/7541/6455/7692) and security findings across all milestones.",
    PROJECT_ROADMAP: "Post-1.0 roadmap: hardening, deferred phases and advanced protocol work.",
}
SECURITY_AREAS = {"area:tls", "area:http1", "area:http2", "area:transport", "area:proxy", "area:websocket"}

LABELS = {
    "type:bug": ("d73a4a", "Defect"),
    "type:security": ("b60205", "Security defect or hardening"),
    "type:enhancement": ("a2eeef", "New capability"),
    "type:docs": ("0075ca", "Documentation"),
    "type:chore": ("cfd3d7", "Packaging, metadata, housekeeping"),
    "type:test": ("bfd4f2", "Test coverage / validation"),
    "type:phase": ("5319e7", "Roadmap phase / sub-phase"),
    "type:plan": ("c5def5", "Plan/design document change"),
    "type:architecture": ("8a63d2", "Architecture / module boundary change"),
    "tracking": ("000000", "Milestone tracking issue"),
    "priority:P0": ("b60205", "Release blocker / critical"),
    "priority:P1": ("d93f0b", "High"),
    "priority:P2": ("fbca04", "Medium"),
    "priority:P3": ("0e8a16", "Low"),
    "source:review-2026-10": ("ededed", "Finding from the 2026-10 full review"),
    "source:roadmap": ("ededed", "Existing plan phase"),
    "platform:il2cpp": ("f9d0c4", "IL2CPP / AOT specific"),
    "needs-device-validation": ("f9d0c4", "Must be confirmed on a physical iOS/Android device"),
    "confidence:plausible": ("fef2c0", "Traced statically; needs runtime confirmation"),
}
for _area in ["tls", "http1", "http2", "transport", "proxy", "websocket", "core", "cache", "auth", "retry",
              "files", "middleware", "observability", "unity", "mobile", "json", "ratelimit", "testing",
              "editor", "packaging", "docs", "ci"]:
    LABELS["area:" + _area] = ("1d76db", "Area: " + _area)


def run(args, input_text=None, check=True):
    for attempt in range(5):
        proc = subprocess.run(args, input=input_text, capture_output=True, text=True)
        if proc.returncode == 0 or not check:
            return proc.stdout
        err = proc.stderr
        if "rate limit" in err.lower() or "secondary" in err.lower():
            wait = 30 * (attempt + 1)
            print(f"  rate limited, sleeping {wait}s", file=sys.stderr)
            time.sleep(wait)
            continue
        raise RuntimeError(f"{' '.join(args[:4])}... failed: {err.strip()}")
    raise RuntimeError(f"{' '.join(args[:4])}... failed after retries")


def gh_json(args):
    return json.loads(run(["gh"] + args) or "null")


def parse_md(path):
    """Issue blocks separated by '===ISSUE===' lines, each with a '---' front matter header."""
    text = open(path, encoding="utf-8").read()
    issues = []
    for block in re.split(r"^===ISSUE===\s*$", text, flags=re.M):
        block = block.strip()
        if not block:
            continue
        m = re.match(r"^---\n(.*?)\n---\n(.*)$", block, flags=re.S)
        if not m:
            raise ValueError(f"{path}: bad block starting {block[:60]!r}")
        meta = {}
        for line in m.group(1).splitlines():
            k, _, v = line.partition(":")
            meta[k.strip()] = v.strip()
        meta["labels"] = [s.strip() for s in meta.get("labels", "").split(",") if s.strip()]
        meta["projects"] = [s.strip() for s in meta.get("projects", "").split(";") if s.strip()]
        meta["body"] = m.group(2).strip() + "\n"
        issues.append(meta)
    return issues


def load_issues():
    issues = []
    for path in sorted(glob.glob(os.path.join(HERE, "issues", "*"))):
        if path.endswith(".json"):
            items = json.load(open(path, encoding="utf-8"))
        elif path.endswith(".md"):
            items = parse_md(path)
        else:
            continue
        for it in items:
            it["_file"] = os.path.basename(path)
        issues.extend(items)

    errors, seen = [], set()
    for it in issues:
        key = it.get("key")
        where = f"{it['_file']}:{key}"
        if not key or key in seen:
            errors.append(f"{where}: missing or duplicate key")
        seen.add(key)
        if not it.get("title") or not it.get("body"):
            errors.append(f"{where}: missing title/body")
        if it.get("milestone") not in MILESTONES:
            errors.append(f"{where}: unknown milestone {it.get('milestone')!r}")
        for lbl in it.get("labels", []):
            if lbl not in LABELS:
                errors.append(f"{where}: unknown label {lbl!r}")
        if not any(l.startswith("priority:") for l in it.get("labels", [])):
            errors.append(f"{where}: no priority label")
        for p in it.get("projects", []):
            if p not in PROJECTS:
                errors.append(f"{where}: unknown project {p!r}")
    for it in issues:
        for ref in re.findall(r"\[\[([^\]]+)\]\]", it.get("body", "")):
            if ref not in seen:
                errors.append(f"{it['_file']}:{it.get('key')}: unknown cross-reference [[{ref}]]")
    if errors:
        raise SystemExit("Validation failed:\n  " + "\n  ".join(errors))
    return issues


def projects_for(it):
    if it.get("projects"):
        return list(it["projects"])
    result = [PROJECT_RELEASE if it["milestone"].startswith("v1.0") else PROJECT_ROADMAP]
    labels = set(it["labels"])
    if "type:security" in labels or ("type:bug" in labels and labels & SECURITY_AREAS):
        result.append(PROJECT_SECURITY)
    return result


def marker(key):
    return f"<!-- seed-key: {key} -->"


def build_tracking(issues):
    """One tracking issue per milestone, body filled after children exist."""
    tracking = []
    for title in MILESTONES:
        children = [i for i in issues if i["milestone"] == title]
        if not children:
            continue
        slug = re.sub(r"[^a-z0-9]+", "-", title.split(":")[0].lower()).strip("-")
        tracking.append({
            "key": "TRACK-" + slug,
            "title": f"[Tracking] {title}",
            "milestone": title,
            "labels": ["tracking", "priority:P1"],
            "projects": [PROJECT_RELEASE if title.startswith("v1.0") else PROJECT_ROADMAP],
            "_children": [c["key"] for c in children],
            "body": f"## {title}\n\n{MILESTONES[title]}\n\n## Issues\n\n" +
                    "\n".join(f"- [ ] [[{c['key']}]] {c['title']}" for c in children) + "\n",
        })
    return tracking


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--repo", help="owner/name (default: current gh repo)")
    ap.add_argument("--only", nargs="*", help="only create these keys")
    ap.add_argument("--skip-projects", action="store_true")
    ap.add_argument("--delay", type=float, default=2.0, help="seconds between issue creations")
    args = ap.parse_args()

    issues = load_issues()
    issues += build_tracking(issues)
    if args.only:
        issues = [i for i in issues if i["key"] in set(args.only)]

    by_milestone = {}
    for it in issues:
        by_milestone.setdefault(it["milestone"], []).append(it)
    print(f"{len(issues)} issues")
    for m in MILESTONES:
        print(f"  {len(by_milestone.get(m, [])):4d}  {m}")
    proj_counts = {}
    for it in issues:
        for p in projects_for(it):
            proj_counts[p] = proj_counts.get(p, 0) + 1
    for p, n in proj_counts.items():
        print(f"  {n:4d}  project: {p}")
    if args.dry_run:
        for it in issues:
            print(f"  {it['key']:<14} {it['milestone'][:10]:<10} {it['title']}")
        return

    repo = args.repo or gh_json(["repo", "view", "--json", "nameWithOwner"])["nameWithOwner"]
    owner = repo.split("/")[0]

    print("labels...")
    for name, (color, desc) in LABELS.items():
        run(["gh", "label", "create", name, "--repo", repo, "--color", color, "--description", desc, "--force"])

    print("milestones...")
    existing_ms = {m["title"]: m["number"] for m in
                   gh_json(["api", f"repos/{repo}/milestones?state=all&per_page=100"])}
    for title, desc in MILESTONES.items():
        if title not in existing_ms:
            run(["gh", "api", "-X", "POST", f"repos/{repo}/milestones", "-f", f"title={title}",
                 "-f", f"description={desc}"])

    project_numbers = {}
    if not args.skip_projects:
        print("projects...")
        existing = {p["title"]: p["number"] for p in
                    gh_json(["project", "list", "--owner", owner, "--limit", "200", "--format", "json"])["projects"]}
        for title, desc in PROJECTS.items():
            if title not in existing:
                existing[title] = gh_json(["project", "create", "--owner", owner, "--title", title,
                                           "--format", "json"])["number"]
                run(["gh", "project", "edit", str(existing[title]), "--owner", owner,
                     "--description", desc])
                run(["gh", "project", "link", str(existing[title]), "--owner", owner, "--repo", repo],
                    check=False)
            project_numbers[title] = existing[title]

    print("existing issues...")
    existing_issues = gh_json(["issue", "list", "--repo", repo, "--state", "all", "--limit", "2000",
                               "--json", "number,url,body"])
    key_to_issue = {}
    for e in existing_issues:
        m = re.search(r"<!-- seed-key: (\S+) -->", e.get("body") or "")
        if m:
            key_to_issue[m.group(1)] = {"number": e["number"], "url": e["url"],
                                        "unresolved": "[[" in (e.get("body") or "")}

    state_path = os.path.join(HERE, "created.json")
    created = json.load(open(state_path)) if os.path.exists(state_path) else {}

    def body_text(it):
        return marker(it["key"]) + "\n" + it["body"]

    for it in issues:
        key = it["key"]
        if key in key_to_issue:
            print(f"  skip {key} (#{key_to_issue[key]['number']})")
        else:
            with tempfile.NamedTemporaryFile("w", suffix=".md", delete=False, encoding="utf-8") as f:
                f.write(body_text(it))
                body_file = f.name
            try:
                url = run(["gh", "issue", "create", "--repo", repo, "--title", it["title"],
                           "--body-file", body_file, "--milestone", it["milestone"],
                           "--label", ",".join(it["labels"])]).strip().splitlines()[-1]
            finally:
                os.unlink(body_file)
            number = int(url.rstrip("/").rsplit("/", 1)[1])
            key_to_issue[key] = {"number": number, "url": url, "unresolved": True}
            print(f"  created {key} -> #{number}")
            time.sleep(args.delay)
        created[key] = {"number": key_to_issue[key]["number"], "url": key_to_issue[key]["url"]}
        json.dump(created, open(state_path, "w"), indent=2, sort_keys=True)

        for p in projects_for(it):
            if p in project_numbers:
                run(["gh", "project", "item-add", str(project_numbers[p]), "--owner", owner,
                     "--url", key_to_issue[key]["url"]])

    print("resolving [[KEY]] cross-references...")
    for it in issues:
        # Only touch bodies that still carry unresolved refs, so manual edits survive re-runs.
        if "[[" not in it["body"] or not key_to_issue[it["key"]].get("unresolved"):
            continue

        def repl(m):
            ref = key_to_issue.get(m.group(1))
            return f"#{ref['number']}" if ref else m.group(0)

        resolved = re.sub(r"\[\[([A-Za-z0-9.\-]+)\]\]", repl, body_text(it))
        with tempfile.NamedTemporaryFile("w", suffix=".md", delete=False, encoding="utf-8") as f:
            f.write(resolved)
            body_file = f.name
        try:
            run(["gh", "issue", "edit", str(key_to_issue[it["key"]]["number"]), "--repo", repo,
                 "--body-file", body_file])
        finally:
            os.unlink(body_file)

    print(f"done: {len(issues)} issues; state in {state_path}")


if __name__ == "__main__":
    main()
