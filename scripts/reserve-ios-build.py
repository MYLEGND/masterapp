#!/usr/bin/env python3
"""Advance the existing Xcode project version before a local archive."""
import fcntl
import os
from pathlib import Path
import plistlib
import re
import sys


def reserve(project: Path, archives: Path, lock_path: Path) -> int:
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    with lock_path.open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        source = project.read_text()
        pattern = r"(CURRENT_PROJECT_VERSION\s*=\s*)(\d+)(;)"
        versions = {int(match[1]) for match in re.findall(pattern, source)}
        if len(versions) != 1:
            raise ValueError("Xcode build versions are missing or inconsistent; no changes made")
        highest = versions.pop()
        for info in archives.glob("*/*.xcarchive/Info.plist"):
            data = plistlib.loads(info.read_bytes()).get("ApplicationProperties", {})
            if data.get("CFBundleIdentifier") != "com.mylegnd.legend.registered":
                continue
            value = str(data.get("CFBundleVersion", ""))
            if not value.isdecimal():
                raise ValueError(f"Cannot safely compare archived build version: {info}")
            highest = max(highest, int(value))
        version = highest + 1
        # The existing project remains the authority for app and extension versions.
        # Persist before the build so failed attempts do not reuse reservations.
        with project.open("w") as output:
            output.write(re.sub(pattern, lambda match: f"{match[1]}{version}{match[3]}", source))
            output.flush()
            os.fsync(output.fileno())
        return version


if __name__ == "__main__":
    root = Path(__file__).resolve().parent.parent
    print(reserve(
        root / "Legend-ios/Legend.xcodeproj/project.pbxproj",
        Path.home() / "Library/Developer/Xcode/Archives",
        Path.home() / "Library/Caches/LEGEND/release-ios.lock",
    ))
