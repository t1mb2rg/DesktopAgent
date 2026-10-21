#!/usr/bin/env python3
"""Pre-publication hygiene check for a *downloaded* Tessel release ZIP.

Checks archive members and small plaintext files only. This does NOT inspect
the contents of the Inno Setup EXE, .NET binary strings, PDBs, or signatures.
Reports stay local and must not be uploaded to GitHub Actions.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import zipfile
from pathlib import Path

BLOCKED_NAMES = {
    ".env", "settings.json", "workspace-session.json",
    "grid-calibration-environment.json", "desktop-agent.db",
    "appsettings.local.json",
}
BLOCKED_SUFFIXES = (
    ".db", ".db-wal", ".db-shm", ".sqlite", ".sqlite3", ".dpapi",
    ".pem", ".pfx", ".key", ".log", ".trx", ".snk",
)
TEXT_SUFFIXES = (".txt", ".json", ".config", ".xml", ".yml", ".yaml", ".md", ".ini")
TEXT_LIMIT_BYTES = 1024 * 1024
TEXT_BUDGET_BYTES = 25 * 1024 * 1024
SUSPECT_TEXT = {
    "user_profile_path": re.compile(r"[a-z]:[\\/]+users[\\/]+[^\\/\s\"']+", re.I),
    "github_auth_token": re.compile(r"(?:ghp_|github_pat_)[a-z0-9_]{20,}", re.I),
    "openai_style_key": re.compile(r"\bsk-[a-z0-9_-]{24,}\b", re.I),
    "private_key_pem": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", re.I),
}

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle_zip", type=Path)
    parser.add_argument("--report", type=Path,
                        default=Path("artifacts/open-source-audit/release-zip-audit.json"))
    args = parser.parse_args()
    if not args.bundle_zip.is_file():
        print("Release ZIP not found.", file=sys.stderr)
        return 2

    flags = []
    count = 0
    text_bytes = 0
    uninspected_large_text = 0
    sha256 = hashlib.sha256()
    with args.bundle_zip.open("rb") as inp:
        for chunk in iter(lambda: inp.read(1024 * 1024), b""):
            sha256.update(chunk)

    with zipfile.ZipFile(args.bundle_zip) as package:
        for member in package.infolist():
            if member.is_dir():
                continue
            count += 1
            p = member.filename.replace("\\", "/")
            basename = p.rsplit("/", 1)[-1].lower()
            if p.startswith(("/", "\\")) or any(part == ".." for part in p.split("/")):
                flags.append({"entry": p, "reason": "unsafe_archive_path"})
            if basename in BLOCKED_NAMES or basename.endswith(BLOCKED_SUFFIXES):
                flags.append({"entry": p, "reason": "potential_runtime_data_or_secret"})
            if basename.endswith(".pdb"):
                # Debug symbols can contain source paths and need separate review.
                flags.append({"entry": p, "reason": "debug_symbols_review"})
            if basename.endswith(TEXT_SUFFIXES):
                if member.file_size > TEXT_LIMIT_BYTES or text_bytes + member.file_size > TEXT_BUDGET_BYTES:
                    uninspected_large_text += 1
                    continue
                text_bytes += member.file_size
                decoded = package.read(member).decode("utf-8", errors="replace")
                for category, rx in SUSPECT_TEXT.items():
                    if rx.search(decoded):
                        flags.append({"entry": p, "reason": category})

    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps({
        "bundle_sha256": sha256.hexdigest(),
        "members": count,
        "checked_text_bytes": text_bytes,
        "uninspected_large_text_files": uninspected_large_text,
        "findings": flags,
        "limitations": [
            "The Inno Setup installer must be examined separately.",
            "Compiled binary strings, embedded resources, debug symbols, signatures and file metadata are not fully inspected.",
            "Only small UTF-8 text-like files are scanned; patterns are heuristics.",
            "Never upload this potentially sensitive report to a public service.",
        ],
    }, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Release ZIP: {count} members; {len(flags)} flag(s); "
          f"{uninspected_large_text} oversized text file(s) not scanned.")
    print("Report saved locally. Review any flags, release installer and binary contents before publishing.")
    return 1 if flags or uninspected_large_text else 0

if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:
        print(f"Release ZIP audit unable to finish: {type(e).__name__}", file=sys.stderr)
        sys.exit(2)
