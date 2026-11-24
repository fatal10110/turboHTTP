#!/usr/bin/env python3
"""Summarizes the TURBOHTTP_DEVICE_HEARTBEAT lines of a device log and flags a stalled run (#1297).

The Android test player (run-device-tests.sh) logs a heartbeat every 15 s from a dedicated thread:

  TURBOHTTP_DEVICE_HEARTBEAT n=4 up_s=60 tick_max_ms=1002 pool_ms=1 main_ms=17 workers=399/400 io=200/200
    min=4/4 pending=n/a threads=52 gc=31 mem_mb=88 test=<full name of the running test>

tick_max_ms is the longest of the interval's 1 s waits on that thread (about 1000 while the guest runs on time),
pool_ms how long the slowest work item the thread queued on the thread pool waited to start, main_ms the same for a
post to the main thread's synchronization context. A sample is flagged as
  guest       the 1 s wait itself came back seconds late: the whole player (guest, emulator or host) was paused;
  starvation  the wait was on time but the pool probe waited (or no worker was available): pool starvation;
  main        the main thread did not run a post for a minute: a test or callback blocks it;
  gap         the heartbeat line came far later than the wait allows: the heartbeat thread itself was held (logging).

Input is a logcat file written with -v epoch (run-device-tests.sh's device log): the first field of a line is the
guest epoch time. Without --live it prints a summary line and one line per stalled episode, and exits 1 when a
stall is suspected (0 otherwise, also when no heartbeat was logged). With --live it looks only at the newest
heartbeat, prints its flag and exits 1 when it is flagged, or when the heartbeats stopped while the log goes on
(flag "silent"; not after the run's finished marker), so the wait loop can take a thread dump during the stall.
"""
import argparse
import re
import sys
import time

MARKER = "TURBOHTTP_DEVICE_HEARTBEAT "
FINISHED = "TURBOHTTP_DEVICE_RUN finished"
STAMP = re.compile(r"^\s*(\d+\.\d+)\s")
FIELD = re.compile(r"(\w+)=(\S+)")

DESCRIPTIONS = {
    "guest": "guest stall: the heartbeat thread's 1 s wait came back late, the whole player was paused",
    "starvation": "thread-pool starvation: the 1 s wait was on time but pool work items waited to start",
    "main": "main thread blocked: a post to the main thread waited",
    "gap": "heartbeat gap: the heartbeat line came late although its 1 s wait was on time",
}


def parse(lines):
    """Returns (samples, last_stamp, finished_stamp): one dict per heartbeat line with its epoch stamp."""
    samples, last, finished = [], None, None
    for line in lines:
        m = STAMP.match(line)
        stamp = float(m.group(1)) if m else None
        if stamp is not None:
            last = stamp
        if FINISHED in line and stamp is not None:
            finished = stamp
        at = line.find(MARKER)
        if at < 0 or stamp is None:
            continue
        body = line[at + len(MARKER):].rstrip("\n")
        test = ""
        t = body.find(" test=")
        if t >= 0:
            body, test = body[:t], body[t + len(" test="):]
        fields = dict(FIELD.findall(body))
        if "tick_max_ms" not in fields:
            continue  # an error line, not a sample
        sample = {"stamp": stamp, "test": test.strip()}
        for key in ("n", "tick_max_ms", "pool_ms", "main_ms", "threads"):
            sample[key] = _int(fields.get(key))
        for key in ("workers", "io"):
            sample[key] = _pair(fields.get(key))
        samples.append(sample)
    return samples, last, finished


def _int(text):
    try:
        return int(text)
    except (TypeError, ValueError):
        return None


def _pair(text):
    parts = (text or "").split("/")
    if len(parts) != 2:
        return None
    a, b = _int(parts[0]), _int(parts[1])
    return None if a is None or b is None else (a, b)


def classify(sample, prev, limits):
    """The flag of one sample, or None."""
    tick = sample["tick_max_ms"]
    if tick is not None and tick >= limits.tick_ms:
        return "guest"
    pool = sample["pool_ms"]
    workers = sample["workers"]
    if (pool is not None and pool >= limits.pool_ms) or (workers is not None and workers[0] <= 0):
        return "starvation"
    main = sample["main_ms"]
    if main is not None and main >= limits.main_ms:
        return "main"
    if prev is not None and sample["stamp"] - prev["stamp"] >= limits.gap_s:
        return "gap"
    return None


