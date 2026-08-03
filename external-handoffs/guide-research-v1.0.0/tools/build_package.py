#!/usr/bin/env python3
"""Test, audit, zip, clean-extract and re-test this handoff package."""

from __future__ import annotations

import hashlib
import json
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ZIP_PATH = ROOT.parent / "CurrencyWars-GuideResearch-Handoff-v1.0.0.zip"
MANIFEST_PATH = ROOT / "PACKAGE_MANIFEST.json"
ALLOWED_SUFFIXES = {".json", ".md", ".txt", ".py"}
SECRET_PATTERNS = {
    "OpenAI-style secret": re.compile(rb"sk-[A-Za-z0-9_-]{20,}"),
    "UID number": re.compile(rb"(?i)UID\s*[:=]\s*[0-9]{6,}"),
    "Windows user path": re.compile(rb"[A-Za-z]:\\Users\\[^\\\r\n]+"),
    "private key": re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
}


def run(command: list[str], cwd: Path) -> str:
    result = subprocess.run(command, cwd=cwd, text=True, capture_output=True, check=False)
    output = result.stdout + result.stderr
    if result.returncode != 0:
        raise SystemExit(f"Command failed ({result.returncode}): {' '.join(command)}\n{output}")
    return output


def package_files(root: Path):
    for path in sorted(root.rglob("*")):
        if path.is_file() and path != MANIFEST_PATH:
            yield path


def audit(root: Path) -> list[str]:
    findings: list[str] = []
    for path in package_files(root):
        relative = path.relative_to(root).as_posix()
        if path.suffix.lower() not in ALLOWED_SUFFIXES:
            findings.append(f"disallowed file type: {relative}")
            continue
        if path.stat().st_size > 5 * 1024 * 1024:
            findings.append(f"unexpected file larger than 5 MiB: {relative}")
        content = path.read_bytes()
        for name, pattern in SECRET_PATTERNS.items():
            if pattern.search(content):
                findings.append(f"{name} detected in {relative}")
    return findings


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_manifest() -> None:
    files = []
    for path in package_files(ROOT):
        files.append(
            {
                "path": path.relative_to(ROOT).as_posix(),
                "size": path.stat().st_size,
                "sha256": sha256(path),
            }
        )
    MANIFEST_PATH.write_text(
        json.dumps(
            {
                "schemaVersion": "handoff-package-manifest.v1",
                "packageVersion": "1.0.0",
                "manifestSelfHash": None,
                "manifestSelfHashReason": "A file cannot contain its own stable SHA-256; every other package file is listed.",
                "fileCountExcludingManifest": len(files),
                "files": files,
            },
            ensure_ascii=False,
            indent=2,
        )
        + "\n",
        encoding="utf-8",
        newline="\n",
    )


def make_zip() -> None:
    if ZIP_PATH.exists():
        ZIP_PATH.unlink()
    with zipfile.ZipFile(ZIP_PATH, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(ROOT.rglob("*")):
            if path.is_file():
                archive.write(path, f"guide-research-v1.0.0/{path.relative_to(ROOT).as_posix()}")


def verify_clean_extract() -> str:
    verification_parent = Path(tempfile.mkdtemp(prefix="currency-wars-guide-handoff-verify-"))
    try:
        with zipfile.ZipFile(ZIP_PATH, "r") as archive:
            archive.extractall(verification_parent)
        extracted = verification_parent / "guide-research-v1.0.0"
        return run([sys.executable, "tests/test_contracts.py"], extracted)
    finally:
        shutil.rmtree(verification_parent, ignore_errors=True)


def main() -> int:
    initial_findings = audit(ROOT)
    if initial_findings:
        raise SystemExit("Package audit failed:\n" + "\n".join(f"  {item}" for item in initial_findings))
    print(run([sys.executable, "tests/test_contracts.py"], ROOT), end="")
    write_manifest()
    post_manifest_findings = audit(ROOT)
    if post_manifest_findings:
        raise SystemExit("Package audit failed after manifest:\n" + "\n".join(post_manifest_findings))
    make_zip()
    print("Clean extract verification:")
    print(verify_clean_extract(), end="")
    print(f"ZIP: {ZIP_PATH}")
    print(f"ZIP SHA-256: {sha256(ZIP_PATH)}")
    print(f"ZIP size: {ZIP_PATH.stat().st_size} bytes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
