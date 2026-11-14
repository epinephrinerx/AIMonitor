#!/usr/bin/env python3
"""Fail when a test was skipped without a documented platform reason.

R-014 accepts the macOS port only if nothing is skipped for platform reasons beyond the tests
that genuinely need one operating system and say so. Those skips all come from
WindowsOnlyFactAttribute or MacOSOnlyFactAttribute, which write a reason starting with
"Windows-only:" or "macOS-only:" into the .trx, so any other skip reason is an undocumented hole
in the suite and fails this check.

Usage: assert_skips_are_documented.py [results-directory] [--expect-reason PREFIX ...]
"""

import argparse
import pathlib
import sys
import xml.etree.ElementTree as ET

TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def skipped_tests(trx_path):
    """Yield (test name, reason) for every test the run did not execute."""
    root = ET.parse(trx_path).getroot()
    for result in root.iter(f"{TRX_NS}UnitTestResult"):
        if result.get("outcome") != "NotExecuted":
            continue
        message = result.find(f"{TRX_NS}Output/{TRX_NS}ErrorInfo/{TRX_NS}Message")
        reason = (message.text or "").strip() if message is not None else ""
        yield result.get("testName", "<unnamed>"), reason


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", nargs="?", default="artifacts/test-results")
    parser.add_argument(
        "--expect-reason",
        action="append",
        dest="expect_reasons",
        metavar="PREFIX",
        help="A skip reason prefix to accept; repeatable. Defaults to the platform-only ones.",
    )
    args = parser.parse_args()
    accepted = tuple(args.expect_reasons or ("Windows-only:", "macOS-only:"))

    directory = pathlib.Path(args.results)
    reports = sorted(directory.glob("*.trx"))
    if not reports:
        print(f"No .trx files under {directory} - did the test step run?", file=sys.stderr)
        return 1

    documented, undocumented = 0, []
    for report in reports:
        for name, reason in skipped_tests(report):
            if reason.startswith(accepted):
                documented += 1
            else:
                undocumented.append((name, reason or "<no reason recorded>"))

    for name, reason in undocumented:
        print(f"Skipped without a documented reason: {name} -- {reason}", file=sys.stderr)

    print(f"{len(reports)} report(s): {documented} documented skip(s), {len(undocumented)} undocumented.")
    return 1 if undocumented else 0


if __name__ == "__main__":
    sys.exit(main())
