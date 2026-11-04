#!/usr/bin/env python3
"""Search the cached fatal10110/turboHTTP closed-PR cache without dumping it.

The cache (<waves root>/.cache/github/prs.json, outside the repository) is built on first
use and rebuilt with --sync.

PRs are already closed, so their state never changes; the only field that may
change after closure is labels (e.g. marking a PR as handled). Matching PRs are
therefore re-verified live for their current labels only, and the cache is
rewritten with the fresh label set.
"""

import argparse
import json
import re
import os
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import wave_mail  # noqa: E402

DEFAULT_CACHE = Path(os.environ.get("WAVES_ROOT", wave_mail.DEFAULT_ROOT)) / ".cache/github/prs.json"
REPOSITORY = wave_mail.GH_REPO
PR_FIELDS = "number,title,author,labels,body,mergedAt,additions,deletions,closingIssuesReferences,comments,reviews"
REVIEW_RANK = {"none": 0, "bot-only": 1, "self": 2, "human": 3}


def text(value):
    return value if isinstance(value, str) else ""


def pr_fields(pr):
    reviews = "\n".join(text(review.get("login")) for review in pr.get("reviews") or [])
    comments = "\n".join(text(comment.get("body")) for comment in pr.get("comments") or [])
    return [
        ("title", text(pr.get("title")), 8),
        ("author", text(pr.get("author")), 6),
        ("labels", " ".join(text(label.get("name")) for label in pr.get("labels") or []), 5),
        ("body", text(pr.get("body")), 2),
        ("comments", comments, 1),
        ("reviews", reviews, 1),
        ("number", str(pr.get("number", "")), 8),
    ]


def score(pr, terms):
    fields = [(name, value.casefold(), weight) for name, value, weight in pr_fields(pr)]
    matched_sources = set()
    total = 0
    for term in terms:
        hits = [(name, weight) for name, value, weight in fields if term in value]
        if not hits:
            return None
        matched_sources.update(name for name, _ in hits)
        total += sum(weight for _, weight in hits)
    return total, matched_sources


def matching_comments(pr, terms):
    matches = []
    for comment in pr.get("comments") or []:
        body = text(comment.get("body"))
        if any(term in body.casefold() for term in terms):
            author = text(comment.get("login")) or "unknown"
            snippet = re.sub(r"\s+", " ", body).strip()[:180]
            matches.append((author, snippet))
    return matches


def refresh_labels(pr):
    result = subprocess.run(
        ["gh", "pr", "view", str(pr["number"]), "--repo", REPOSITORY, "--json", "labels"],
        check=True,
        capture_output=True,
        text=True,
    )
    pr["labels"] = json.loads(result.stdout).get("labels") or []


def review_status(author, reviews):
    logins = [r for r in reviews if r]
    if not logins:
        return "none"
    if any(not r.endswith("[bot]") and r != author for r in logins):
        return "human"
    if all(r.endswith("[bot]") for r in logins):
        return "bot-only"
    return "self"


def normalize(pr):
    author = text((pr.get("author") or {}).get("login"))
    reviews = [text((r.get("author") or {}).get("login")) for r in pr.get("reviews") or []]
    return {
        "number": pr.get("number"),
        "title": pr.get("title"),
        "author": author,
        "labels": pr.get("labels") or [],
        "body": pr.get("body"),
        "mergedAt": pr.get("mergedAt"),
        "additions": pr.get("additions"),
        "deletions": pr.get("deletions"),
        "fixedIssues": [i.get("number") for i in pr.get("closingIssuesReferences") or []],
        "comments": [{"login": text((c.get("author") or {}).get("login")), "body": text(c.get("body"))} for c in pr.get("comments") or []],
        "reviews": [{"login": r} for r in reviews],
        "reviewStatus": review_status(author, reviews),
    }


def sync(cache):
    result = subprocess.run(
        ["gh", "pr", "list", "--repo", REPOSITORY, "--state", "closed", "--limit", "1000", "--json", PR_FIELDS],
        check=True,
        capture_output=True,
        text=True,
    )
    prs = [normalize(pr) for pr in json.loads(result.stdout)]
    cache.parent.mkdir(parents=True, exist_ok=True)
    cache.write_text(json.dumps(prs, indent=2) + "\n", encoding="utf-8")
    return prs


