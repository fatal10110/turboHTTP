#!/usr/bin/env python3
"""Builds the Unity platform certification matrix (Phase 15.6, #105) from NUnit3 result XMLs.

Input: the results-*.xml files that run-tests.sh, run-device-tests.sh and run-emulator-tests.sh leave
in their cache folder (or an iOS simulator results.xml). One XML is one row; its target, backend,
stripping, architecture and API level come from the file name the runners write:

  results-EditMode-<ts>.xml, results-PlayMode-<ts>.xml          Editor, Mono
  results-Android-<Low|High>-<arch>-api<sdk>[-<serial>]-<ts>.xml Android, IL2CPP

Anything else (or a correction) is given with --set key=value (target, run, backend, stripping, arch,
api, device, unity_version, filter, commit), applied to every XML of that invocation.

Output: a JSON file (default TestResults~/unity-platform-matrix.json; the "~" keeps Unity from
importing it into the package). Rows already in the file are kept unless an input row has the same
id, so separate runs add their rows one by one (--fresh starts over). The file has:

  schema / schema_version   "turbohttp.unity-platform-matrix", 1 (fields are only ever added)
  cells                      the fixed certification cells of the 15.6 plan with a status:
                             pass | fail | no-tests | not-run | needs-device-validation | policy-disabled
  rows                       per run: target, backend, stripping, arch, api, device, unity_version,
                             commit, totals, status, per-suite counts and the TURBOHTTP_METRIC lines
                             the budget tests print (Tests/Runtime/Unity/ReliabilityBudgets.cs)

Usage: platform_matrix.py [--out FILE] [--fresh] [--set key=value]... [--gate] [results.xml ...]
Exit: 0 written; 1 with --gate when a cell is fail; 2 bad arguments or unreadable input.
"""
import argparse
import datetime
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

SCHEMA = "turbohttp.unity-platform-matrix"
SCHEMA_VERSION = 1
METRIC_PREFIX = "TURBOHTTP_METRIC"
CERTIFICATION_SUITES = ("UnityReliabilitySuiteTests", "UnityStressSuiteTests", "UnityPerformanceBudgetTests")
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_OUT = os.path.join("TestResults~", "unity-platform-matrix.json")

# The certification cells of the 15.6 plan (Step 3). The ids are stable: dashboards parse them.
# "match" selects the rows of a cell; "absent" is the status of a cell no row reached.
CELLS = (
    {"id": "editor-editmode-mono", "target": "Editor", "backend": "Mono",
     "match": {"target": "Editor", "run": "EditMode"}, "absent": "not-run",
     "certifies": False},  # the runtime certification suites run in PlayMode only
    {"id": "editor-playmode-mono", "target": "Editor", "backend": "Mono",
     "match": {"target": "Editor", "run": "PlayMode"}, "absent": "not-run"},
    {"id": "standalone-windows", "target": "StandaloneWindows64", "backend": "Mono|IL2CPP",
     "match": {"target": "StandaloneWindows64"}, "absent": "not-run",
     "reason": "no script runs the test suite in a standalone player yet (#404); build-player.sh runs the smoke rows"},
    {"id": "standalone-macos", "target": "StandaloneOSX", "backend": "Mono|IL2CPP",
     "match": {"target": "StandaloneOSX"}, "absent": "needs-device-validation", "reason": "needs a Mac (#99)"},
    {"id": "standalone-linux", "target": "StandaloneLinux64", "backend": "Mono|IL2CPP",
     "match": {"target": "StandaloneLinux64"}, "absent": "not-run",
     "reason": "no script runs the test suite in a standalone player yet (#404)"},
    {"id": "android-il2cpp-arm64", "target": "Android", "backend": "IL2CPP",
     "match": {"target": "Android", "arch": "arm64"}, "absent": "needs-device-validation",
     "reason": "physical arm64 device run (#99)"},
    {"id": "android-il2cpp-x86_64-emulator", "target": "Android", "backend": "IL2CPP",
     "match": {"target": "Android", "arch": "x86_64"}, "absent": "not-run",
     "reason": "API-level coverage only: no ARM, radio or vendor stack"},
    {"id": "ios-il2cpp", "target": "iOS", "backend": "IL2CPP",
     "match": {"target": "iOS"}, "absent": "needs-device-validation", "reason": "iOS simulator or device run (#99)"},
    {"id": "webgl", "target": "WebGL", "backend": "-", "match": {"target": "WebGL"}, "absent": "policy-disabled",
     "reason": "the socket transport does not support WebGL; the test assembly excludes it"},
)

EDITOR_NAME = re.compile(r"^results-(EditMode|PlayMode)-")
ANDROID_NAME = re.compile(
    r"^results-Android-(?P<stripping>Low|High)-(?P<arch>arm64|armv7|x86_64)-api(?P<api>[^-]+)"
    r"(?:-(?P<serial>.+?))?-(?P<ts>\d{8}-\d{6})\.xml$")
