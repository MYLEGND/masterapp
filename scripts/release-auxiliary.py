#!/usr/bin/env python3
"""Run independent post-publication Cloudflare release lanes concurrently.

Founder baseline and business-router publication own different Workers/resources.
Each lane executes from its own copied Cloudflare workspace and installs its own
lockfile-pinned toolchain, so npm/Wrangler runtime state can never race across
lanes while the two provider operations overlap.

Durable write authorization remains inside deploy-founder-cloudflare.py and
release-router.py. This coordinator never creates an alternate receipt authority.
"""
from __future__ import annotations

import concurrent.futures
import http.client
import importlib.util
import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import sys
import tempfile
import time


ROOT = Path(__file__).resolve().parents[1]
CF = ROOT / "Legend-Cloudflare"


def release_authority():
    path = ROOT / "scripts" / "validation-resume.py"
    spec = importlib.util.spec_from_file_location("validation_resume_authority", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_RELEASE_AUTHORITY = release_authority()
RESULT_ROOT = Path(os.environ.get("RELEASE_AUXILIARY_RESULT_DIR", "/tmp/release-auxiliary-results"))


def run(command, *, cwd=ROOT, input_text=None, timeout=600, env=None):
    result = subprocess.run(
        list(command),
        cwd=cwd,
        input=input_text,
        text=True,
        capture_output=True,
        timeout=timeout,
        check=False,
        env=env,
    )
    if result.returncode:
        raise RuntimeError("Auxiliary release command failed")
    return result.stdout.strip()


def write_result(name, payload):
    RESULT_ROOT.mkdir(parents=True, exist_ok=True)
    path = RESULT_ROOT / f"{name}.json"
    temporary = path.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(payload, sort_keys=True) + "\n")
    temporary.replace(path)


def isolated_cloudflare_workspace(label):
    root = Path(tempfile.mkdtemp(prefix=f"legend-{label}-cloudflare-"))
    target = root / "Legend-Cloudflare"
    shutil.copytree(
        CF,
        target,
        ignore=shutil.ignore_patterns("node_modules", ".wrangler", "*.log"),
    )
    return root, target


def founder_lane():
    token = os.environ.get("CLOUDFLARE_API_TOKEN", "")
    account = os.environ.get("CLOUDFLARE_ACCOUNT_ID", "")
    if not token or not account:
        raise RuntimeError("Founder Cloudflare authority is not configured")
    print("::add-mask::" + token, flush=True)
    print("::add-mask::" + account, flush=True)
    Path("/tmp/translation-results").mkdir(parents=True, exist_ok=True)
    workspace_root, workspace = isolated_cloudflare_workspace("founder")
    environment = os.environ.copy()
    environment["LEGEND_CLOUDFLARE_ROOT"] = str(workspace)
    try:
        run([
            sys.executable,
            "scripts/deploy-founder-cloudflare.py",
            "deploy",
            "--state", "/tmp/legend-founder-cloudflare-state.json",
            "--receipt", "/tmp/translation-results/legend-founder-cloudflare.json",
        ], timeout=900, env=environment)
    finally:
        shutil.rmtree(workspace_root, ignore_errors=True)
    return {"schemaVersion": 1, "lane": "founder", "success": True}


def first_party_router_proof():
    hosts = tuple(json.loads(os.environ["ROUTING_PASS_THROUGH_HOSTS"]))
    if not hosts or any(not isinstance(host, str) or not host for host in hosts):
        raise RuntimeError("Router pass-through host inventory is invalid")
    context = ssl.create_default_context()
    failures = []
    for host in hosts:
        last = None
        for attempt in range(1, 7):
            connection = http.client.HTTPSConnection(host, 443, timeout=15, context=context)
            try:
                connection.request(
                    "GET", "/",
                    headers={
                        "User-Agent": "LEGEND-worker-release-smoke/1.0",
                        "Cache-Control": "no-cache",
                    },
                )
                response = connection.getresponse()
                body = response.read(1025)
                last = f"HTTP {response.status}"
                print(host, attempt, response.status, len(body), flush=True)
                if 200 <= response.status < 400:
                    break
            except Exception as exc:
                last = type(exc).__name__
                print(host, attempt, last, flush=True)
            finally:
                connection.close()
            time.sleep(5)
        else:
            failures.append(f"{host}: {last}")
    if failures:
        raise RuntimeError("LEGEND-owned host smoke failed after bounded retries")


def router_lane():
    _RELEASE_AUTHORITY.require_canonical_release_runtime()
    result = {
        "schemaVersion": 1,
        "lane": "router",
        "policySuccess": False,
        "deploySuccess": False,
    }
    try:
        run([sys.executable, "scripts/cloudflare-routing-authority.py", "reconcile-bot-fight"])
        run([sys.executable, "scripts/cloudflare-routing-authority.py", "reconcile-bic"])
        result["policySuccess"] = True

        decision = run([sys.executable, "scripts/release-router.py", "prepare"])
        if decision == "publish":
            workspace_root, workspace = isolated_cloudflare_workspace("router")
            try:
                run([
                    "npm", "ci", "--ignore-scripts", "--no-audit", "--no-fund",
                ], cwd=workspace, timeout=600)
                wrangler = workspace / "node_modules" / ".bin" / "wrangler"
                if not wrangler.is_file():
                    raise RuntimeError("Canonical Wrangler binary missing after router install")
                run([
                    str(wrangler), "deploy",
                    "--config", "wrangler.website-routing.jsonc",
                ], cwd=workspace, timeout=900)
                run([
                    str(wrangler), "secret", "put", "LEGEND_WEBSITE_BRIDGE_SECRET",
                    "--config", "wrangler.website-routing.jsonc",
                ], cwd=workspace, input_text=os.environ["LEGEND_WEBSITE_BRIDGE_SECRET"], timeout=300)
            finally:
                shutil.rmtree(workspace_root, ignore_errors=True)
            run([sys.executable, "scripts/release-router.py", "complete"])
        elif decision != "preserve":
            raise RuntimeError("Unknown router publication decision")

        first_party_router_proof()
        result["deploySuccess"] = True
        return result
    except Exception:
        return result


def guarded(name, callback):
    try:
        payload = callback()
    except Exception as exc:
        payload = {
            "schemaVersion": 1,
            "lane": name,
            "success": False,
            "errorType": type(exc).__name__,
        }
    if name == "router":
        payload["success"] = bool(payload.get("policySuccess") and payload.get("deploySuccess"))
    write_result(name, payload)
    return payload


def main():
    founder = os.environ.get("RUN_FOUNDER_AUXILIARY") == "true"
    router = os.environ.get("RUN_ROUTER_AUXILIARY") == "true"
    lanes = []
    if founder:
        lanes.append("founder")
    if router:
        lanes.append("router")
    if not lanes:
        raise RuntimeError("No auxiliary release lane selected")

    callbacks = {
        "founder": founder_lane,
        "router": router_lane,
    }
    results = {}
    with concurrent.futures.ThreadPoolExecutor(max_workers=len(lanes)) as pool:
        futures = {pool.submit(guarded, lane, callbacks[lane]): lane for lane in lanes}
        for future in concurrent.futures.as_completed(futures):
            lane = futures[future]
            results[lane] = future.result()

    failed = [lane for lane in lanes if results.get(lane, {}).get("success") is not True]
    print(json.dumps({
        "lanes": lanes,
        "failed": failed,
    }, sort_keys=True))
    if failed:
        raise SystemExit(1)


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print("Auxiliary release fanout failed: " + type(exc).__name__, file=sys.stderr)
        raise SystemExit(1)
