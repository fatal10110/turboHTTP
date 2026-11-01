#!/usr/bin/env python3
"""Summarises a Unity Test Runner NUnit3 results file for run-tests.sh (#151, #190).

Prints the totals, the failed cases and every skipped/ignored case with its reason, then applies the
skip gates:
  * every skipped case must carry a reason with an issue reference (#123) or the [env] marker that
    TestRequirements.IgnoreEnvironment adds; anything else is an unexplained skip;
  * an unfiltered run (the whole platform suite) may not skip more cases than the platform's entry in
    the baseline file (Tools~/unity/skip-baseline.txt). A filtered run covers part of the suite, so the
    count is only listed.

Usage: summarize_results.py <results.xml> <EditMode|PlayMode> <filtered:0|1> <baseline-file>
Exit: 0 gates pass, 4 a skip gate failed. (Test failures are reported by the caller via Unity's exit code.)
"""
import re
import sys
import xml.etree.ElementTree as ET

SKIP_GATE_EXIT = 4
REASON_OK = re.compile(r"#\d+|\[env\]")


def read_baseline(path, platform):
    """Max allowed skipped cases for the platform, or None when the file has no entry."""
    try:
        with open(path, encoding="utf-8") as fh:
            for line in fh:
                line = line.split("#", 1)[0].strip()
                parts = line.split()
                if len(parts) == 2 and parts[0] == platform:
                    return int(parts[1])
    except FileNotFoundError:
        return None
    return None


def skipped_cases(root):
    """(fullname, reason) of every skipped or ignored test-case."""
    cases = []
    for case in root.iter("test-case"):
        if case.get("result") in ("Skipped", "Ignored") or case.get("label") == "Ignored":
            reason = (case.findtext("reason/message") or case.findtext("failure/message") or "").strip()
            cases.append((case.get("fullname") or case.get("name") or "?", " ".join(reason.split())))
    return cases


def summarize(xml_path, platform, filtered, baseline_path, out=sys.stdout):
    run = ET.parse(xml_path).getroot()
    a = run.attrib
    print(f"run-tests: total={a.get('total')} passed={a.get('passed')} failed={a.get('failed')} "
          f"skipped={a.get('skipped')} inconclusive={a.get('inconclusive')} duration={a.get('duration')}s", file=out)
    for case in run.iter("test-case"):
        if case.get("result") == "Failed":
            msg = (case.findtext("failure/message") or "").strip().splitlines()
            print(f"  FAIL {case.get('fullname')}: {msg[0] if msg else ''}", file=out)

    skipped = skipped_cases(run)
    gate_failed = False
    for name, reason in skipped:
        explained = bool(REASON_OK.search(reason))
        print(f"  SKIP {name}: {reason or '(no reason)'}" + ("" if explained else "   <-- no #issue or [env] marker"), file=out)
        gate_failed = gate_failed or not explained
    if gate_failed:
        print("run-tests: SKIP GATE FAILED: a skipped test has no issue reference (#123) or [env] marker (#190)", file=out)

    baseline = read_baseline(baseline_path, platform)
    if filtered:
        print(f"run-tests: skipped={len(skipped)} (filtered run; the {platform} baseline applies to unfiltered runs only)", file=out)
    elif baseline is None:
        print(f"run-tests: SKIP GATE FAILED: no {platform} entry in {baseline_path}", file=out)
        gate_failed = True
    elif len(skipped) > baseline:
        print(f"run-tests: SKIP GATE FAILED: {len(skipped)} skipped > baseline {baseline} for {platform} "
              f"({baseline_path}); fix the skip, or raise the baseline with the reason in the PR", file=out)
        gate_failed = True
    else:
        print(f"run-tests: skipped={len(skipped)} within baseline {baseline} for {platform}", file=out)
    return SKIP_GATE_EXIT if gate_failed else 0


if __name__ == "__main__":
    if len(sys.argv) != 5:
        print(__doc__, file=sys.stderr)
        sys.exit(2)
    sys.exit(summarize(sys.argv[1], sys.argv[2], sys.argv[3] == "1", sys.argv[4]))