METADATA_KEYS = ("target", "run", "backend", "stripping", "arch", "api", "device", "unity_version", "filter", "commit")


def unity_version_default():
    try:
        with open(os.path.join(REPO, "Tools~", "ci", "unity-version"), encoding="utf-8") as fh:
            return fh.read().strip() or None
    except OSError:
        return None


def commit_default():
    try:
        out = subprocess.run(["git", "-C", REPO, "rev-parse", "--short=12", "HEAD"],
                             capture_output=True, text=True, timeout=10, check=False)
        return out.stdout.strip() or None
    except (OSError, subprocess.SubprocessError):
        return None


def metadata_from_name(path):
    """Row metadata the runners encode in the results file name; empty when the name is not theirs."""
    name = os.path.basename(path)
    m = EDITOR_NAME.match(name)
    if m:
        return {"target": "Editor", "run": m.group(1), "backend": "Mono", "stripping": "None", "device": "host"}
    m = ANDROID_NAME.match(name)
    if m:
        serial = m.group("serial")
        emulator = (serial or "").startswith("emulator-") or m.group("arch") == "x86_64"
        meta = {"target": "Android", "run": "Player", "backend": "IL2CPP", "stripping": m.group("stripping"),
                "arch": m.group("arch"), "api": m.group("api"), "device": "emulator" if emulator else "device"}
        if serial:
            meta["serial"] = serial
        return meta
    return {}


def parse_metric(line):
    """A TURBOHTTP_METRIC line as a dict (value/budget as numbers), or None."""
    line = line.strip()
    if not line.startswith(METRIC_PREFIX + " "):
        return None
    metric = {}
    for token in line[len(METRIC_PREFIX) + 1:].split():
        key, sep, value = token.partition("=")
        if not sep:
            continue
        if key in ("value", "budget"):
            try:
                value = float(value)
            except ValueError:
                return None
        metric[key] = value
    return metric if "name" in metric and "value" in metric else None


def _suite_of(case):
    classname = case.get("classname")
    if classname:
        return classname
    fullname = (case.get("fullname") or case.get("name") or "?").split("(", 1)[0]
    return fullname.rsplit(".", 1)[0] if "." in fullname else fullname


def _status(passed, failed, total):
    if failed:
        return "fail"
    return "pass" if passed else ("no-tests" if not total else "skipped")


def parse_results(xml_path):
    """Totals, per-suite counts and metrics of one NUnit3 result file."""
    root = ET.parse(xml_path).getroot()
    suites = {}
    metrics = {}
    totals = {"total": 0, "passed": 0, "failed": 0, "skipped": 0, "inconclusive": 0}
    for case in root.iter("test-case"):
        result = case.get("result") or ""
        if case.get("label") == "Ignored" or result in ("Skipped", "Ignored"):
            bucket = "skipped"
        elif result == "Passed":
            bucket = "passed"
        elif result == "Inconclusive":
            bucket = "inconclusive"
        else:
            bucket = "failed"
        totals["total"] += 1
        totals[bucket] += 1
        suite = suites.setdefault(_suite_of(case), {"total": 0, "passed": 0, "failed": 0, "skipped": 0, "inconclusive": 0})
        suite["total"] += 1
        suite[bucket] += 1
        for line in (case.findtext("output") or "").splitlines():
            metric = parse_metric(line)
            if metric:
                metric["test"] = case.get("fullname") or case.get("name") or "?"
                metrics[(metric["test"], metric["name"])] = metric
    for suite in suites.values():
        suite["status"] = _status(suite["passed"], suite["failed"] + suite["inconclusive"], suite["total"])
    return {
        "started": root.get("start-time"),
        "duration_s": _float(root.get("duration")),
        "totals": totals,
        "status": _status(totals["passed"], totals["failed"], totals["total"]),
        "suites": dict(sorted(suites.items())),
        "metrics": [metrics[k] for k in sorted(metrics)],
    }


def _float(text):
    try:
        return float(text)
    except (TypeError, ValueError):
        return None


def row_id(meta):
    """Stable row id: target, run or backend, arch, api level, stripping and device kind."""
    parts = [meta.get("target"), meta.get("run") if meta.get("target") == "Editor" else meta.get("backend"),
             meta.get("arch"), ("api" + str(meta["api"])) if meta.get("api") else None,
             meta.get("stripping") if meta.get("target") != "Editor" else None,
             meta.get("device") if meta.get("target") != "Editor" else None]
    return "-".join(str(p).lower() for p in parts if p)


