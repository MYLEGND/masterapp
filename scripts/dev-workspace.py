#!/usr/bin/env python3
"""Prepare and check the existing MASTERAPP developer workspace; never deploy."""
from __future__ import annotations

import argparse
import contextlib
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

PLAYWRIGHT_VERSION = "1.65.0"
PLAYWRIGHT_INTEGRITY = "sha512-F3AMWcR44qMBt4kq+6Kb6mX7bjGXshktKFOPULd7reED6ZJWevjbNRqysP7q+AATE9kdPEZBibex1BYsvpVoeA=="
CI_BROWSER_TEST = "AgentPortal.Tests.CrmHistoryTests.HostedSyntheticHistorySupportsDesktopMobileKeyboardAndProtectedEndpoints"
SCOPES = ("build", "dotnet", "website", "browser")
LOG_LIMIT = 1024 * 1024
REMOTES = {
    "https://github.com/MYLEGND/masterapp.git",
    "https://github.com/MYLEGND/masterapp",
    "git@github.com:MYLEGND/masterapp.git",
    "ssh://git@github.com/MYLEGND/masterapp.git",
}
UNVERIFIED = [
    "Full application startup and shared SQL Server database/schema readiness",
    "Live identity, provider integrations, payments, notifications and voice",
    "CI-only CRM browser test and native-component rendering gate",
    "Native iOS/Android builds and physical-device behavior",
    "Independent acceptance, required CI/admission and deployment/live verification",
]


class Blocked(RuntimeError):
    pass


def probe(command: list[str], cwd: Path) -> str:
    result = subprocess.run(command, cwd=cwd, text=True, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, timeout=30, check=False)
    if result.returncode:
        raise Blocked("Read-only probe failed: " + command[0])
    return result.stdout.strip()


def repository() -> Path:
    root = Path(__file__).resolve().parents[1]
    if not (root / "MASTERAPP.sln").is_file():
        raise Blocked("Run the repository-owned script in the existing MASTERAPP checkout.")
    if Path(probe(["git", "rev-parse", "--show-toplevel"], root)).resolve() != root:
        raise Blocked("The script must belong to the checkout root.")
    if probe(["git", "config", "--get", "remote.origin.url"], root) not in REMOTES:
        raise Blocked("The origin is not the canonical MYLEGND/masterapp repository.")
    return root


def state_directory(root: Path) -> Path:
    suffix = hashlib.sha256(str(root).encode()).hexdigest()[:16]
    cloud = platform.system() == "Linux" and os.environ.get("CODESPACES") == "true"
    base = Path("/workspaces") if cloud else Path(tempfile.gettempdir())
    if cloud and (base.is_symlink() or not base.is_dir()):
        raise Blocked("Codespace state requires the existing /workspaces directory.")
    path = base / ("masterapp-dev-" + str(os.getuid()) + "-" + suffix)
    if path.is_symlink():
        raise Blocked("Workspace state path must not be a symlink.")
    if cloud and (path in root.parents or any(
            parent == root or (parent / ".git").exists() or (parent / ".git").is_symlink()
            for parent in (path, *path.parents))):
        raise Blocked("Codespace state must be outside Git checkouts.")
    path.mkdir(mode=0o700, exist_ok=True)
    if path.stat().st_uid != os.getuid() or path.stat().st_mode & 0o077:
        raise Blocked("Workspace state directory must be private and owned by this user.")
    return path


def clean_environment(root: Path, state: Path) -> dict[str, str]:
    # Preserve runtime paths/proxy trust, not application, provider or CI credentials.
    allowed = {"PATH", "HOME", "LANG", "LC_ALL", "TMPDIR", "TEMP", "TMP",
               "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "NUGET_PACKAGES",
               "SSL_CERT_FILE", "SSL_CERT_DIR", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY",
               "http_proxy", "https_proxy", "no_proxy", "SYSTEMROOT"}
    env = {key: value for key, value in os.environ.items() if key in allowed}
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", NPM_CONFIG_USERCONFIG=os.devnull,
               NPM_CONFIG_AUDIT="false", NPM_CONFIG_FUND="false",
               PLAYWRIGHT_BROWSERS_PATH=str(state / "browsers"),
               # Existing source-contract tests use this as a repository-path input.
               # It is not CI identity; GITHUB_ACTIONS/RUNNER_OS are deliberately absent.
               GITHUB_WORKSPACE=str(root))
    return env


