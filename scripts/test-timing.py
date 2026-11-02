#!/usr/bin/env python3
"""MSS-122 slow-test timing report (extends MSS-067).

Parses a ``dotnet test --logger trx`` results file and prints the slowest
individual tests and the slowest test classes, plus sequential-equivalent
totals. Use it to spot slow-test regressions without manually
instrumenting the suite:

    dotnet test MtgSoloSports.slnx --configuration Release --logger "trx;LogFileName=timing.trx"
    python3 scripts/test-timing.py tests/MtgSoloSports.Tests/TestResults/timing.trx [--top 20]

Class grouping uses TRX ``TestDefinitions/UnitTest/TestMethod@className``
metadata joined via ``testId`` (case-insensitive), so parameterized cases
(``Method(arg: 1)``) group correctly. When definitions are missing, it falls
back to the display-name heuristic.

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
    try:
        hours, minutes, seconds = raw.split(":")
        return int(hours) * 3600 + int(minutes) * 60 + float(seconds)
    except (ValueError, AttributeError):
        return 0.0


def fallback_class(display_name: str) -> str:
    # Last segment is the method (possibly with "(args)"); class is everything before it.
    if "." not in display_name:
        return display_name
    method_split = display_name.rsplit(".", 1)[0]
    return method_split


def collect_rows(root: ET.Element) -> tuple[list[tuple[float, str, str, str]], int]:
    """Return (secs, class, display_name, outcome) rows plus definition hits."""
    id_to_class: dict[str, str] = {}
    for unit in root.findall(".//t:TestDefinitions/t:UnitTest", NS):
        tid = unit.get("id", "")
        method = unit.find("t:TestMethod", NS)
        cls = method.get("className", "") if method is not None else ""
        if tid and cls:
            id_to_class[tid] = cls
            id_to_class[tid.lower()] = cls

    results = root.findall(".//t:Results/t:UnitTestResult", NS)
    if not results:
        results = root.findall(".//t:UnitTestResult", NS)

    rows: list[tuple[float, str, str, str]] = []
    hits = 0
    for result in results:
        display = result.get("testName", "?")
        secs = parse_duration(result.get("duration", "0:00:00"))
        outcome = result.get("outcome", "?")
        tid = result.get("testId", "")
        cls = id_to_class.get(tid, "") or id_to_class.get(tid.lower(), "")
        if cls:
            hits += 1
        else:
            cls = fallback_class(display)
        rows.append((secs, cls, display, outcome))
    rows.sort(key=lambda row: row[0], reverse=True)
    return rows, hits


def self_check() -> int:
    trx = """<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <TestDefinitions>
    <UnitTest id="11111111-1111-1111-1111-111111111111">
      <TestMethod className="Ns.AlphaTests" name="Fast" />
    </UnitTest>
    <UnitTest id="22222222-2222-2222-2222-222222222222">
      <TestMethod className="Ns.BetaTests" name="Theory" />
    </UnitTest>
    <UnitTest id="33333333-3333-3333-3333-333333333333">
      <TestMethod className="Ns.BetaTests" name="Theory" />
    </UnitTest>
  </TestDefinitions>
  <Results>
    <UnitTestResult testId="11111111-1111-1111-1111-111111111111" testName="Ns.AlphaTests.Fast" duration="00:00:05" outcome="Passed" />
    <UnitTestResult testId="22222222-2222-2222-2222-222222222222" testName="Ns.BetaTests.Theory(x: 1)" duration="00:00:07" outcome="Passed" />
    <UnitTestResult testId="33333333-3333-3333-3333-333333333333" testName="Ns.BetaTests.Theory(x: 2)" duration="00:00:09" outcome="Failed" />
    <UnitTestResult testId="44444444-4444-4444-4444-444444444444" testName="Ns.GammaTests.Orphan" duration="not-a-duration" outcome="Passed" />
    <UnitTestResult testId="55555555-5555-5555-5555-555555555555" testName="BareName" outcome="Passed" />
  </Results>
</TestRun>"""
    root = ET.fromstring(trx)
    rows, hits = collect_rows(root)
    assert hits == 3, f"expected 3 definition hits, got {hits}"
    assert len(rows) == 5, f"expected 5 rows, got {len(rows)}"
    by_class: dict[str, float] = collections.defaultdict(float)
    by_count: dict[str, int] = collections.defaultdict(int)
    for secs, cls, _, _ in rows:
        by_class[cls] += secs
        by_count[cls] += 1
    # Parameterized Beta cases must group to one class with summed 16s.
    assert by_count["Ns.BetaTests"] == 2, by_count
    assert abs(by_class["Ns.BetaTests"] - 16.0) < 0.01, by_class
    # Malformed duration is 0, missing duration is 0, both preserved as rows.
    assert by_class["Ns.GammaTests"] == 0.0, by_class
    assert by_count["BareName"] == 1, by_count
    # Slowest-first ordering with outcomes preserved.
    assert rows[0][2] == "Ns.BetaTests.Theory(x: 2)" and rows[0][3] == "Failed", rows[0]
    print("self-check: PASS (class grouping, parameterized cases, durations, missing/malformed)")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trx", nargs="?", help="Path to the .trx results file.")
    parser.add_argument("--top", type=int, default=20, help="Rows per ranking.")
    parser.add_argument(
        "--min-seconds",
        type=float,
        default=0.0,
        help="Hide tests faster than this many seconds.",
    )
    parser.add_argument(
        "--self-check",
        action="store_true",
        help="Run the synthetic TRX grouping self-check and exit.",
    )
    args = parser.parse_args()

    if args.self_check:
        return self_check()

    if not args.trx:
        print("trx file argument is required (or use --self-check).", file=sys.stderr)
        return 2

    if not os.path.isfile(args.trx):
        print(f"trx file not found: {args.trx}", file=sys.stderr)
        return 2

    tree = ET.parse(args.trx)
    rows, hits = collect_rows(tree.getroot())
    if not rows:
        print("no UnitTestResult entries found; is this a trx file?", file=sys.stderr)
        return 2

    total = sum(secs for secs, _, _, _ in rows)
    passed = sum(1 for _, _, _, outcome in rows if outcome == "Passed")
    print(f"tests: {len(rows)} (passed {passed}), sequential-equivalent: {total:.0f}s ({total / 60:.1f} min)")
    if hits == 0:
        print("warning: no TestDefinitions/TestMethod@className metadata found; class ranking uses display-name fallback.")
    print()
    print("Slowest test cases (trx duration includes parallel contention):")
    shown = 0
    for secs, _, name, outcome in rows:
        if secs < args.min_seconds:
            break
        flag = "" if outcome == "Passed" else f" [{outcome}]"
        print(f"  {secs:8.1f}s  {name}{flag}")
        shown += 1
        if shown >= args.top:
            break

    by_class: dict[str, float] = collections.defaultdict(float)
    by_count: dict[str, int] = collections.defaultdict(int)
    for secs, cls, _, _ in rows:
        by_class[cls] += secs
        by_count[cls] += 1

    print()
    print("Slowest test classes by summed test time (TRX TestMethod@className):")
    for cls in sorted(by_class, key=lambda key: by_class[key], reverse=True)[: args.top]:
        total_cls = by_class[cls]
        count = by_count[cls]
        print(f"  {total_cls:8.0f}s total ({count:3d} tests, avg {total_cls / count:5.1f}s)  {cls}")

    print()
    print("Hint: tests_that_build_a_full_Season_1 (~30s each in isolation) dominate.")
    print("Prefer SharedSaveTemplates forks for new Season 1 based tests; see docs/testing.md.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
