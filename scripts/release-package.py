#!/usr/bin/env python3
"""Canonical immutable release-package authority.

Validation builds one exact package set for a validated application revision.
Release may only restore and verify those bytes; it never rebuilds a supposedly
equivalent package. Deployment authorization remains in the approved release
workflow and is intentionally separate from package identity.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import zipfile

SCHEMA = "legend-validated-release-package.v1"
ROOT = Path(__file__).resolve().parents[1]
APPS = {
    "portal": ("AgentPortal/AgentPortal.csproj", "agentportal.zip", False),
    "client": ("ClientApp/ClientApp.csproj", "clientapp.zip", False),
    "protect": ("Protect-Website/ProtectWebsite.csproj", "protect.zip", False),
    "parfait": ("ParfaitApp/ParfaitApp.csproj", "parfait.zip", False),
    "website": ("static", "website.zip", True),
}
MIGRATION_BUNDLE = "masterapp-migrations"
CONTRACT_INPUTS = (
    "scripts/release-package.py",
    "scripts/deploy-approved-app.py",
    ".config/dotnet-tools.json",
)


def sha256_file(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate_revision(value: str) -> str:
    if not re.fullmatch(r"[0-9a-f]{40}", value or ""):
        raise ValueError("Expected exact lowercase Git commit SHA")
    subprocess.run(["git", "cat-file", "-e", value + "^{commit}"], cwd=ROOT, check=True)
    return value


def contract_hash() -> str:
    digest = hashlib.sha256()
    for relative in CONTRACT_INPUTS:
        path = ROOT / relative
        if not path.exists():
            raise FileNotFoundError(relative)
        digest.update(relative.encode() + b"\0")
        digest.update(path.read_bytes())
        digest.update(b"\0")
    return digest.hexdigest()


def package_identity(revision: str) -> str:
    revision = validate_revision(revision)
    payload = json.dumps(
        {
            "schema": SCHEMA,
            "applicationReleaseSha": revision,
            "packageContractSha256": contract_hash(),
        },
        sort_keys=True,
        separators=(",", ":"),
    ).encode()
    return hashlib.sha256(payload).hexdigest()


def artifact_name(revision: str) -> str:
    return "founder-diagnostics-packages-" + package_identity(revision)


def run(*args, env=None):
    subprocess.run(list(args), cwd=ROOT, env=env, check=True)


def zip_directory(source: Path, destination: Path):
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(source.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(source))


def build_dotnet(app: str, revision: str, output: Path):
    project, archive_name, _ = APPS[app]
    publish = output / (app + "-publish")
    shutil.rmtree(publish, ignore_errors=True)
    run(
        "dotnet", "publish", project, "-c", "Release", "--nologo",
        "-o", str(publish), "-p:SourceRevisionId=" + revision,
        "-m:1", "-nr:false", "-p:UseSharedCompilation=false",
    )
    wwwroot = publish / "wwwroot"
    wwwroot.mkdir(parents=True, exist_ok=True)
    (wwwroot / "_deployment-provenance.json").write_text(
        json.dumps({"releaseSha": revision, "release": SCHEMA}, separators=(",", ":")) + "\n"
    )
    zip_directory(publish, output / archive_name)


def build_website(revision: str, output: Path):
    run("npm", "ci", "--prefix", "Legend-Website", "--ignore-scripts", "--no-audit", "--no-fund")
    run("npm", "--prefix", "Legend-Website", "run", "build")
    dist = ROOT / "Legend-Website" / "dist"
    (dist / "_deployment-provenance.txt").write_text(revision + "\n")
    shutil.copy2(ROOT / "Legend-Website" / "public" / "web.config", dist / "web.config")
    zip_directory(dist, output / APPS["website"][1])


def build_migration_bundle(output: Path):
    env = os.environ | {
        "ConnectionStrings__MasterAppDb": "Server=127.0.0.1;Database=masterapp_ef_design_time;Integrated Security=true;TrustServerCertificate=true;Encrypt=false",
        "EF_FORCE_SQLSERVER": "true",
        "DOTNET_ENVIRONMENT": "Development",
        "ASPNETCORE_ENVIRONMENT": "Development",
    }
    run("dotnet", "tool", "restore", env=env)
    destination = output / MIGRATION_BUNDLE
    run(
        "dotnet", "tool", "run", "dotnet-ef", "migrations", "bundle",
        "--force",
        "--project", "Infrastructure/Infrastructure.csproj",
        "--startup-project", "AgentPortal/AgentPortal.csproj",
        "--context", "MasterAppDbContext",
        "--configuration", "Release",
        "--output", str(destination),
        env=env,
    )
    destination.chmod(0o755)


def build_all(revision: str, output: Path):
    revision = validate_revision(revision)
    shutil.rmtree(output, ignore_errors=True)
    output.mkdir(parents=True, exist_ok=True)

    for app in ("portal", "client", "protect", "parfait"):
        build_dotnet(app, revision, output)
    build_website(revision, output)
    build_migration_bundle(output)

    files = [output / APPS[app][1] for app in APPS] + [output / MIGRATION_BUNDLE]
    sums = []
    for path in files:
        sums.append(f"{sha256_file(path)}  {path.name}")
    (output / "SHA256SUMS").write_text("\n".join(sums) + "\n")

    tree = subprocess.check_output(
        ["git", "rev-parse", revision + "^{tree}"], cwd=ROOT, text=True
    ).strip()
    manifest = {
        "schema": SCHEMA,
        "applicationReleaseSha": revision,
        "applicationTreeSha": tree,
        "packageContractSha256": contract_hash(),
        "packageIdentity": package_identity(revision),
        "files": {path.name: sha256_file(path) for path in files},
    }
    (output / "manifest.json").write_text(json.dumps(manifest, sort_keys=True, indent=2) + "\n")
    verify_all(revision, output)
    return manifest


def embedded_revision(package: Path, static: bool):
    with zipfile.ZipFile(package) as archive:
        if archive.testzip() is not None:
            raise ValueError("Corrupt immutable ZIP: " + package.name)
        entry = "_deployment-provenance.txt" if static else "wwwroot/_deployment-provenance.json"
        raw = archive.read(entry).decode().strip()
        return raw if static else json.loads(raw)["releaseSha"]


def verify_all(revision: str, directory: Path):
    revision = validate_revision(revision)
    manifest_path = directory / "manifest.json"
    sums_path = directory / "SHA256SUMS"
    if not manifest_path.exists() or not sums_path.exists():
        raise ValueError("Validated package manifest/checksums missing")
    manifest = json.loads(manifest_path.read_text())
    expected_tree = subprocess.check_output(
        ["git", "rev-parse", revision + "^{tree}"], cwd=ROOT, text=True
    ).strip()
    expected = {
        "schema": SCHEMA,
        "applicationReleaseSha": revision,
        "applicationTreeSha": expected_tree,
        "packageContractSha256": contract_hash(),
        "packageIdentity": package_identity(revision),
    }
    for key, value in expected.items():
        if manifest.get(key) != value:
            raise ValueError(f"Validated package {key} mismatch")

    rows = {}
    for line in sums_path.read_text().splitlines():
        parts = line.split()
        if len(parts) != 2:
            raise ValueError("Malformed SHA256SUMS")
        rows[Path(parts[1]).name] = parts[0]

    expected_files = [APPS[app][1] for app in APPS] + [MIGRATION_BUNDLE]
    if set(rows) != set(expected_files):
        raise ValueError("Validated package inventory mismatch")
    manifest_files = manifest.get("files")
    if not isinstance(manifest_files, dict) or set(manifest_files) != set(expected_files):
        raise ValueError("Validated package manifest inventory mismatch")

    for app, (_, archive_name, static) in APPS.items():
        path = directory / archive_name
        digest = sha256_file(path)
        if rows.get(archive_name) != digest or manifest_files.get(archive_name) != digest:
            raise ValueError("Validated package digest mismatch: " + archive_name)
        if embedded_revision(path, static) != revision:
            raise ValueError("Validated package provenance mismatch: " + app)

    bundle = directory / MIGRATION_BUNDLE
    bundle_digest = sha256_file(bundle)
    if rows.get(MIGRATION_BUNDLE) != bundle_digest or manifest_files.get(MIGRATION_BUNDLE) != bundle_digest:
        raise ValueError("Migration bundle digest mismatch")
    if not os.access(bundle, os.X_OK):
        bundle.chmod(0o755)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    identity = sub.add_parser("identity")
    identity.add_argument("--revision", required=True)
    identity.add_argument("--output")

    build = sub.add_parser("build")
    build.add_argument("--revision", required=True)
    build.add_argument("--directory", required=True)
    build.add_argument("--output")

    verify = sub.add_parser("verify")
    verify.add_argument("--revision", required=True)
    verify.add_argument("--directory", required=True)

    args = parser.parse_args()
    if args.command == "identity":
        data = {
            "artifact": artifact_name(args.revision),
            "identity": package_identity(args.revision),
            "contract": contract_hash(),
        }
    elif args.command == "build":
        manifest = build_all(args.revision, Path(args.directory))
        data = {
            "artifact": artifact_name(args.revision),
            "identity": manifest["packageIdentity"],
            "contract": manifest["packageContractSha256"],
        }
    else:
        verify_all(args.revision, Path(args.directory))
        print("Validated immutable release package verified.")
        return

    if args.output:
        with open(args.output, "a") as out:
            for key, value in data.items():
                out.write(f"{key}={value}\n")
    print(json.dumps(data, sort_keys=True))


if __name__ == "__main__":
    main()