def redact(text: str) -> str:
    for key, value in os.environ.items():
        if len(value) >= 8 and re.search(r"TOKEN|SECRET|PASSWORD|CONNECTION|API.?KEY|ACCOUNT.?KEY", key, re.I):
            text = text.replace(value, "[REDACTED]")
    text = re.sub(r"(?i)(Bearer\s+)\S+", r"\1[REDACTED]", text)
    text = re.sub(r"(?i)((?:password|pwd|client_secret|api[_-]?key|access_token|refresh_token|accountkey)\s*[:=]\s*)[^\s;,]+", r"\1[REDACTED]", text)
    text = re.sub(r"(https?://)[^/\s:@]+:[^@/\s]+@", r"\1[REDACTED]@", text)
    return text


def private_write(path: Path, content: str) -> None:
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "w") as stream:
        stream.write(content)


def run_step(name: str, command: list[str], root: Path, env: dict[str, str],
             output: Path, steps: list[dict], timeout: int = 1200) -> None:
    print("RUN " + name, flush=True)
    started = time.monotonic()
    process = subprocess.Popen(command, cwd=root, env=env, text=True, errors="replace",
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               start_new_session=True)
    timed_out = False
    try:
        raw, _ = process.communicate(timeout=timeout)
    except (subprocess.TimeoutExpired, KeyboardInterrupt) as error:
        os.killpg(process.pid, signal.SIGTERM)
        try:
            raw, _ = process.communicate(timeout=10)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            raw, _ = process.communicate()
        if isinstance(error, KeyboardInterrupt):
            raise
        timed_out = True
    cleaned = redact(raw)
    encoded = cleaned.encode("utf-8")
    truncated = len(encoded) > LOG_LIMIT
    if truncated:
        half = LOG_LIMIT // 2
        cleaned = (encoded[:half].decode("utf-8", "replace") +
                   "\n[LOG TRUNCATED: first/last portions retained]\n" +
                   encoded[-half:].decode("utf-8", "replace"))
    logfile = output / (name + ".log")
    private_write(logfile, cleaned)
    result = {"step": name, "exitCode": process.returncode, "timedOut": timed_out,
              "seconds": round(time.monotonic() - started, 1), "log": str(logfile),
              "logTruncated": truncated}
    steps.append(result)
    print(("PASS " if process.returncode == 0 and not timed_out else "FAIL ") + name +
          " — log: " + str(logfile), flush=True)
    if process.returncode != 0 or timed_out:
        raise Blocked("Step failed: " + name + ". Inspect its redacted local log; no retries were started.")


def inventory(root: Path) -> dict:
    versions = {}
    for name, command in (("dotnet", ["dotnet", "--version"]), ("node", ["node", "--version"]),
                          ("npm", ["npm", "--version"]), ("git", ["git", "--version"])):
        versions[name] = probe(command, root) if shutil.which(name) else None
    return {"checkout": str(root), "head": probe(["git", "rev-parse", "HEAD"], root),
            "branch": probe(["git", "branch", "--show-current"], root),
            "trackedChanges": bool(probe(["git", "status", "--porcelain", "--untracked-files=no"], root)),
            "platform": platform.system(), "python": platform.python_version(),
            "versions": versions, "freeGiB": round(shutil.disk_usage(root).free / 1024**3, 1),
            "codespaces": os.environ.get("CODESPACES") == "true",
            "dockerInstalled": shutil.which("docker") is not None,
            "fullApplicationReady": False, "productionReady": False}


def require_toolchain(info: dict) -> None:
    if sys.version_info < (3, 9):
        raise Blocked("Python 3.9 or newer is required.")
    versions = info["versions"]
    if not (versions.get("dotnet") or "").startswith("10."):
        raise Blocked("Select the existing .NET 10 SDK before continuing; the script does not replace SDKs.")
    if not (versions.get("node") or "").startswith("v24.") or not versions.get("npm"):
        raise Blocked("Select Node 24 with npm before continuing; the script does not replace Node.")
    if info["freeGiB"] < 6:
        raise Blocked("At least 6 GiB free is required for restore/build/browser caches; no cleanup was attempted.")