def episodes(samples, limits):
    """Runs of consecutive samples with the same flag: (flag, first sample, last sample, count)."""
    out, prev = [], None
    for sample in samples:
        flag = classify(sample, prev, limits)
        prev = sample
        if flag is None:
            continue
        if out and out[-1][0] == flag and out[-1][4] == sample["index"] - 1:
            f, first, _, count, _ = out[-1]
            out[-1] = (f, first, sample, count + 1, sample["index"])
        else:
            out.append((flag, sample, sample, 1, sample["index"]))
    return [(f, first, last, count) for f, first, last, count, _ in out]


def clock(stamp):
    return time.strftime("%H:%M:%S", time.gmtime(stamp))


def _max(samples, key):
    values = [s[key] for s in samples if s[key] is not None]
    return max(values) if values else None


def _fmt(value, unit=""):
    return "n/a" if value is None else f"{value}{unit}"


def summarize(samples, limits, prefix):
    if not samples:
        print(f"{prefix}heartbeat: none logged")
        return 0
    for i, s in enumerate(samples):
        s["index"] = i
    gaps = [b["stamp"] - a["stamp"] for a, b in zip(samples, samples[1:])]
    workers = [s["workers"] for s in samples if s["workers"]]
    io = [s["io"] for s in samples if s["io"]]
    span = samples[-1]["stamp"] - samples[0]["stamp"]
    print(f"{prefix}heartbeat: {len(samples)} samples over {span:.0f} s; 1 s wait max "
          f"{_fmt(_max(samples, 'tick_max_ms'), ' ms')}, pool probe max {_fmt(_max(samples, 'pool_ms'), ' ms')}, "
          f"main probe max {_fmt(_max(samples, 'main_ms'), ' ms')}, heartbeat gap max "
          f"{(max(gaps) if gaps else 0):.0f} s, workers available min "
          f"{min(workers)[0] if workers else 'n/a'}/{workers[0][1] if workers else 'n/a'}, io available min "
          f"{min(io)[0] if io else 'n/a'}/{io[0][1] if io else 'n/a'}, threads max {_fmt(_max(samples, 'threads'))}")
    found = episodes(samples, limits)
    for flag, first, last, count in found[:8]:
        worst = max(s["tick_max_ms"] or 0 for s in samples[first["index"]:last["index"] + 1])
        pool = max(s["pool_ms"] or 0 for s in samples[first["index"]:last["index"] + 1])
        print(f"{prefix}STALL SUSPECTED ({DESCRIPTIONS[flag]}) from {clock(first['stamp'])} to "
              f"{clock(last['stamp'])} UTC, {count} sample(s), 1 s wait max {worst} ms, pool probe max {pool} ms; "
              f"running: {first['test'] or 'n/a'}")
    if len(found) > 8:
        print(f"{prefix}STALL SUSPECTED: {len(found) - 8} more episode(s)")
    if found:
        print(f"{prefix}a stalled run is not a valid result: rerun it (testing.md, #1297)")
        return 1
    return 0


def live(samples, last, finished, limits):
    """The flag of the newest heartbeat, or "silent" when they stopped while the log goes on."""
    if not samples:
        return None
    newest = samples[-1]
    if finished is not None and finished >= newest["stamp"]:
        return None
    if last is not None and last - newest["stamp"] >= limits.silent_s:
        return "silent"
    return classify(newest, samples[-2] if len(samples) > 1 else None, limits)


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("log", help="logcat file written with -v epoch")
    p.add_argument("--live", action="store_true", help="check only the newest heartbeat")
    p.add_argument("--prefix", default="", help="prefix of every printed line")
    p.add_argument("--tick-ms", type=int, default=5000, help="1 s wait at or above this is a guest stall")
    p.add_argument("--pool-ms", type=int, default=10000, help="pool probe wait at or above this is starvation")
    p.add_argument("--main-ms", type=int, default=60000, help="main-thread probe wait at or above this is a block")
    p.add_argument("--gap-s", type=float, default=60, help="seconds between heartbeats at or above this is a gap")
    p.add_argument("--silent-s", type=float, default=90, help="--live: newest heartbeat this much older than the log")
    args = p.parse_args(argv)
    try:
        with open(args.log, encoding="utf-8", errors="replace") as fh:
            samples, last, finished = parse(fh)
    except OSError as e:
        print(f"{args.prefix}heartbeat: cannot read {args.log}: {e}", file=sys.stderr)
        return 0
    if args.live:
        flag = live(samples, last, finished, args)
        if flag is None:
            return 0
        print(flag)
        return 1
    return summarize(samples, args, args.prefix)


if __name__ == "__main__":
    sys.exit(main())
