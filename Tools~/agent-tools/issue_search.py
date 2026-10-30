#!/usr/bin/env python3
"""Search the cached fatal10110/turboHTTP issues without dumping the cache.

The cache (<waves root>/.cache/github/issues.json, outside the repository) is built on
first use and rebuilt with --sync. Matching issues are refreshed live before printing."""

import argparse
import json
import re
import os
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import wave_mail  # noqa: E402

DEFAULT_CACHE = Path(os.environ.get("WAVES_ROOT", wave_mail.DEFAULT_ROOT)) / ".cache/github/issues.json"
REPOSITORY = wave_mail.GH_REPO
ISSUE_FIELDS = "number,title,state,body,comments,labels,milestone,url"


def text(value):
    return value if isinstance(value, str) else ""


def issue_fields(issue):
    comments = issue.get("comments") or []
    return [
        ("title", text(issue.get("title")), 8),
        ("labels", " ".join(text(label.get("name")) for label in issue.get("labels") or []), 5),
        ("milestone", text((issue.get("milestone") or {}).get("title")), 4),
        ("body", text(issue.get("body")), 2),
        ("comments", "\n".join(text(comment.get("body")) for comment in comments), 1),
        ("number", str(issue.get("number", "")), 8),
    ]


def score(issue, terms):
    fields = [(name, value.casefold(), weight) for name, value, weight in issue_fields(issue)]
    matched_sources = set()
    total = 0
    for term in terms:
        hits = [(name, weight) for name, value, weight in fields if term in value]
        if not hits:
            return None
        matched_sources.update(name for name, _ in hits)
        total += sum(weight for _, weight in hits)
    return total, matched_sources


def matching_comments(issue, terms):
    matches = []
    for comment in issue.get("comments") or []:
        body = text(comment.get("body"))
        if any(term in body.casefold() for term in terms):
            author = text((comment.get("author") or {}).get("login")) or "unknown"
            snippet = re.sub(r"\s+", " ", body).strip()[:180]
            matches.append((author, snippet))
    return matches


def refresh_issue(issue):
    result = subprocess.run(
        ["gh", "issue", "view", str(issue["number"]), "--repo", REPOSITORY, "--json", ISSUE_FIELDS],
        check=True,
        capture_output=True,
        text=True,
    )
    issue.clear()
    issue.update(json.loads(result.stdout))


def sync(cache):
    result = subprocess.run(
        ["gh", "issue", "list", "--repo", REPOSITORY, "--state", "all", "--limit", "2000", "--json", ISSUE_FIELDS],
        check=True,
        capture_output=True,
        text=True,
    )
    issues = json.loads(result.stdout)
    cache.parent.mkdir(parents=True, exist_ok=True)
    cache.write_text(json.dumps(issues, indent=2) + "\n", encoding="utf-8")
    return issues


def search(issues, terms, state, milestone, label, limit):
    results = []
    for issue in issues:
        if state != "all" and text(issue.get("state")).casefold() != state:
            continue
        if milestone and milestone.casefold() not in text((issue.get("milestone") or {}).get("title")).casefold():
            continue
        labels = [text(item.get("name")).casefold() for item in issue.get("labels") or []]
        if label and label.casefold() not in labels:
            continue
        scored = score(issue, terms)
        if scored:
            results.append((scored[0], issue, scored[1]))
    results.sort(key=lambda item: (-item[0], item[1].get("state") != "OPEN", -item[1].get("number", 0)))
    return results[:limit]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("query", nargs="*", help="terms that must appear across cached issue fields")
    parser.add_argument("--sync", action="store_true", help="rebuild the cache from GitHub first")
    parser.add_argument("--cache", type=Path, default=DEFAULT_CACHE)
    parser.add_argument("--state", choices=("all", "open", "closed"), default="all")
    parser.add_argument("--milestone", help="case-insensitive milestone substring")
    parser.add_argument("--label", help="case-insensitive exact label")
    parser.add_argument("--limit", type=int, default=10)
    args = parser.parse_args()

    if not 1 <= args.limit <= 50:
        parser.error("--limit must be between 1 and 50")

    terms = [term.casefold() for term in re.findall(r"[\w.-]+", " ".join(args.query))]
    if not terms and not args.sync:
        parser.error("query must contain at least one searchable term")

    try:
        issues = sync(args.cache) if args.sync or not args.cache.exists() else json.loads(args.cache.read_text(encoding="utf-8"))
    except (OSError, subprocess.CalledProcessError, json.JSONDecodeError) as error:
        parser.error(f"could not build the issue cache from {REPOSITORY}: {error}")
    if not terms:
        print(f"cache synced: {len(issues)} issues in {args.cache}")
        return
    results = search(issues, terms, args.state, args.milestone, args.label, args.limit)
    if not results:
        print("no cached issues matched")
        return

    try:
        for _, issue, _ in results:
            refresh_issue(issue)
    except (OSError, subprocess.CalledProcessError, json.JSONDecodeError) as error:
        parser.error(f"could not refresh issue from {REPOSITORY}: {error}")
    args.cache.write_text(json.dumps(issues, indent=2) + "\n", encoding="utf-8")
    results = search(issues, terms, args.state, args.milestone, args.label, args.limit)
    if not results:
        print("no cached issues matched")
        return

    for _, issue, sources in results:
        milestone = text((issue.get("milestone") or {}).get("title")) or "No milestone"
        source_list = ", ".join(sorted(sources))
        print(f"#{issue['number']} [{issue['state']}] {milestone} — {issue['title']} (matched: {source_list})")
        for author, snippet in matching_comments(issue, terms)[:1]:
            print(f"  comment by {author}: {snippet}")


if __name__ == "__main__":
    main()
