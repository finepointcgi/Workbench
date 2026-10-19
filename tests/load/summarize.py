#!/usr/bin/env python3
"""Per-endpoint latency, errors and database time from a k6 CSV result (k6 run --out csv=result.csv ...).

    python3 -I tests/load/summarize.py result.csv [--md] [--skip 45]

--skip N leaves out the first N seconds (the ramp-up: sign-ins and cold caches).

Prints, for each request name: count, error rate, p50/p95/p99 latency, the Server-Timing database time (p50/p95) and
command count (p50/max) the server reported, and the response size.
"""
import csv
import sys
from collections import defaultdict


def pct(values, p):
    if not values:
        return float("nan")
    values = sorted(values)
    k = (len(values) - 1) * p / 100
    lo, hi = int(k), min(int(k) + 1, len(values) - 1)
    return values[lo] + (values[hi] - values[lo]) * (k - lo)


def main(path, markdown, skip):
    duration = defaultdict(list)
    failed = defaultdict(list)
    db = defaultdict(list)
    commands = defaultdict(list)
    received = defaultdict(list)
    live = []
    scenario_of = {}
    start = None
    with open(path, newline="") as f:
        for row in csv.DictReader(f):
            ts = float(row["timestamp"])
            start = ts if start is None else start
            if ts - start < skip:
                continue
            metric, name = row["metric_name"], row.get("name") or ""
            value = float(row["metric_value"])
            if metric == "http_req_duration":
                duration[name].append(value)
                scenario_of.setdefault(name, row.get("scenario", ""))
            elif metric == "http_req_failed":
                failed[name].append(value)
            elif metric == "db_ms":
                db[name].append(value)
            elif metric == "db_commands":
                commands[name].append(value)
            elif metric == "data_received" and name:
                received[name].append(value)
            elif metric == "sheet_live_update_ms":
                live.append(value)

    rows = []
    for name in sorted(duration, key=lambda n: (scenario_of[n], n)):
        if name.startswith("signin"):
            continue
        d = duration[name]
        fails = failed[name]
        rows.append([
            scenario_of[name], name, len(d), f"{100 * sum(fails) / max(1, len(fails)):.2f}%",
            f"{pct(d, 50):.0f}", f"{pct(d, 95):.0f}", f"{pct(d, 99):.0f}",
            f"{pct(db[name], 50):.0f}" if db[name] else "-", f"{pct(db[name], 95):.0f}" if db[name] else "-",
            f"{pct(commands[name], 50):.0f}" if commands[name] else "-", f"{max(commands[name]):.0f}" if commands[name] else "-",
            f"{pct(received[name], 50) / 1024:.0f}" if received[name] else "-",
        ])

    header = ["scenario", "request", "count", "errors", "p50 ms", "p95 ms", "p99 ms", "db p50 ms", "db p95 ms", "queries p50", "queries max", "KB p50"]
    if markdown:
        print("| " + " | ".join(header) + " |")
        print("|" + "|".join("---" for _ in header) + "|")
        for r in rows:
            print("| " + " | ".join(str(c) for c in r) + " |")
    else:
        widths = [max(len(str(x)) for x in col) for col in zip(header, *rows)] if rows else [len(h) for h in header]
        for r in [header] + rows:
            print("  ".join(str(c).ljust(w) for c, w in zip(r, widths)))

    total = sum(len(v) for k, v in failed.items() if not k.startswith("signin"))
    errors = sum(sum(v) for k, v in failed.items() if not k.startswith("signin"))
    print(f"\nrequests {total}, error rate {100 * errors / max(1, total):.2f}%")
    if live:
        print(f"sheet live updates received {len(live)}, delivery p50 {pct(live, 50):.0f} ms, p95 {pct(live, 95):.0f} ms")


if __name__ == "__main__":
    args = sys.argv[1:]
    skip = float(args[args.index("--skip") + 1]) if "--skip" in args else 0
    main(args[0], "--md" in args, skip)
