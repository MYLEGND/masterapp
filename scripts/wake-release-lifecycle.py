#!/usr/bin/env python3
"""Wake the sole protected release lifecycle after one terminal child workflow.

This script is intentionally not a release decision authority. It only dispatches
legend-release-lifecycle.yml on the protected branch and binds the wake-up to the
current terminal child run ID. The lifecycle re-reads durable evidence and decides
what, if anything, may happen next.
"""
import json
import os
import re
import urllib.error
import urllib.request

APPROVED = "legend/approved-changes"
LIFECYCLE = "legend-release-lifecycle.yml"
REPOSITORY = os.environ.get("GITHUB_REPOSITORY", "")
TOKEN = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN", "")
RUN_ID = os.environ.get("GITHUB_RUN_ID", "")

if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", REPOSITORY):
    raise SystemExit("Malformed GitHub repository identity")
if not TOKEN:
    raise SystemExit("Lifecycle wake requires an authenticated GitHub token")
if not re.fullmatch(r"[1-9][0-9]*", RUN_ID):
    raise SystemExit("Lifecycle wake requires the exact terminal child run ID")

url = f"https://api.github.com/repos/{REPOSITORY}/actions/workflows/{LIFECYCLE}/dispatches"
payload = json.dumps({
    "ref": APPROVED,
    "inputs": {"release_run": RUN_ID},
}).encode()
request = urllib.request.Request(
    url,
    data=payload,
    method="POST",
    headers={
        "Authorization": "Bearer " + TOKEN,
        "Accept": "application/vnd.github+json",
        "Content-Type": "application/json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "legend-release-lifecycle-wake/1.0",
    },
)
try:
    with urllib.request.urlopen(request, timeout=30) as response:
        if response.status != 204:
            raise RuntimeError(f"Unexpected lifecycle wake status: {response.status}")
except urllib.error.HTTPError as exc:
    raise SystemExit(f"Lifecycle wake dispatch failed with HTTP {exc.code}") from None

print(f"Dispatched protected lifecycle reconciliation for terminal child run {RUN_ID}.")
