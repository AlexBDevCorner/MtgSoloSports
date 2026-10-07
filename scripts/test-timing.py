#!/usr/bin/env python3
"""MSS-067 slow-test timing report.

Parses a ``dotnet test --logger trx`` results file and prints the slowest
individual tests and the slowest test classes, plus sequential-equivalent
totals. Use it to spot slow-test regressions without manually
instrumenting the suite:

    dotnet test MtgSoloSports.slnx --configuration Release --logger "trx;LogFileName=timing.trx"
    python3 scripts/test-timing.py tests/MtgSoloSports.Tests/TestResults/timing.trx [--top 20]

Only the standard library is used. Never mutates the tree.
"""

from __future__ import annotations

import argparse
import collections
import os
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def parse_duration(raw: str) -> float:
    hours, minutes, seconds = raw.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(seconds)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trx", help="Path to the .trx results file.")
    parser.add_argument("--top", type=int, default=20, help="Rows per ranking.")
    parser.add_argument(
        "--min-seconds",
        type=float,
        default=0.0,
        help="Hide tests faster than this many seconds.",
    )
    args = parser.parse_args()

    if not os.path.isfile(args.trx):
        print(f"trx file not found: {args.trx}", file=sys.stderr)
        return 2

    tree = ET.parse(args.trx)
    results = tree.getroot().findall(".//t:UnitTestResult", NS)
    if not results:
        print("no UnitTestResult entries found; is this a trx file?", file=sys.stderr)
        return 2

    rows: list[tuple[float, str, str]] = []
    for result in results:
        name = result.get("testName", "?")
        secs = parse_duration(result.get("duration", "0:00:00"))
        outcome = result.get("outcome", "?")
        rows.append((secs, name, outcome))
    rows.sort(key=lambda row: row[0], reverse=True)

    total = sum(secs for secs, _, _ in rows)
    passed = sum(1 for _, _, outcome in rows if outcome == "Passed")
    print(f"tests: {len(rows)} (passed {passed}), sequential-equivalent: {total:.0f}s ({total / 60:.1f} min)")
    print()
    print(f"Slowest {args.top} tests (trx duration includes parallel contention):")
    shown = 0
    for secs, name, outcome in rows:
        if secs < args.min_seconds:
            break
        flag = "" if outcome == "Passed" else f" [{outcome}]"
        print(f"  {secs:8.1f}s  {name}{flag}")
        shown += 1
        if shown >= args.top:
            break

    by_class: dict[str, float] = collections.defaultdict(float)
    by_count: dict[str, int] = collections.defaultdict(int)
    for secs, name, _ in rows:
        cls = name.rsplit(".", 2)[0] if name.count(".") >= 2 else name
        by_class[cls] += secs
        by_count[cls] += 1

    print()
    print(f"Slowest {args.top} classes by summed test time:")
    for cls in sorted(by_class, key=lambda key: by_class[key], reverse=True)[: args.top]:
        total_cls = by_class[cls]
        count = by_count[cls]
        print(f"  {total_cls:8.0f}s total ({count:3d} tests, avg {total_cls / count:5.1f}s)  {cls}")

    print()
    print("Hint: tests_that_build_a_full_Season_1 (~30s each in isolation) dominate.")
    print("Prefer SharedSaveTemplates forks for new Season 1 based tests; see docs/test-performance-baseline.md.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