def verify_playwright(state: Path) -> Path:
    base = state / "playwright"
    lock = json.loads((base / "package-lock.json").read_text())
    entry = lock["packages"]["node_modules/playwright"]
    if entry.get("version") != PLAYWRIGHT_VERSION or entry.get("integrity") != PLAYWRIGHT_INTEGRITY:
        raise Blocked("Pinned Playwright package integrity/version mismatch.")
    if json.loads((base / "node_modules/playwright/package.json").read_text())["version"] != PLAYWRIGHT_VERSION:
        raise Blocked("Installed Playwright version differs from its verified lock.")
    return base / "node_modules/playwright/cli.js"


def bootstrap(root: Path, state: Path, env: dict, output: Path, steps: list, install_deps: bool) -> None:
    if platform.system() != "Linux" or os.environ.get("CODESPACES") != "true":
        raise Blocked("bootstrap is for the existing Linux Codespace; no Linux setup is run on a Mac.")
    for relative in ("Legend-Website", "tests/website"):
        modules = root / relative / "node_modules"
        if modules.is_symlink():
            raise Blocked("Refusing to replace an existing dependency symlink: " + relative)
        run_step("npm-" + relative.replace("/", "-"),
                 ["npm", "ci", "--prefix", relative, "--ignore-scripts", "--no-audit", "--no-fund"],
                 root, env, output, steps)
    run_step("ef-tool-restore", ["dotnet", "tool", "restore"], root, env, output, steps)
    run_step("dotnet-restore", ["dotnet", "restore", "MASTERAPP.sln", "--disable-parallel", "-m:1",
                              "-p:MasterAppArtifactsRoot=" + str(state / "build")], root, env, output, steps)
    base = state / "playwright"
    base.mkdir(mode=0o700, exist_ok=True)
    private_write(base / "package.json", json.dumps({"private": True, "dependencies": {"playwright": PLAYWRIGHT_VERSION}}))
    run_step("playwright-package", ["npm", "install", "--prefix", str(base), "--ignore-scripts",
                                   "--no-audit", "--no-fund", "--registry=https://registry.npmjs.org"],
             root, env, output, steps)
    cli = verify_playwright(state)
    if install_deps:
        run_step("chromium-system-dependencies", ["node", str(cli), "install-deps", "chromium"],
                 root, env, output, steps)
    run_step("chromium-download", ["node", str(cli), "install", "chromium"], root, env, output, steps)


@contextlib.contextmanager
def browser_modules(root: Path, state: Path):
    link = root / "tests/layout/node_modules"
    target = state / "playwright/node_modules"
    if link.exists() or link.is_symlink():
        raise Blocked("Existing tests/layout/node_modules must be preserved; resolve its ownership before browser checks.")
    link.symlink_to(target, target_is_directory=True)
    try:
        yield
    finally:
        if link.is_symlink() and link.resolve() == target.resolve():
            link.unlink()