def build_row(xml_path, overrides=None, unity_version=None, commit=None):
    meta = {"unity_version": unity_version, "commit": commit}
    meta.update(metadata_from_name(xml_path))
    meta.update({k: v for k, v in (overrides or {}).items() if v is not None})
    if not meta.get("target"):
        raise ValueError(f"{xml_path}: target unknown; name it results-<EditMode|PlayMode|Android-...>-<ts>.xml "
                         f"or pass --set target=<Editor|Android|iOS|...>")
    row = {"id": row_id(meta)}
    for key in METADATA_KEYS + ("serial",):
        if meta.get(key) is not None:
            row[key] = meta[key]
    row["source"] = os.path.basename(xml_path)
    row.update(parse_results(xml_path))
    certification = [s for s in row["suites"] if s.rsplit(".", 1)[-1] in CERTIFICATION_SUITES]
    row["certification_suites"] = sorted({s.rsplit(".", 1)[-1] for s in certification})
    return row


def _matches(row, match):
    return all(str(row.get(k, "")).lower() == str(v).lower() for k, v in match.items())


def build_cells(rows):
    cells = []
    for spec in CELLS:
        matched = [r for r in rows if _matches(r, spec["match"])]
        cell = {"id": spec["id"], "target": spec["target"], "backend": spec["backend"]}
        if not matched:
            cell["status"] = spec["absent"]
            if spec.get("reason"):
                cell["reason"] = spec["reason"]
        else:
            statuses = [r["status"] for r in matched]
            cell["status"] = ("fail" if "fail" in statuses else "pass" if "pass" in statuses else "no-tests")
            cell["rows"] = [r["id"] for r in matched]
            if spec.get("certifies", True):
                ran = {s for r in matched for s in r.get("certification_suites", [])}
                cell["missing_certification_suites"] = [s for s in CERTIFICATION_SUITES if s not in ran]
        cells.append(cell)
    return cells


def build_matrix(rows, generated=None, unity_version=None):
    rows = sorted(rows, key=lambda r: r["id"])
    return {
        "schema": SCHEMA,
        "schema_version": SCHEMA_VERSION,
        "generated": generated or datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "unity_version": unity_version,
        "certification_suites": list(CERTIFICATION_SUITES),
        "cells": build_cells(rows),
        "rows": rows,
    }


def merge_rows(existing, new_rows):
    by_id = {r["id"]: r for r in existing if isinstance(r, dict) and "id" in r}
    for row in new_rows:
        by_id[row["id"]] = row
    return list(by_id.values())


def summary_lines(matrix):
    lines = []
    for cell in matrix["cells"]:
        detail = ", ".join(cell.get("rows", [])) or cell.get("reason", "")
        lines.append(f"{cell['id']:<32} {cell['status']:<24} {detail}")
    for row in matrix["rows"]:
        t = row["totals"]
        lines.append(f"  row {row['id']}: {row['status']} total={t['total']} passed={t['passed']} failed={t['failed']} "
                     f"skipped={t['skipped']} metrics={len(row['metrics'])}")
        for metric in row["metrics"]:
            budget = f" budget={metric['budget']:g}" if "budget" in metric else ""
            lines.append(f"    {metric['name']}={metric['value']:g}{budget} {metric.get('unit', '')} "
                         f"{metric.get('result', '')}".rstrip())
    return lines


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("results", nargs="*", help="NUnit3 result XML files")
    parser.add_argument("--out", default=None, help=f"matrix file (default <repo>/{DEFAULT_OUT})")
    parser.add_argument("--fresh", action="store_true", help="drop the rows already in the output file")
    parser.add_argument("--set", action="append", default=[], metavar="KEY=VALUE",
                        help="row metadata for every XML given: " + ", ".join(METADATA_KEYS))
    parser.add_argument("--gate", action="store_true", help="exit 1 when any cell is fail")
    args = parser.parse_args(argv)

    overrides = {}
    for item in args.set:
        key, sep, value = item.partition("=")
        if not sep or key not in METADATA_KEYS:
            print(f"platform_matrix: --set {item}: expected one of {', '.join(METADATA_KEYS)}=<value>", file=sys.stderr)
            return 2
        overrides[key] = value

    out = args.out or os.path.join(REPO, DEFAULT_OUT)
    unity_version = overrides.get("unity_version") or unity_version_default()
    commit = overrides.get("commit") or commit_default()
    try:
        new_rows = [build_row(p, overrides, unity_version, commit) for p in args.results]
    except (OSError, ET.ParseError, ValueError) as exc:
        print(f"platform_matrix: {exc}", file=sys.stderr)
        return 2

    existing = []
    if not args.fresh and os.path.exists(out):
        try:
            with open(out, encoding="utf-8") as fh:
                existing = json.load(fh).get("rows", [])
        except (OSError, ValueError, AttributeError) as exc:
            print(f"platform_matrix: cannot read {out} ({exc}); pass --fresh to replace it", file=sys.stderr)
            return 2

    matrix = build_matrix(merge_rows(existing, new_rows), unity_version=unity_version_default())
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(matrix, fh, indent=2)
        fh.write("\n")
    print(f"platform_matrix: {out}")
    for line in summary_lines(matrix):
        print(line)
    if args.gate and any(c["status"] == "fail" for c in matrix["cells"]):
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
