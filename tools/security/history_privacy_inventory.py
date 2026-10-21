#!/usr/bin/env python3
"""Inventory potentially personal text in reachable Git history without echoing it.

This is not a secret scanner (use Gitleaks for that). It reports location
(commit/path/category) and counts, never matched text. Reports are local-only.
Binary files, screenshots, removed/unreachable objects, PR comments and Actions
artifacts need separate inspection.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

# Broad candidates by design. All findings require human triage.
PATTERNS = {
    "windows_user_profile": re.compile(r"[a-z]:[\\/]+users[\\/]+[^\\/\s\"']+", re.I),
    "desktop_stable_id": re.compile(
        r"stableid.{0,220}(?:[\\/]desktop[\\/]|\.docx?\b|\.pdf\b|\.url\b)",
        re.I,
    ),
    "email_address": re.compile(
        r"\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b",
        re.I,
    ),
    "private_network_address": re.compile(
        r"\b(?:192\.168|172\.(?:1[6-9]|2[0-9]|3[01])|10)\."
        r"(?:\d{1,3}\.)?\d{1,3}\b",
    ),
}

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", default="artifacts/open-source-audit/history-privacy-inventory.json")
    args = parser.parse_args()

    root = subprocess.run(["git", "rev-parse", "--show-toplevel"],
                          check=True, capture_output=True, text=True).stdout.strip()
    result_path = (Path(root) / args.output).resolve()
    result_path.parent.mkdir(parents=True, exist_ok=True)
    if not result_path.is_relative_to(Path(root).resolve()):
        raise RuntimeError("The report must be saved under the repository root.")

    process = subprocess.Popen(
        ["git", "log", "--all", "--full-history", "--diff-merges=first-parent",
         "--no-ext-diff", "--no-color", "--format=PRIVACY_COMMIT:%H",
         "-p"],
        cwd=root,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
    )

    commit = None
    path = None
    counts = Counter()
    locations: set[tuple[str, str, str]] = set()
    diff_lines = 0
    try:
        assert process.stdout is not None
        for raw in process.stdout:
            line = raw.decode("utf-8", errors="replace").rstrip("\r\n")
            if line.startswith("PRIVACY_COMMIT:"):
                commit = line[len("PRIVACY_COMMIT:"):].strip()
                path = None
            elif line.startswith("diff --git a/"):
                # Keep the path in the local report only. It may itself contain
                # a private name and must never be printed in CI.
                path = line.split(" b/", 1)[-1]
            elif commit and path and line.startswith(("+", "-")) and not line.startswith(("+++", "---")):
                diff_lines += 1
                for category, pattern in PATTERNS.items():
                    if pattern.search(line[1:]):
                        counts[category] += 1
                        locations.add((commit, path, category))
    finally:
        if process.stdout:
            process.stdout.close()
        code = process.wait()

    if code != 0:
        raise RuntimeError(f"git log privacy inventory failed with exit code {code}")

    report = {
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "scope": "all locally reachable Git refs; text diff additions and deletions",
        "counts_by_category": dict(sorted(counts.items())),
        "matching_commit_path_category_count": len(locations),
        "diff_lines_examined": diff_lines,
        "locations_no_raw_matches": [
            {"commit": sha, "path": p, "category": category}
            for sha, p, category in sorted(locations)
        ],
        "limitations": [
            "Text-oriented heuristic, not a substitute for secret scanning or manual privacy review.",
            "Paths in local report may be identifying; never upload or publish it.",
            "Binary screenshots, release assets, PR discussions and Actions artifacts are not scanned.",
            "Deleted/unreachable Git objects are not included.",
        ],
    }
    result_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n",
                           encoding="utf-8")

    # Logs are deliberately limited to counts. No matched strings, paths or
    # commit IDs are ever sent to public workflow logs.
    print(f"Historical privacy inventory: examined {diff_lines} changed text lines.")
    for category in sorted(PATTERNS):
        print(f"  {category}: {counts.get(category, 0)} candidate line(s)")
    print("Candidates are NOT a publish-pass verdict; inspect local report privately.")
    return 0

if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        # Avoid leaking incidental sensitive strings contained in exceptions.
        print(f"Privacy inventory failed: {type(exc).__name__}", file=sys.stderr)
        sys.exit(2)