def check(root: Path, state: Path, env: dict, output: Path, steps: list, scopes: list[str], selection: str | None) -> None:
    artifacts = "-p:MasterAppArtifactsRoot=" + str(state / "build")
    # A requested .NET test always builds this exact candidate first.
    if "build" in scopes or "dotnet" in scopes:
        build_env = env.copy()
        if platform.system() == "Linux" and os.environ.get("CODESPACES") == "true":
            build_env.update(DOTNET_PROCESSOR_COUNT="1", DOTNET_GCHeapHardLimit="0x100000000")
        run_step("solution-build", ["dotnet", "build", "MASTERAPP.sln", "-c", "Release", "--no-restore",
                                    "-m:1", "-nr:false", "-p:UseSharedCompilation=false", artifacts], root, build_env, output, steps)
    if "dotnet" in scopes:
        excluded = "FullyQualifiedName!=" + CI_BROWSER_TEST
        test_filter = "(" + selection + ")&" + excluded if selection else excluded
        print("EXCLUDED FROM LOCAL RUN: " + CI_BROWSER_TEST + " (genuine CI gate retained)", flush=True)
        test_env = env.copy()
        test_temp = output / "tmp"
        test_temp.mkdir(mode=0o700)
        test_env["TMPDIR"] = str(test_temp)
        run_step("dotnet-tests", ["dotnet", "test", "AgentPortal.Tests/AgentPortal.Tests.csproj", "-c", "Release",
                                  "--no-build", "--no-restore", "-m:1", artifacts, "--filter", test_filter,
                                  "--logger", "trx;LogFileName=portable.trx", "--results-directory", str(output)],
                 root, test_env, output, steps, timeout=1800)
        document = ET.parse(output / "portable.trx")
        counters = next((e.attrib for e in document.iter() if e.tag.rsplit("}", 1)[-1] == "Counters"), {})
        if int(counters.get("executed", "0")) < 1 or int(counters.get("failed", "0")):
            raise Blocked("The .NET TRX did not establish a nonempty passing selection.")
        steps[-1]["testCounts"] = counters
    if "website" in scopes:
        run_step("website-build", ["npm", "run", "build", "--prefix", "Legend-Website"], root, env, output, steps)
        run_step("website-tests", ["npm", "test", "--prefix", "tests/website"], root, env, output, steps)
        run_step("renderer-tests", ["npm", "run", "test:renderer", "--prefix", "Legend-Website"], root, env, output, steps)
    if "browser" in scopes:
        verify_playwright(state)
        with browser_modules(root, state):
            run_step("browser-layout", ["node", "tests/layout/chat-geometry.browser.mjs"],
                     root, env, output, steps, timeout=180)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("doctor", "bootstrap", "check"))
    parser.add_argument("--scope", action="append", choices=SCOPES,
                        help="check only: repeat to select checks; default is all four scopes")
    parser.add_argument("--filter", help="check only: additional .NET test filter; CI exclusion remains enforced")
    parser.add_argument("--install-browser-deps", action="store_true",
                        help="bootstrap only: explicitly install Chromium OS dependencies in the Linux Codespace")
    args = parser.parse_args()
    if args.command != "check" and (args.scope or args.filter):
        parser.error("--scope/--filter apply only to check")
    if args.command != "bootstrap" and args.install_browser_deps:
        parser.error("--install-browser-deps applies only to bootstrap")
    root = repository()
    info = inventory(root)
    if args.command == "doctor":
        print(json.dumps(info, indent=2))
        return 0
    require_toolchain(info)
    state = state_directory(root)
    # Lock only this checkout's generated setup/build resources; never change Git ownership.
    import fcntl
    with (state / "workspace.lock").open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise Blocked("Another dev-workspace command owns this checkout's local outputs.") from error
        output = Path(tempfile.mkdtemp(prefix=args.command + "-", dir=state))
        env = clean_environment(root, state)
        steps: list[dict] = []
        summary = {**info, "operation": args.command, "scopes": args.scope or list(SCOPES),
                   "excludedDotnetTest": CI_BROWSER_TEST, "unverified": UNVERIFIED,
                   "steps": steps, "passed": False}
        try:
            if args.command == "bootstrap":
                bootstrap(root, state, env, output, steps, args.install_browser_deps)
            else:
                check(root, state, env, output, steps, args.scope or list(SCOPES), args.filter)
            if probe(["git", "rev-parse", "HEAD"], root) != info["head"]:
                raise Blocked("HEAD changed during execution; this run cannot attest the new candidate.")
            summary["passed"] = True
        except (Blocked, OSError, ValueError, KeyError, ET.ParseError, KeyboardInterrupt) as error:
            summary["failure"] = redact(str(error)) or "Interrupted"
        finally:
            private_write(output / "summary.json", json.dumps(summary, indent=2) + "\n")
        print("Evidence: " + str(output / "summary.json"), flush=True)
        if not summary["passed"]:
            print("BLOCKED/FAILED: " + summary["failure"], file=sys.stderr)
            return 1
        print("LOCAL " + args.command.upper() + " PASSED; full application and production readiness remain unverified.")
        return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (Blocked, OSError, ValueError, subprocess.SubprocessError) as error:
        print("BLOCKED: " + redact(str(error)), file=sys.stderr)
        sys.exit(1)
