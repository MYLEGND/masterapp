#!/usr/bin/env python3
"""Canonical pre-publication resource fanout for LEGEND releases.

One target owns one Azure app-settings transaction. Shared authorization,
marketing, editor, and routing requirements are merged in memory before any write,
so overlapping configuration authorities never race on the same app. Different
apps reconcile concurrently. Database migration is a separate resource lane and
runs concurrently with target configuration.

No configuration values or secret-derived hashes are persisted in the result.
Durable first-write safety remains owned by release-child-receipt.py and
release-migration.py.
"""
from __future__ import annotations

import concurrent.futures
import importlib.util
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys
import urllib.request


ROOT = Path(__file__).resolve().parents[1]


def release_authority():
    spec = importlib.util.spec_from_file_location("release_execution_authority", ROOT / "scripts/validation-resume.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def env_json(name, default):
    raw = os.environ.get(name)
    if not raw:
        return default
    value = json.loads(raw)
    return value


def run(command, *, capture=False, timeout=300, env=None):
    result = subprocess.run(
        command,
        cwd=ROOT,
        env=env,
        capture_output=capture,
        text=True,
        timeout=timeout,
        check=False,
    )
    if result.returncode:
        raise RuntimeError("Canonical pre-publication command failed")
    return result.stdout if capture else ""


def az_json(*args):
    raw = run(["az", *args, "-o", "json"], capture=True, timeout=120)
    value = json.loads(raw)
    if not isinstance(value, list) and not isinstance(value, dict):
        raise RuntimeError("Azure observation returned an invalid shape")
    return value


def app_settings(app):
    rows = az_json(
        "webapp", "config", "appsettings", "list",
        "-g", os.environ["RELEASE_RESOURCE_GROUP"],
        "-n", app,
    )
    if not isinstance(rows, list):
        raise RuntimeError("Azure app settings observation is invalid")
    result = {}
    for row in rows:
        if isinstance(row, dict) and isinstance(row.get("name"), str):
            result[row["name"]] = row.get("value") or ""
    return result


def first_value(settings, *keys):
    for key in keys:
        value = settings.get(key)
        if value:
            return value
    return ""


def existing_key(settings, keys, fallback):
    return next((key for key in keys if key in settings), fallback)


def require(value, label):
    if not value:
        raise RuntimeError("Canonical source setting is missing: " + label)
    return value


def mask(value):
    if value:
        print("::add-mask::" + value, flush=True)


def write_github_env(name, value):
    path = os.environ.get("GITHUB_ENV")
    if not path:
        raise RuntimeError("GitHub environment file is unavailable")
    if "\n" in value or "\r" in value:
        raise RuntimeError("Invalid multiline release environment value")
    with open(path, "a", encoding="utf-8") as output:
        output.write(f"{name}={value}\n")


def child_receipt(child, target=None, partition=None, *, prepare=False):
    command = [sys.executable, "scripts/release-child-receipt.py", child]
    if target is not None:
        command += ["--target", target]
    if partition is not None:
        command += ["--partition", partition]
    if prepare:
        command.append("--prepare")
    run(command, timeout=240)


def cloudflare_account(token, zone_id):
    configured = os.environ.get("CONFIGURED_CLOUDFLARE_ACCOUNT_ID", "")
    if configured:
        return configured
    request = urllib.request.Request(
        f"https://api.cloudflare.com/client/v4/zones/{zone_id}",
        headers={
            "Authorization": "Bearer " + token,
            "User-Agent": "LEGEND-release-prepublication/1.0",
        },
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        payload = json.load(response)
    account = ((payload.get("result") or {}).get("account") or {}).get("id", "")
    if payload.get("success") is not True or not account:
        raise RuntimeError("Cloudflare account authority could not be resolved")
    return account


def routing_authority(preserve_live):
    if os.environ.get("WEBSITE_ROUTING") != "true":
        return None
    token = require(os.environ.get("PRIMARY_CLOUDFLARE_TOKEN", ""), "Cloudflare API token")
    mask(token)
    primary = app_settings(require(os.environ.get("ROUTING_PRIMARY", ""), "routing primary"))
    zone_id = first_value(primary, "WebsiteDomains__CloudflareZoneId", "WebsiteDomains:CloudflareZoneId")
    if not re.fullmatch(r"[A-Fa-f0-9]{32}", zone_id or ""):
        raise RuntimeError("Configured Cloudflare SaaS zone ID is missing or invalid")
    account_id = cloudflare_account(token, zone_id)
    if not re.fullmatch(r"[A-Za-z0-9_-]{16,64}", account_id or ""):
        raise RuntimeError("Cloudflare account ID is invalid")
    bridge = first_value(primary, "WebsiteRouting__BridgeSecret", "WebsiteRouting:BridgeSecret")
    if not bridge:
        if preserve_live:
            raise RuntimeError("Preserve-live routing recovery requires the existing bridge secret")
        bridge = secrets.token_hex(32)
    if len(bridge) < 32:
        raise RuntimeError("Website routing bridge secret is missing or too short")
    for value in (zone_id, account_id, bridge):
        mask(value)
    write_github_env("CLOUDFLARE_API_TOKEN", token)
    write_github_env("CLOUDFLARE_ZONE_ID", zone_id)
    write_github_env("CLOUDFLARE_ACCOUNT_ID", account_id)
    write_github_env("LEGEND_WEBSITE_BRIDGE_SECRET", bridge)
    return {
        "bridgeSecret": bridge,
        "targets": {
            row["releaseName"]: row
            for row in env_json("ROUTING_TARGETS", [])
            if isinstance(row, dict)
            and isinstance(row.get("releaseName"), str)
            and isinstance(row.get("azureHost"), str)
        },
    }


def source_authority(shared_targets, marketing_targets, editor_targets):
    if not (shared_targets or marketing_targets or editor_targets):
        return {}
    source = app_settings(require(os.environ.get("DATABASE_AUTHORITY", ""), "database authority"))
    values = {}
    if shared_targets:
        values["founder"] = require(
            first_value(source, "FOUNDER_OID", "FounderOid", "Founder__Oid", "Founder:Oid"),
            "Founder OID",
        )
        values["founderUpn"] = require(
            first_value(source, "Founder__Upn", "Founder:Upn"),
            "Founder UPN",
        )
        values["ingest"] = require(
            first_value(
                source,
                "Analytics__SharedSecret", "Analytics:SharedSecret",
                "LeadIngest__SharedSecret", "LeadIngest:SharedSecret",
            ),
            "analytics shared secret",
        )
    if shared_targets or marketing_targets:
        values["dataBlob"] = require(
            first_value(source, "DataProtection__BlobUri", "DataProtection:BlobUri"),
            "data protection blob",
        )
        values["dataKey"] = require(
            first_value(source, "DataProtection__KeyVaultKeyId", "DataProtection:KeyVaultKeyId"),
            "data protection key",
        )
    if editor_targets:
        values["editorBlob"] = require(
            first_value(
                source,
                "WebsiteEditorDataProtection__BlobUri", "WebsiteEditorDataProtection:BlobUri",
                "DataProtection__BlobUri", "DataProtection:BlobUri",
            ),
            "editor data protection blob",
        )
        values["editorKey"] = require(
            first_value(
                source,
                "WebsiteEditorDataProtection__KeyVaultKeyId", "WebsiteEditorDataProtection:KeyVaultKeyId",
                "DataProtection__KeyVaultKeyId", "DataProtection:KeyVaultKeyId",
            ),
            "editor data protection key",
        )
    for value in values.values():
        mask(value)
    return values


def add_expected(desired, partition_keys, settings, aliases, fallback, expected, partition=None):
    key = existing_key(settings, aliases, fallback)
    previous = desired.get(key)
    if previous is not None and previous != expected:
        raise RuntimeError("Conflicting canonical settings authority for " + key)
    desired[key] = expected
    if partition is not None:
        partition_keys.setdefault(partition, set()).add(key)
    return key


def configure_target(app, *, preserve_live, shared_targets, marketing_targets, editor_targets,
                     source, routing):
    settings = app_settings(app)
    desired = {}
    partition_keys = {}

    if not preserve_live and app in shared_targets:
        add_expected(
            desired, partition_keys, settings,
            ("FOUNDER_OID", "FounderOid", "Founder__Oid", "Founder:Oid"),
            "FOUNDER_OID", source["founder"], ("shared-config", "authorization"),
        )
        add_expected(
            desired, partition_keys, settings,
            ("Founder__Upn", "Founder:Upn"),
            "Founder__Upn", source["founderUpn"], ("shared-config", "authorization"),
        )
        add_expected(
            desired, partition_keys, settings,
            ("DataProtection__BlobUri", "DataProtection:BlobUri"),
            "DataProtection__BlobUri", source["dataBlob"], ("shared-config", "authorization"),
        )
        add_expected(
            desired, partition_keys, settings,
            ("DataProtection__KeyVaultKeyId", "DataProtection:KeyVaultKeyId"),
            "DataProtection__KeyVaultKeyId", source["dataKey"], ("shared-config", "authorization"),
        )
        add_expected(
            desired, partition_keys, settings,
            ("WEBSITE_NODE_DEFAULT_VERSION",),
            "WEBSITE_NODE_DEFAULT_VERSION", "~24", ("shared-config", "authorization"),
        )
        for key in ("Analytics__SharedSecret", "Tracking__SharedSecret"):
            add_expected(
                desired, partition_keys, settings, (key,), key, source["ingest"],
                ("shared-config", "authorization"),
            )
        for alias in (
            "Analytics:SharedSecret",
            "LeadIngest:SharedSecret",
            "LeadIngest__SharedSecret",
            "Tracking:SharedSecret",
            "TRACKING_SHARED_SECRET",
        ):
            if alias in settings:
                add_expected(
                    desired, partition_keys, settings, (alias,), alias, source["ingest"],
                    ("shared-config", "authorization"),
                )

    if not preserve_live and app in marketing_targets:
        add_expected(
            desired, partition_keys, settings,
            ("MarketingDataProtection__BlobUri", "MarketingDataProtection:BlobUri"),
            "MarketingDataProtection__BlobUri", source["dataBlob"], ("shared-config", "marketing"),
        )
        add_expected(
            desired, partition_keys, settings,
            ("MarketingDataProtection__KeyVaultKeyId", "MarketingDataProtection:KeyVaultKeyId"),
            "MarketingDataProtection__KeyVaultKeyId", source["dataKey"], ("shared-config", "marketing"),
        )

    if not preserve_live and app in editor_targets:
        add_expected(
            desired, partition_keys, settings,
            ("WebsiteEditorDataProtection__BlobUri", "WebsiteEditorDataProtection:BlobUri"),
            "WebsiteEditorDataProtection__BlobUri", source["editorBlob"], ("editor-config", "editor"),
        )
        add_expected(
            desired, partition_keys, settings,
            ("WebsiteEditorDataProtection__KeyVaultKeyId", "WebsiteEditorDataProtection:KeyVaultKeyId"),
            "WebsiteEditorDataProtection__KeyVaultKeyId", source["editorKey"], ("editor-config", "editor"),
        )

    routing_row = (routing or {}).get("targets", {}).get(app)
    if routing_row is not None:
        bridge = routing["bridgeSecret"]
        add_expected(
            desired, partition_keys, settings,
            ("WebsiteRouting__BridgeSecret", "WebsiteRouting:BridgeSecret"),
            "WebsiteRouting__BridgeSecret", bridge,
        )
        add_expected(
            desired, partition_keys, settings,
            ("WebsiteRouting__BridgeOriginHost", "WebsiteRouting:BridgeOriginHost"),
            "WebsiteRouting__BridgeOriginHost", routing_row["azureHost"],
        )

    changes = {key: value for key, value in desired.items() if settings.get(key) != value}
    routing_keys = {
        key for key in desired
        if key.startswith("WebsiteRouting__") or key.startswith("WebsiteRouting:")
    }
    if preserve_live and any(key in changes for key in routing_keys):
        raise RuntimeError("Preserve-live routing recovery found canonical settings drift")

    prepared = []
    if changes:
        for (child, partition), keys in partition_keys.items():
            if any(key in changes for key in keys):
                child_receipt(child, app, partition, prepare=True)
                prepared.append((child, partition))
        args = [
            "az", "webapp", "config", "appsettings", "set",
            "-g", os.environ["RELEASE_RESOURCE_GROUP"],
            "-n", app,
            "--settings",
            *[f"{key}={value}" for key, value in sorted(changes.items())],
            "--output", "none",
        ]
        run(args, timeout=180)
        settings = app_settings(app)

    for key, expected in desired.items():
        if settings.get(key) != expected:
            raise RuntimeError("Canonical settings verification failed for " + app + ":" + key)

    for child, partition in partition_keys:
        child_receipt(child, app, partition)

    return {
        "target": app,
        "changedKeys": sorted(changes),
        "preparedPartitions": [f"{child}:{partition}" for child, partition in prepared],
        "routing": routing_row is not None,
    }


def configure_all_targets():
    preserve_live = os.environ.get("PRESERVE_LIVE_TARGETS") == "true"
    selected = set(env_json("SELECTED_TARGETS", []))
    shared = set(env_json("SHARED_AUTH_TARGETS", [])) & selected
    marketing = set(env_json("MARKETING_TARGETS", [])) & selected
    editor = set(env_json("EDITOR_TARGETS", [])) & selected
    if preserve_live:
        shared.clear()
        marketing.clear()
        editor.clear()

    routing = routing_authority(preserve_live)
    routing_targets = set((routing or {}).get("targets", {}))
    source = source_authority(shared, marketing, editor)
    targets = sorted(shared | marketing | editor | routing_targets)
    if not targets:
        return {"targets": [], "changedTargets": []}

    results = {}
    with concurrent.futures.ThreadPoolExecutor(max_workers=min(8, len(targets))) as pool:
        futures = {
            pool.submit(
                configure_target,
                app,
                preserve_live=preserve_live,
                shared_targets=shared,
                marketing_targets=marketing,
                editor_targets=editor,
                source=source,
                routing=routing,
            ): app
            for app in targets
        }
        for future in concurrent.futures.as_completed(futures):
            app = futures[future]
            results[app] = future.result()

    if shared or marketing:
        child_receipt("shared-config")
    if editor:
        child_receipt("editor-config")

    ordered = [results[app] for app in targets]
    return {
        "targets": ordered,
        "changedTargets": [
            row["target"] for row in ordered if row["changedKeys"]
        ],
    }


def git_ok(*args):
    return subprocess.run(
        ["git", *args],
        cwd=ROOT,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    ).returncode == 0


def changed_migrations(base, revision):
    raw = run(
        ["git", "diff", "--name-only", base, revision, "--", "Infrastructure/Migrations"],
        capture=True,
    )
    return sorted(
        line.strip()
        for line in raw.splitlines()
        if re.fullmatch(r"Infrastructure/Migrations/[0-9]{14}_.+\.cs", line.strip())
        and not line.strip().endswith(".Designer.cs")
    )


def release_proven(base, authority):
    path = ROOT / "scripts" / "release-lifecycle.py"
    spec = importlib.util.spec_from_file_location("release_lifecycle", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if not module.release_proven(module.GitHub(), base, app=authority):
        raise RuntimeError("Live database baseline lacks a successful canonical release receipt")


def run_migration_lane():
    if os.environ.get("PRESERVE_LIVE_TARGETS") == "true":
        return {"status": "skipped"}
    if os.environ.get("SELECTED_DATABASE_DEPENDENT") != "true":
        return {"status": "skipped"}

    base = require(os.environ.get("EXPECTED_DB_BASE_SHA", ""), "database baseline")
    revision = require(os.environ.get("APPLICATION_RELEASE_SHA", ""), "application release SHA")
    if git_ok("merge-base", "--is-ancestor", base, revision):
        pass
    elif git_ok("merge-base", "--is-ancestor", revision, base):
        if not git_ok("diff", "--quiet", revision, base, "--"):
            raise RuntimeError("Live database merge alias contains source drift")
    else:
        raise RuntimeError("Live database baseline and application revision diverged")

    changed = changed_migrations(base, revision)
    if not changed:
        return {"status": "not-applicable", "changedMigrations": []}

    release_proven(base, require(os.environ.get("DATABASE_AUTHORITY", ""), "database authority"))
    run([
        sys.executable, "scripts/release-package.py", "verify",
        "--revision", revision,
        "--directory", "/tmp/diagnostics-packages",
    ], timeout=180)

    plan_raw = run([
        sys.executable, "scripts/migration-probe-package.py", "resolve",
        "--tool-revision", run(["git", "rev-parse", "HEAD"], capture=True).strip(),
        "--application-revision", revision,
        "--directory", "/tmp/migration-probe",
    ], capture=True, timeout=180)
    plan = json.loads(plan_raw)
    run([
        "gh", "run", "download", str(plan["runId"]),
        "--repo", os.environ["GITHUB_REPOSITORY"],
        "--name", plan["artifact"],
        "--dir", "/tmp/migration-probe",
    ], timeout=240)
    run([
        sys.executable, "scripts/migration-probe-package.py", "verify",
        "--tool-revision", run(["git", "rev-parse", "HEAD"], capture=True).strip(),
        "--application-revision", revision,
        "--directory", "/tmp/migration-probe",
    ], timeout=180)
    _invoke_migration_bundle()
    return {"status": "reconciled", "changedMigrations": changed}



def _invoke_migration_bundle():
    # The owning migration runner emits only fixed redacted terminal labels.
    # Preserve the exact safe label rather than masking it behind a generic
    # prepublication RuntimeError. No retry, write, or provider detail is added.
    observation = subprocess.run(
        [sys.executable, "scripts/release-migration.py"],
        cwd=ROOT, capture_output=True, text=True, timeout=900, check=False,
    )
    if observation.returncode:
        reason = observation.stderr.strip()
        suffix = '; preserve prior evidence and reconcile without replay.'
        authorized = {
            'Migration stage unresolved: ' + stage
            for stage in ('schema-observation', 'child-history', 'mutation-admission',
                          'bundle-execution', 'schema-verification', 'success-receipt',
                          'preparation')
        }
        # Reuse the canonical schema-observation classification authority,
        # never trust free-form error text or URLs from subprocess output.
        authorized.update(_approved_observation_labels())
        if reason.endswith(suffix) and reason[:-len(suffix)] in authorized:
            if reason[:-len(suffix)] == (
                'Database contains applied migration history absent from validated bundle'
            ):
                # The read-only probe is the sole identity producer. Forward
                # only one fully validated, bounded schema-metadata line.
                lines = observation.stdout.strip().splitlines()
                if len(lines) == 1 and re.fullmatch(
                    r'LEGEND_SCHEMA_HISTORY:[1-9][0-9]{0,3}:'
                    r'(?:[0-9]{8,14}_[A-Za-z0-9_]{1,128}|NONCANONICAL)'
                    r'(?:,(?:[0-9]{8,14}_[A-Za-z0-9_]{1,128}|NONCANONICAL)){0,15}',
                    lines[0],
                ):
                    print(lines[0], flush=True)
            raise RuntimeError('LEGEND_PREPUBLICATION_MIGRATION:'
                               + reason[:-len(suffix)]) from None
        raise RuntimeError('LEGEND_PREPUBLICATION_MIGRATION:UNKNOWN_FAILURE') from None


def _approved_observation_labels():
    spec = importlib.util.spec_from_file_location(
        'release_migration_safe_labels', ROOT / 'scripts/release-migration.py')
    migration = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(migration)
    return migration.OBSERVATION_ERRORS


def main():
    release_authority().assert_protected_release_execution()
    output = Path(os.environ.get("RELEASE_PREPUBLICATION_RESULT", "/tmp/release-prepublication.json"))
    output.parent.mkdir(parents=True, exist_ok=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        config_future = pool.submit(configure_all_targets)
        migration_future = pool.submit(run_migration_lane)
        # Observe both independent outcomes. Previously config_future.result()
        # could mask a concurrent migration failure and vice versa.
        outcomes = {}
        faults = {}
        for lane, future in (('CONFIGURATION', config_future), ('MIGRATION', migration_future)):
            try:
                outcomes[lane] = future.result()
            except Exception as exc:
                faults[lane] = exc
    if faults:
        for lane in ('CONFIGURATION', 'MIGRATION'):
            if lane not in faults:
                print('LEGEND_PREPUBLICATION:' + lane + ':READY', flush=True)
                continue
            exc = faults[lane]
            # Only an exact message created by the local migration owner may
            # be exposed; configuration/provider exceptions remain opaque.
            reason = str(exc)
            if lane == 'MIGRATION' and type(exc) is RuntimeError and (
                reason.startswith('LEGEND_PREPUBLICATION_MIGRATION:')
            ) and len(reason) <= 200 and (
                reason == 'LEGEND_PREPUBLICATION_MIGRATION:UNKNOWN_FAILURE'
                or reason.removeprefix('LEGEND_PREPUBLICATION_MIGRATION:') in
                   {'Migration stage unresolved: ' + s for s in
                    ('schema-observation', 'child-history', 'mutation-admission',
                     'bundle-execution', 'schema-verification', 'success-receipt',
                     'preparation')}
                or reason.removeprefix('LEGEND_PREPUBLICATION_MIGRATION:') in
                   _approved_observation_labels()
            ):
                print(reason, file=sys.stderr, flush=True)
            else:
                print('LEGEND_PREPUBLICATION:' + lane + ':FAILED',
                      file=sys.stderr, flush=True)
        raise RuntimeError('Pre-publication lanes require exact reconciliation') from None
    configuration = outcomes['CONFIGURATION']
    migration = outcomes['MIGRATION']
    result = {
        "schemaVersion": 1,
        "configuration": configuration,
        "migration": migration,
    }
    output.write_text(json.dumps(result, sort_keys=True, indent=2) + "\n")
    print(json.dumps({
        "configuredTargets": [row["target"] for row in configuration["targets"]],
        "changedTargets": configuration["changedTargets"],
        "migrationStatus": migration["status"],
    }, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        # Provider and credential-bearing subprocess details intentionally stay opaque.
        print("Canonical pre-publication resource reconciliation failed: " + type(exc).__name__, file=sys.stderr)
        raise SystemExit(1)