def search(prs, terms, status, reviews, label, no_label, ref, min_size, limit):
    ref = int(ref) if ref else None
    results = []
    for pr in prs:
        if status != "all":
            merged = bool(pr.get("mergedAt"))
            if status == "merged" and not merged:
                continue
            if status == "unmerged" and merged:
                continue
        if reviews and pr.get("reviewStatus") not in reviews:
            continue
        labels = [text(item.get("name")).casefold() for item in pr.get("labels") or []]
        if label and label.casefold() not in labels:
            continue
        if no_label and no_label.casefold() in labels:
            continue
        if ref is not None:
            referenced = set(pr.get("fixedIssues") or []) | {
                item.get("n") for item in pr.get("refs") or [] if isinstance(item, dict)
            }
            if ref not in referenced:
                continue
        if min_size and (pr.get("additions") or 0) + (pr.get("deletions") or 0) < min_size:
            continue
        scored = score(pr, terms)
        if scored:
            results.append((scored[0], pr, scored[1]))
    rank = {status: i for i, status in enumerate(REVIEW_RANK)}
    results.sort(
        key=lambda item: (
            -item[0],
            rank.get(item[1].get("reviewStatus"), 99),
            -item[1].get("number", 0),
        )
    )
    return results[:limit]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("query", nargs="*", help="terms that must appear across cached PR fields")
    parser.add_argument("--cache", type=Path, default=DEFAULT_CACHE)
    parser.add_argument("--sync", action="store_true", help="rebuild the cache from GitHub first")
    parser.add_argument("--status", choices=("all", "merged", "unmerged"), default="all")
    parser.add_argument("--review", action="append", choices=tuple(REVIEW_RANK), default=None)
    parser.add_argument("--label", help="case-insensitive exact label (e.g. handled)")
    parser.add_argument("--no-label", dest="no_label", help="exclude PRs with this exact label (e.g. find non-handled)")
    parser.add_argument("--ref", help="PR must reference this issue/PR number")
    parser.add_argument("--min-size", type=int, help="minimum additions + deletions")
    parser.add_argument("--limit", type=int, default=10)
    args = parser.parse_args()

    if not 1 <= args.limit <= 50:
        parser.error("--limit must be between 1 and 50")

    terms = [term.casefold() for term in re.findall(r"[\w.-]+", " ".join(args.query))]
    filters = (args.ref, args.label, args.no_label, args.review, args.min_size)
    if not terms and not any(filters) and not args.sync:
        parser.error("query must contain at least one searchable term (or pass a filter)")

    try:
        prs = sync(args.cache) if args.sync or not args.cache.exists() else json.loads(args.cache.read_text(encoding="utf-8"))
    except (OSError, subprocess.CalledProcessError, json.JSONDecodeError) as error:
        parser.error(f"could not build the PR cache from {REPOSITORY}: {error}")
    if not terms and not any(filters):
        print(f"cache synced: {len(prs)} closed PRs in {args.cache}")
        return
    results = search(prs, terms, args.status, args.review, args.label, args.no_label, args.ref, args.min_size, args.limit)
    if not results:
        print("no cached PRs matched")
        return

    # PRs are closed; only labels may have changed since the snapshot.
    for _, pr, _ in results:
        try:
            refresh_labels(pr)
        except (OSError, subprocess.CalledProcessError, json.JSONDecodeError) as error:
            parser.error(f"could not refresh labels for PR #{pr['number']} from {REPOSITORY}: {error}")
    args.cache.write_text(json.dumps(prs, indent=2) + "\n", encoding="utf-8")
    results = search(prs, terms, args.status, args.review, args.label, args.no_label, args.ref, args.min_size, args.limit)
    if not results:
        print("no cached PRs matched")
        return

    for _, pr, sources in results:
        merged = "MERGED" if pr.get("mergedAt") else "CLOSED"
        size = (pr.get("additions") or 0) + (pr.get("deletions") or 0)
        source_list = ", ".join(sorted(sources))
        print(
            f"#{pr['number']} [{merged}] review:{pr.get('reviewStatus')} "
            f"+{pr.get('additions')}/-{pr.get('deletions')} ({size}) — {pr.get('title')} "
            f"(matched: {source_list})"
        )
        labels = [text(label.get("name")) for label in pr.get("labels") or []]
        if labels:
            print(f"  labels: {', '.join(labels)}")
        for author, snippet in matching_comments(pr, terms)[:1]:
            print(f"  comment by {author}: {snippet}")


if __name__ == "__main__":
    main()
