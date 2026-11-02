#!/usr/bin/env python3
"""Check an Autobahn fuzzingserver report against the documented expectations (#217).

  check_report.py <reports/clients/index.json> --expectations expectations.json [--partial]

Every case must be OK (behavior and behaviorClose: OK or INFORMATIONAL), or be listed in the
expectations file with the non-OK result it is documented to produce and why ("behavior" and
"behaviorClose" are one result, or a list of results for a timing-dependent case). A non-OK case
that is not listed, a listed case with a result not listed for it, and (without --partial) a listed
case that did not run all fail the gate. Exit 0 = gate passes, 1 = gate fails.
"""
import argparse
import json
import sys

PASSING = ("OK", "INFORMATIONAL")


def allowed(value):
    return tuple(value) if isinstance(value, list) else (value,)


def case_key(case_id):
    return tuple(int(p) for p in case_id.split("."))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("index")
    ap.add_argument("--expectations", required=True)
    ap.add_argument("--partial", action="store_true", help="only some case groups ran")
    args = ap.parse_args()

    with open(args.index, encoding="utf-8") as fh:
        index = json.load(fh)
    if len(index) != 1:
        sys.exit(f"check_report: expected one agent in {args.index}, found {sorted(index)}")
    agent, results = next(iter(index.items()))
    with open(args.expectations, encoding="utf-8") as fh:
        expected = json.load(fh)["cases"]

    problems, documented, counts = [], {}, {}
    for case_id in sorted(results, key=case_key):
        r = results[case_id]
        got = (r.get("behavior"), r.get("behaviorClose"))
        for b in set(got):
            counts[b] = counts.get(b, 0) + 1
        exp = expected.get(case_id)
        if exp is None:
            if got[0] not in PASSING or got[1] not in PASSING:
                problems.append(f"{case_id}: {got[0]}/{got[1]} (not documented in expectations.json)")
            continue
        want = (allowed(exp["behavior"]), allowed(exp["behaviorClose"]))
        if got[0] not in want[0] or got[1] not in want[1]:
            problems.append(f"{case_id}: {got[0]}/{got[1]}, documented as "
                            f"{'|'.join(want[0])}/{'|'.join(want[1])} ({exp['reason']})")
        else:
            documented.setdefault((got, exp["reason"]), []).append(case_id)

    if not args.partial:
        for case_id in sorted(set(expected) - set(results), key=case_key):
            problems.append(f"{case_id}: documented in expectations.json but did not run")

    print(f"check_report: agent {agent}, {len(results)} case(s); results " +
          ", ".join(f"{k}={v}" for k, v in sorted(counts.items())))
    for (got, reason), ids in documented.items():
        print(f"  documented {got[0]}/{got[1]} ({len(ids)}): {', '.join(ids)}\n      {reason}")
    for line in problems:
        print("  FAIL " + line)
    if problems:
        print(f"check_report: {len(problems)} problem(s)")
        return 1
    print("check_report: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
