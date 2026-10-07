#!/usr/bin/env python3
"""Fail-closed validation resumption for LEGEND GitHub Actions.

The canonical gate model preserves independently successful children from trusted
PR producers when source, execution and toolchain-policy content identities are
equivalent. A new commit, branch, parent failure or receipt-only workflow change
is not itself invalidation. Unknown dependencies or missing proof fail closed.
"""
from __future__ import annotations

import argparse
import ast
import functools
import concurrent.futures
import fnmatch
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import urllib.error


TRUSTED_PR_BASE = "legend/approved-changes"
DIRECT_RELEASE_WORKFLOW = "all-intentional-direct-release-20260918.yml"
PACKAGE_VALIDATION_WORKFLOW = "masterapp-platform-architecture-validation.yml"
RELEASE_REQUEST_PATH = "Docs/releases/direct-release-request.json"
RELEASE_RESOURCE_GROUP = "masterapp-rg"
MIGRATION_BUNDLE_NAME = "masterapp-migrations"
ROUTING_WORKER_NAME = "legend-business-website-router"
ROUTING_PASS_THROUGH_HOSTS = (
    "mylegnd.com",
    "www.mylegnd.com",
    "protect.mylegnd.com",
    "portal.mylegnd.com",
    "client.mylegnd.com",
)
DOMAIN_REFRESH_PROJECT = "scripts/DomainReleaseRefresh/DomainReleaseRefresh.csproj"

# Release children use the same content-based evidence authority as validation.
# Mutable provider reality is reconciled by each child before a retained receipt
# can suppress a write; a receipt alone never proves current configuration.
DIRECT_RELEASE_CHILDREN = {
    "founder-cloudflare": {
        "step": "Run independent auxiliary release fanout",
        "paths": ("Legend-Cloudflare/", "scripts/deploy-founder-cloudflare.py", "scripts/release-auxiliary.py"),
        "operation_paths": ("Legend-Cloudflare/src/", "Legend-Cloudflare/wrangler.founder-baseline.jsonc", "Legend-Cloudflare/package.json", "Legend-Cloudflare/package-lock.json"),
        "operation_exclusions": ("Legend-Cloudflare/src/website-routing/",),
    },
    "migrations": {
        "step": "Synchronize canonical pre-publication resource lanes",
        "paths": (
            "scripts/MigrationReleaseProbe/",
            "scripts/release-migration.py",
            "scripts/release-prepublication.py",
        ),
    },
    "shared-config": {
        "step": "Synchronize canonical pre-publication resource lanes",
        "paths": ("scripts/release-child-receipt.py", "scripts/release-prepublication.py"),
    },
    "editor-config": {
        "step": "Synchronize canonical pre-publication resource lanes",
        "paths": ("scripts/release-child-receipt.py", "scripts/release-prepublication.py"),
    },
    "routing-cloudflare": {
        "step": "Run independent auxiliary release fanout",
        "paths": ("Legend-Cloudflare/", "scripts/cloudflare-routing-authority.py", "scripts/release-router.py", "scripts/release-auxiliary.py"),
        "operation_paths": ("Legend-Cloudflare/src/website-routing/", "Legend-Cloudflare/wrangler.website-routing.jsonc", "Legend-Cloudflare/package.json", "Legend-Cloudflare/package-lock.json"),
    },
    "live-proof": {
        "step": "Verify every deployed target and collect all failures",
        "paths": ("scripts/release-child-receipt.py",),
    },
}


def direct_child_operation_identity(child, revision, material_identity):
    """Physical desired operation survives changes to its verifier/control plane."""
    gate = DIRECT_RELEASE_CHILDREN[child]
    if not re.fullmatch(r"[a-f0-9]{64}", material_identity):
        raise ValueError("Invalid release child material identity")
    raw = subprocess.run(["git", "ls-tree", "-r", "-z", revision], check=True, capture_output=True).stdout.decode()
    sources = {}
    for entry in raw.split("\0"):
        if entry:
            meta, path = entry.split("\t", 1)
            if direct_child_operation_matches(child, path):
                sources[path] = meta
    return hashlib.sha256(json.dumps(dict(child=child, materialIdentity=material_identity,
        sources=sources), sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def direct_child_operation_matches(child, path):
    gate = DIRECT_RELEASE_CHILDREN[child]
    def includes(prefix):
        return path == prefix or (prefix.endswith("/") and path.startswith(prefix))
    return (any(includes(prefix) for prefix in gate.get("operation_paths", ())) and
            not any(includes(prefix) for prefix in gate.get("operation_exclusions", ())))


def direct_child_identity(child, revision, material_identity):
    """Bind a child to its inputs and owning executable block, not its SHA.

    material_identity is a public immutable package/schema/provider resource
    identity, never a configuration value or digest of a secret.
    """
    gate = DIRECT_RELEASE_CHILDREN[child]
    if not re.fullmatch(r"[a-f0-9]{64}", material_identity):
        raise ValueError("Invalid release child material identity")
    raw = subprocess.run(["git", "ls-tree", "-r", "-z", revision],
                         check=True, capture_output=True).stdout.decode()
    inputs = {}
    for entry in raw.split("\0"):
        if not entry:
            continue
        meta, path = entry.split("\t", 1)
        if any(path == prefix or (prefix.endswith("/") and path.startswith(prefix))
               for prefix in gate["paths"]):
            inputs[path] = meta
    text = git_show_file(revision, ".github/workflows/" + DIRECT_RELEASE_WORKFLOW)
    block = named_step_blocks(text).get(gate["step"])
    if not block:
        raise ValueError("Canonical release child owner missing")
    contract = dict(child=child, definition=gate, inputs=inputs,
                    executionContract=block, materialIdentity=material_identity)
    return hashlib.sha256(json.dumps(contract, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def release_child_history(repository, child, dependency_identity, token, phase):
    """Read independently durable children even when their release parent failed."""
    import tempfile
    if child not in DIRECT_RELEASE_CHILDREN or phase not in {"intent", "success"}:
        raise ValueError("Unknown release child or phase")
    if not re.fullmatch(r"[a-f0-9]{64}", dependency_identity):
        raise ValueError("Invalid release child identity")
    name = f"legend-release-child-{phase}-{dependency_identity}"
    payload = api_get(repository, "actions/artifacts?name=" + urllib.parse.quote(name, safe="") + "&per_page=100", token)
    rows = payload.get("artifacts")
    if not isinstance(rows, list) or payload.get("total_count", 0) > len(rows):
        raise RuntimeError("Release child history incomplete")
    records = []
    for artifact in rows:
        if artifact.get("expired") or artifact.get("name") != name:
            raise RuntimeError("Release child evidence missing or expired")
        run_id = artifact.get("workflow_run", {}).get("id")
        run = api_get(repository, f"actions/runs/{run_id}", token)
        if (run.get("path") != ".github/workflows/" + DIRECT_RELEASE_WORKFLOW or
                run.get("head_branch") != TRUSTED_PR_BASE or run.get("event") != "workflow_dispatch" or
                run.get("head_repository", {}).get("full_name", "").lower() != repository.lower()):
            raise RuntimeError("Untrusted release child producer")
        with tempfile.TemporaryDirectory(prefix="legend-child-") as temporary:
            _download_run_artifact(repository, run_id, name, Path(temporary))
            path = Path(temporary) / "operation.json"
            if path.stat().st_size > 32768:
                raise RuntimeError("Oversized release child evidence")
            record = json.loads(path.read_text())
        if (record.get("schemaVersion") != 1 or record.get("child") != child or
                record.get("dependencyIdentity") != dependency_identity or
                record.get("phase") != phase or record.get("producingRun") != run_id):
            raise RuntimeError("Release child receipt identity mismatch")
        _validate_child_generation(repository, run, artifact, record, child, token, phase)
        records.append(record)
    if records and any(row != records[0] for row in records[1:]):
        raise RuntimeError("Competing release child receipts")
    return records[0] if records else None


def _trusted_child_producer(repository, run):
    if (type(run.get("id")) is not int or run["id"] < 1
            or run.get("path") != ".github/workflows/" + DIRECT_RELEASE_WORKFLOW
            or run.get("head_branch") != TRUSTED_PR_BASE
            or run.get("event") != "workflow_dispatch"
            or run.get("head_repository", {}).get("full_name", "").lower() != repository.lower()
            or not re.fullmatch(r"[a-f0-9]{40}", run.get("head_sha", ""))):
        raise RuntimeError("Release child execution history has an untrusted producer")


def _validate_child_generation(repository, run, artifact, record, child, token, phase="intent"):
    """Bind a retained physical operation to its actual public material identity."""
    _trusted_child_producer(repository, run)
    run_id = run["id"]
    identity = record.get("dependencyIdentity", "")
    material = record.get("materialIdentity", "")
    execution = record.get("executionAuthority", "")
    attempt = record.get("producingAttempt")
    if (record.get("schemaVersion") != 1 or record.get("child") != child
            or record.get("phase") != phase or record.get("producingRun") != run_id
            or type(attempt) is not int or attempt < 1 or attempt > run.get("run_attempt", 1)
            or not re.fullmatch(r"[a-f0-9]{64}", identity)
            or not re.fullmatch(r"[a-f0-9]{64}", material)
            or not re.fullmatch(r"[a-f0-9]{40}", execution)
            or artifact.get("name") != f"legend-release-child-{phase}-" + identity):
        raise RuntimeError("Historical release child generation is malformed")
    title = re.fullmatch(r"LEGEND release pr=[0-9]+ candidate=[a-f0-9]{40} authority=([a-f0-9]{40})", run.get("display_title", ""))
    if title is None or title.group(1) != execution:
        raise RuntimeError("Historical release child generation lacks bound checkout authority")
    comparison = api_get(repository, f"compare/{execution}...{run['head_sha']}", token)
    if comparison.get("status") not in {"ahead", "identical"}:
        raise RuntimeError("Historical release child authority is not protected event ancestry")
    if direct_child_operation_identity(child, execution, material) != identity:
        raise RuntimeError("Historical release child physical identity is inconsistent")
    return identity


_RELEASE_CHILD_NOOP_PROOFS = set()


def _legacy_migration_noop(repository, run, job, step, source, token):
    """Prove the exact retired migration step exited before any schema operation."""
    import datetime
    legacy = 'Apply additive diagnostics migrations before restarting apps'
    block = named_step_blocks(_job_blocks(source).get('release', '')).get(legacy, '')
    if (hashlib.sha256(block.encode()).hexdigest() != '74500e6966d2c198564712b33c93a1a06150d99bdc3e4aacc4e79fe11ad061cc'
        or job.get('status') != 'completed' or type(job.get('id')) is not int
        or step.get('name') != legacy or step.get('status') != 'completed'
        or step.get('conclusion') != 'success'):
        return False
    release = _job_blocks(source).get('release', '')
    context = {'environment': _workflow_top_level_field(source, 'env'),
               'defaults': _workflow_top_level_field(source, 'defaults'),
               'runtime': release.split('    steps:', 1)[0]}
    if hashlib.sha256(json.dumps(context, sort_keys=True).encode()).hexdigest() != '0b2c73c15828a62dc7440c79fe6ae3ac072af6a2442f1f5d52f31eeebd79629a':
        return False
    try:
        start = datetime.datetime.fromisoformat(step['started_at'].replace('Z', '+00:00'))
        end = datetime.datetime.fromisoformat(step['completed_at'].replace('Z', '+00:00'))
        if start.tzinfo is None or end.tzinfo is None or end < start:
            return False
    except (KeyError, TypeError, ValueError, AttributeError):
        return False
    key = (repository, job['id'], run['head_sha'], step['started_at'], step['completed_at'])
    if key in _RELEASE_CHILD_NOOP_PROOFS:
        return True
    raw = _release_job_log(repository, job['id'], token)
    text = '\n'.join(re.sub(r'^\d{4}-\d{2}-\d{2}T[0-9:.]+Z ', '', line) for line in raw.splitlines())
    heads = set(re.findall(r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$', text))
    if heads != {run['head_sha']}:
        return False
    marker = 'No candidate migration source changed from the database baseline; migration receipt gate is not applicable.'
    hits = 0
    for line in raw.splitlines():
        stamp, separator, message = line.partition(' ')
        if not separator or message != marker:
            continue
        try:
            observed = datetime.datetime.fromisoformat(stamp.replace('Z', '+00:00'))
        except ValueError:
            return False
        if observed.tzinfo is None or not start <= observed < end + datetime.timedelta(seconds=1):
            return False
        hits += 1
    if hits != 1:
        return False
    _RELEASE_CHILD_NOOP_PROOFS.add(key)
    return True


def release_child_first_write_proven(repository, child, dependency_identity, material_identity,
                                     current_run, current_attempt, token, *, partition_identity=None):
    """Authorize first mutation only from complete positive execution evidence.

    Deleted/expired receipts never establish absence. A started historical child
    must have an authenticated different *physical* operation generation. Its
    verifier revision, title or current caller material cannot stand in for the
    original operation's immutable material.
    """
    gate = DIRECT_RELEASE_CHILDREN[child]
    partition_identity = partition_identity or material_identity
    if (not re.fullmatch(r"[a-f0-9]{64}", partition_identity)
            or not re.fullmatch(r"[a-f0-9]{64}", dependency_identity)
            or not re.fullmatch(r"[a-f0-9]{64}", material_identity)
            or type(current_run) is not int or current_run < 1
            or type(current_attempt) is not int or current_attempt < 1):
        raise ValueError("Invalid release child mutation identity")
    workflow = urllib.parse.quote(DIRECT_RELEASE_WORKFLOW, safe="")
    seen = 0
    for page in range(1, 11):
        payload = api_get(repository, f"actions/workflows/{workflow}/runs?per_page=100&page={page}", token)
        runs = payload.get("workflow_runs")
        if not isinstance(runs, list):
            raise RuntimeError("Release child execution history unavailable")
        seen += len(runs)
        for run in runs:
            if run.get("head_branch") != TRUSTED_PR_BASE or run.get("event") != "workflow_dispatch":
                continue
            _trusted_child_producer(repository, run)
            run_id = run["id"]
            attempts = run.get("run_attempt", 1)
            if type(attempts) is not int or attempts < 1:
                raise RuntimeError("Release child attempt history unavailable")
            if run_id == current_run and current_attempt == 1:
                continue
            inventory = None
            for attempt in range(1, attempts + 1):
                if run_id == current_run and attempt == current_attempt:
                    continue
                jobs_payload = api_get(repository, f"actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100", token)
                jobs = jobs_payload.get("jobs")
                count = jobs_payload.get("total_count")
                if (not isinstance(jobs, list) or type(count) is not int or count != len(jobs)
                    or any(not isinstance(job, dict) for job in jobs)):
                    raise RuntimeError("Release child execution history incomplete")
                if release_attempt_never_entered(jobs):
                    continue
                if any(job.get('name') == 'admission' for job in jobs):
                    source = _release_history_source(repository, run['head_sha'],
                        '.github/workflows/' + DIRECT_RELEASE_WORKFLOW, token)
                    if release_attempt_never_entered(jobs, source):
                        continue
                owners = [job for job in jobs if job.get("name") == "release"]
                if len(owners) != 1:
                    raise RuntimeError("Release child owner unproven")
                job = owners[0]
                if job.get("status") == "queued" or job.get("conclusion") == "skipped":
                    continue
                steps = [step for step in job.get("steps", []) if step.get("name") == gate["step"]]
                if not steps and child == 'migrations':
                    source = _release_history_source(repository, run['head_sha'],
                        '.github/workflows/' + DIRECT_RELEASE_WORKFLOW, token)
                    legacy = 'Apply additive diagnostics migrations before restarting apps'
                    block = named_step_blocks(_job_blocks(source).get('release', '')).get(legacy, '')
                    # Exact retired serial migration owner. Recognition only maps
                    # its execution evidence; entered work still requires the
                    # original authenticated intent and partition disposition.
                    if hashlib.sha256(block.encode()).hexdigest() == '74500e6966d2c198564712b33c93a1a06150d99bdc3e4aacc4e79fe11ad061cc':
                        steps = [step for step in job.get('steps', []) if step.get('name') == legacy]
                if len(steps) != 1:
                    raise RuntimeError("Release child execution detail unavailable")
                if steps[0].get("status") == "queued" or steps[0].get("conclusion") == "skipped":
                    continue
                if child == 'migrations' and steps[0].get('name') != gate['step']:
                    if _legacy_migration_noop(repository, run, job, steps[0], source, token):
                        continue
                if inventory is None:
                    artifact_payload = api_get(repository, f"actions/runs/{run_id}/artifacts?per_page=100", token)
                    inventory = artifact_payload.get("artifacts")
                    if not isinstance(inventory, list) or artifact_payload.get("total_count", 0) > len(inventory):
                        raise RuntimeError("Release child original generation inventory incomplete")
                generations = []
                for artifact in inventory:
                    if not re.fullmatch(r"legend-release-child-intent-[a-f0-9]{64}", artifact.get("name", "")):
                        continue
                    # Any expired intent can conceal this child's original write.
                    if artifact.get("expired"):
                        raise RuntimeError("Release child original intent expired; no replay authorized")
                    record = _release_history_json(repository, run_id, artifact, "operation.json")
                    if record.get("child") != child:
                        continue
                    generation = _validate_child_generation(repository, run, artifact, record, child, token)
                    if record["producingAttempt"] == attempt:
                        generations.append(generation)
                        partition = record.get("partitionIdentity")
                        if not isinstance(partition, str) or not re.fullmatch(r"[a-f0-9]{64}", partition):
                            raise RuntimeError("Release child historical partition unproven; no replay authorized")
                        if partition == partition_identity and generation != dependency_identity:
                            completed = release_child_history(repository, child, generation, token, "success")
                            if (completed is None or completed.get("partitionIdentity") != partition
                                    or completed.get("materialIdentity") != record.get("materialIdentity")):
                                raise RuntimeError("Release child prior partition operation unresolved; no replay authorized")
                if not generations:
                    raise RuntimeError("Release child may have written; missing intent cannot authorize replay")
                if dependency_identity in generations:
                    raise RuntimeError("Release child physical operation already entered; reconcile without replay")
        if len(runs) < 100:
            if payload.get("total_count", seen) > seen:
                raise RuntimeError("Release child history truncated; no mutation authorized")
            return True
    raise RuntimeError("Release child history truncated; no mutation authorized")

# Single canonical web release inventory. Validation, release baseline discovery,
# deployment reconciliation, live-resume probing, package naming and final
# enforcement consume this exact definition instead of maintaining parallel maps.
RELEASE_TARGETS = {
    "portal": {
        "releaseName": "masterapp-portal",
        "host": "portal.mylegnd.com",
        "azureHost": "masterapp-portal.azurewebsites.net",
        "project": "AgentPortal/AgentPortal.csproj",
        "sourceRoot": "AgentPortal",
        "roles": ("database-authority", "database-dependent", "shared-settings-source", "marketing-settings-target"),
        "package": "agentportal.zip",
        "provenancePath": "/api/runtime-provenance",
        "proofHosts": ("portal.mylegnd.com", "masterapp-portal.azurewebsites.net"),
        "static": False,
    },
    "client": {
        "releaseName": "masterapp-client",
        "host": "client.mylegnd.com",
        "azureHost": "masterapp-client.azurewebsites.net",
        "project": "ClientApp/ClientApp.csproj",
        "sourceRoot": "ClientApp",
        "roles": ("browser-entry", "marketing-settings-target"),
        "package": "clientapp.zip",
        "provenancePath": "/api/runtime-provenance",
        "proofHosts": ("client.mylegnd.com", "masterapp-client.azurewebsites.net"),
        "static": False,
    },
    "protect": {
        "releaseName": "masterapp-protect",
        "host": "masterapp-protect.azurewebsites.net",
        "azureHost": "masterapp-protect.azurewebsites.net",
        "project": "Protect-Website/ProtectWebsite.csproj",
        "sourceRoot": "Protect-Website",
        "roles": ("database-dependent", "shared-auth-target", "editor-target", "marketing-settings-target", "routing-target", "routing-primary", "public-release"),
        "routingProbePath": "/",
        "package": "protect.zip",
        "provenancePath": "/api/runtime-provenance",
        "proofHosts": ("masterapp-protect.azurewebsites.net",),
        "static": False,
    },
    "parfait": {
        "releaseName": "masterapp-parfait",
        "host": "masterapp-parfait.azurewebsites.net",
        "azureHost": "masterapp-parfait.azurewebsites.net",
        "project": "ParfaitApp/ParfaitApp.csproj",
        "sourceRoot": "ParfaitApp",
        "roles": ("database-dependent", "editor-target", "marketing-settings-target", "routing-target"),
        "routingProbePath": "/store",
        "package": "parfait.zip",
        "provenancePath": "/api/runtime-provenance",
        "proofHosts": ("masterapp-parfait.azurewebsites.net",),
        "static": False,
    },
    "website": {
        "releaseName": "masterapp-website",
        "host": "masterapp-website.azurewebsites.net",
        "azureHost": "masterapp-website.azurewebsites.net",
        "project": "static",
        "sourceRoot": "Legend-Website",
        "roles": ("static-target", "public-release"),
        "package": "website.zip",
        "provenancePath": "/_deployment-provenance.txt",
        "proofHosts": ("masterapp-website.azurewebsites.net", "mylegnd.com", "www.mylegnd.com"),
        "static": True,
    },
}

def release_name_map():
    return {row["releaseName"]: key for key, row in RELEASE_TARGETS.items()}


def target_keys_with_role(role: str):
    return tuple(
        key for key, row in RELEASE_TARGETS.items()
        if role in row.get("roles", ())
    )


def unique_target_with_role(role: str):
    keys = target_keys_with_role(role)
    if len(keys) != 1:
        raise ValueError(f"Canonical release role {role!r} must resolve to exactly one target")
    return keys[0]


def release_runtime_profile(selected_names):
    keys = selected_release_target_keys(selected_names)
    selected = set(keys)
    def names_for(role):
        return [
            RELEASE_TARGETS[key]["releaseName"]
            for key in target_keys_with_role(role)
        ]
    def selected_has(role):
        return any(key in selected for key in target_keys_with_role(role))

    database_key = unique_target_with_role("database-authority")
    browser_keys = target_keys_with_role("browser-entry")
    static_keys = target_keys_with_role("static-target")
    routing_primary = unique_target_with_role("routing-primary")
    return {
        "resourceGroup": RELEASE_RESOURCE_GROUP,
        "migrationBundle": MIGRATION_BUNDLE_NAME,
        "routingWorker": ROUTING_WORKER_NAME,
        "routingPassThroughHosts": list(ROUTING_PASS_THROUGH_HOSTS),
        "domainRefreshProject": DOMAIN_REFRESH_PROJECT,
        "databaseAuthority": RELEASE_TARGETS[database_key]["releaseName"],
        "browserEntryTargets": names_for("browser-entry"),
        "browserEntryHosts": [
            host
            for key in browser_keys
            for host in RELEASE_TARGETS[key]["proofHosts"]
        ],
        "sharedAuthTargets": names_for("shared-auth-target"),
        "editorTargets": names_for("editor-target"),
        "marketingTargets": names_for("marketing-settings-target"),
        "routingTargets": [
            {
                "releaseName": RELEASE_TARGETS[key]["releaseName"],
                "azureHost": RELEASE_TARGETS[key]["azureHost"],
                "probePath": RELEASE_TARGETS[key]["routingProbePath"],
            }
            for key in target_keys_with_role("routing-target")
        ],
        "routingPrimary": RELEASE_TARGETS[routing_primary]["releaseName"],
        "routingPrimaryHost": RELEASE_TARGETS[routing_primary]["azureHost"],
        "selectedHasBrowserEntry": selected_has("browser-entry"),
        "selectedHasStatic": selected_has("static-target"),
        "selectedHasSharedAuth": selected_has("shared-auth-target"),
        "selectedHasEditor": selected_has("editor-target"),
        "selectedDatabaseDependent": selected_has("database-dependent"),
        "clientOnly": len(keys) == 1 and keys[0] in browser_keys,
        "websiteOnly": len(keys) == 1 and keys[0] in static_keys,
        "portalOnly": len(keys) == 1 and keys[0] == database_key,
        "publicOnly": bool(keys) and all("public-release" in RELEASE_TARGETS[key].get("roles", ()) for key in keys),
    }


def release_admission_resources(paths, selected_names, *, routing=False):
    """Read/write resource ownership used by every release admission.

    Inventory roles describe actual runtime reads and selected-target settings
    writes. Shared source or unknown ownership already expands target scope in
    release_targets_for_paths; shared schema and Cloudflare writes additionally
    conflict with consumers even when app names alone would be disjoint.
    """
    profile = release_runtime_profile(selected_names)
    keys = selected_release_target_keys(selected_names)
    resources = {'write/app/' + RELEASE_TARGETS[key]['releaseName'] for key in keys}
    if any(not RELEASE_TARGETS[key]['static'] for key in keys):
        resources.add('read/schema/masterapp')
    if profile['selectedHasSharedAuth'] or profile['selectedHasEditor'] or profile['selectedDatabaseDependent']:
        resources.add('read/app/' + profile['databaseAuthority'])
    if any(path.startswith('Infrastructure/Migrations/') for path in paths):
        resources.add('write/schema/masterapp')
    if founder_cloudflare_release_required(paths):
        resources.add('write/cloudflare/founder')
        resources.add('write/app/' + profile['databaseAuthority'])
    if routing:
        resources.add('write/cloudflare/router')
        resources.update('write/app/' + row['releaseName'] for row in profile['routingTargets'])
    # A write subsumes its own read, retaining one canonical resource spelling.
    return sorted(value for value in resources
                  if not value.startswith('read/') or 'write/' + value[5:] not in resources)


def release_resources_overlap(left, right):
    def modes(values):
        result = {}
        for value in values:
            mode, separator, resource = value.partition('/')
            if separator != '/' or mode not in {'read', 'write'} or not resource:
                raise ValueError('Malformed release resource ownership')
            result[resource] = 'write' if mode == 'write' or result.get(resource) == 'write' else 'read'
        return result
    lhs, rhs = modes(left), modes(right)
    return any(lhs[key] == 'write' or rhs[key] == 'write' for key in lhs.keys() & rhs.keys())


def selected_release_target_keys(names, *, allow_empty=False):
    by_name = release_name_map()
    if (
        not isinstance(names, list)
        or (not allow_empty and not names)
        or len(names) != len(set(names))
        or any(not isinstance(name, str) or name not in by_name for name in names)
    ):
        raise ValueError("Selected release targets do not match canonical inventory")
    selected = set(names)
    return tuple(
        key for key, row in RELEASE_TARGETS.items()
        if row["releaseName"] in selected
    )


def founder_cloudflare_release_required(paths):
    """Publication depends on physical Worker inputs, never verifier-only edits."""
    return any(direct_child_operation_matches("founder-cloudflare", path)
               for path in dict.fromkeys(paths))


def release_targets_for_paths(paths):
    """Derive publication scope from the validated PR without a parallel scope table.

    A target owns its canonical sourceRoot. Founder Cloudflare runtime/deploy
    authority is activated through the Founder Portal control boundary, so it
    selects only the Portal target rather than expanding to every application.
    Other release-control/test-only changes need no application publication. Any
    remaining application path not owned by exactly one target is treated as
    shared/unknown and expands fail-closed to the complete inventory so a new
    shared source cannot be silently omitted.
    """
    unique_paths = tuple(dict.fromkeys(paths))
    founder_cloudflare = founder_cloudflare_release_required(unique_paths)
    application_paths = [
        path for path in unique_paths
        if not release_control_only_path(path)
        and not (
            path.startswith("Legend-Cloudflare/src/")
            or path == "Legend-Cloudflare/wrangler.founder-baseline.jsonc"
        )
    ]

    selected = {"portal"} if founder_cloudflare else set()
    if not application_paths:
        return tuple(
            row["releaseName"] for key, row in RELEASE_TARGETS.items()
            if key in selected
        )

    for path in application_paths:
        owners = [
            key for key, row in RELEASE_TARGETS.items()
            if path == row["sourceRoot"] or path.startswith(row["sourceRoot"] + "/")
        ]
        if len(owners) != 1:
            return tuple(row["releaseName"] for row in RELEASE_TARGETS.values())
        selected.add(owners[0])
    return tuple(
        row["releaseName"] for key, row in RELEASE_TARGETS.items()
        if key in selected
    )

LIFECYCLE_AUTHORITY_PATHS = (
    "AGENTS.md",
    "DEPLOYMENT.md",
    ".claude/settings.local.json",
    "deploy-portal.sh",
    "AgentPortal/deploy-live-zipdeploy.json",
    ".github/workflows/deployment-diagnostics.yml",
    ".github/workflows/legend-production-readonly-diagnostic.yml",
    ".github/workflows/legend-release-lifecycle.yml",
    ".github/workflows/all-intentional-direct-release-20260918.yml",
    ".github/workflows/masterapp-platform-architecture-validation.yml",
    ".github/workflows/approved-release-security-validation.yml",
    ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
    ".github/workflows/step6-openai-ads-execution-validation.yml",
    ".github/workflows/steps7-8-governed-advertising-validation.yml",
    "scripts/release-lifecycle.py",
    "scripts/release_policy.py",
    "scripts/approved-release-baseline.py",
    "scripts/deploy-approved-app.py",
    "scripts/release-package.py",
    "scripts/validation-resume.py",
    "scripts/test-validation-resume.py",
    "scripts/test-release-policy.py",
    "scripts/test-release-lifecycle.py",
    "scripts/test-deploy-approved-app.py",
    "scripts/deploy-founder-cloudflare.py",
    "scripts/wake-release-lifecycle.py",
)

RELEASE_EXECUTION_CONTROL_INPUTS = (
    "scripts/migration-probe-package.py",
    "scripts/release-prepublication.py",
    "scripts/release-child-receipt.py",
    "scripts/release-router.py",
    "scripts/cloudflare-routing-authority.py",
    "scripts/release-auxiliary.py",
    "scripts/test-release-children.py",
    "scripts/MigrationReleaseProbe/**",
    "scripts/release-migration.py",
    "scripts/test-release-migration.py",
    "scripts/release-workflow.py",
    "scripts/test-release-workflow.py",
    "scripts/release-operation-evidence.py",
    "scripts/test-release-operation-evidence.py",
    "scripts/release-artifacts/**",
)

# Application identity excludes release/test/control-only edits. This authority is
# shared by release baseline resolution and package-canary preservation.
RELEASE_CONTROL_ONLY_EXACT = frozenset({
    "AGENTS.md",
    ".github/CODEOWNERS",
    ".github/copilot-instructions.md",
    "DEPLOYMENT.md",
    ".claude/settings.local.json",
    "deploy-portal.sh",
    "AgentPortal/deploy-live-zipdeploy.json",
    "scripts/approved-release-baseline.py",
    "scripts/release-lifecycle.py",
    "scripts/release_policy.py",
    "scripts/deploy-approved-app.py",
    "scripts/release-package.py",
    "scripts/validation-resume.py",
    "scripts/test-validation-resume.py",
    "scripts/test-release-policy.py",
    "scripts/test-release-lifecycle.py",
    "scripts/test-deploy-approved-app.py",
    "scripts/deploy-founder-cloudflare.py",
    "scripts/wake-release-lifecycle.py",
})

PACKAGE_AUTHORITY_PATHS = frozenset({
    "scripts/release-package.py",
    ".config/dotnet-tools.json",
    ".github/workflows/masterapp-platform-architecture-validation.yml",
})


def release_control_authority_path(path: str) -> bool:
    """One canonical predicate for code that can change validation or publication truth."""
    return (
        path in LIFECYCLE_AUTHORITY_PATHS
        or path in PACKAGE_AUTHORITY_PATHS
        or matches(path, RELEASE_EXECUTION_CONTROL_INPUTS)
    )


PACKAGE_BUILD_WORKFLOW = '.github/workflows/masterapp-platform-architecture-validation.yml'
PACKAGE_COMPONENT_BUILD_STEPS = (
    'Checkout exact package component authority',
    'Setup .NET for canonical package component build',
    'Setup Node for canonical static package component build',
    'Build immutable validated release package component',
)
PACKAGE_ASSEMBLY_BUILD_STEPS = (
    'Checkout exact package authority',
    'Load immutable package components',
    'Build immutable validated release package',
)


def package_builder_workflow_contract(text: str) -> str:
    """Hash the complete fan-out/fan-in byte-production execution envelope.

    Planning and artifact retention are not byte producers. The isolated component
    matrix and the single assembly barrier are: their job configuration, checkout,
    toolchains, component set, download shape and build commands all remain package
    identity inputs so concurrency cannot create an alternate packaging authority.
    """
    jobs = _job_blocks(text)
    component = jobs.get('validated-release-package-components')
    assembly = jobs.get('validated-release-package')
    if component is None or assembly is None:
        raise ValueError('Canonical package fan-out/fan-in jobs missing')

    def through(block, required):
        steps = named_step_blocks(block)
        if any(name not in steps for name in required):
            raise ValueError('Canonical package builder steps missing')
        lines, spans = _named_step_spans(block)
        end = next(finish for name, start, finish in spans if name == required[-1])
        return ''.join(lines[:end]).rstrip() + '\n'

    header = text.split('\njobs:', 1)[0]
    execution = []
    header_lines = header.splitlines(keepends=True)
    for i, line in enumerate(header_lines):
        if re.match(r'^(env|defaults):', line):
            end = i + 1
            while end < len(header_lines) and (not header_lines[end].strip() or header_lines[end][0].isspace()):
                end += 1
            execution.append(''.join(header_lines[i:end]))
    execution.append(through(component, PACKAGE_COMPONENT_BUILD_STEPS))
    execution.append(through(assembly, PACKAGE_ASSEMBLY_BUILD_STEPS))
    return ''.join(execution)


def release_control_only_path(path: str) -> bool:
    return (
        path.startswith(".github/workflows/")
        or path.startswith(".github/agents/")
        or path.startswith("scripts/MigrationReleaseProbe/")
        or path == "scripts/migration-probe-package.py"
        or path.startswith("Docs/")
        or path.startswith("AgentPortal.Tests/")
        or path.startswith("tests/")
        or path in RELEASE_CONTROL_ONLY_EXACT
        or matches(path, RELEASE_EXECUTION_CONTROL_INPUTS)
    )


def package_canary_input_path(path: str) -> bool:
    return path in PACKAGE_AUTHORITY_PATHS or not release_control_only_path(path)

WORKFLOW_PATHS = {
    name: ".github/workflows/" + name
    for name in (
        "masterapp-platform-architecture-validation.yml",
        "step5-isolated-conversion-mapping-validation.yml",
        "step6-openai-ads-execution-validation.yml",
        "steps7-8-governed-advertising-validation.yml",
        "approved-release-security-validation.yml",
    )
}

GLOBAL_DOTNET_INPUTS = (
    "MASTERAPP.sln",
    "global.json",
    "NuGet.config",
    "Directory.Build.*",
    "Directory.Packages.*",
    "**/*.csproj",
    "**/*.props",
    "**/*.targets",
)

WEB_DOTNET_SOURCE = (
    "AgentPortal/**",
    "ClientApp/**",
    "Protect-Website/**",
    "ParfaitApp/**",
    "Infrastructure/**",
    "Domain/**",
    "SHARED/**",
)

WEBSITE_SOURCE = (
    "Infrastructure/WebsiteEditing/**",
    "Infrastructure/WebsiteRuntime/**",
    "Infrastructure/Businesses/**",
    "AgentPortal/Controllers/**Website*",
    "AgentPortal/Services/**Website*",
    "AgentPortal/Views/**Website*",
    "ClientApp/Controllers/**Website*",
    "ClientApp/Views/**Website*",
    "Protect-Website/**",
    "SHARED/WebsitePlatform/**",
)

MARKETING_SOURCE = (
    "Infrastructure/Analytics/**",
    "SHARED/Analytics/**",
    "AgentPortal/Controllers/WebsiteAnalyticsController.cs",
    "AgentPortal/Views/WebsiteAnalytics/**",
    "AgentPortal/wwwroot/js/website-analytics.js",
    "AgentPortal/wwwroot/css/website-analytics.css",
)

BOOKING_SOURCE = (
    "Infrastructure/**Booking*",
    "AgentPortal/**Booking*",
    "Protect-Website/**Booking*",
    "Domain/**Booking*",
    "SHARED/**Booking*",
)

CRM_SOURCE = (
    "Infrastructure/Businesses/**",
    "AgentPortal/Controllers/LeadsController.cs",
    "AgentPortal/Controllers/ClientsController.cs",
    "AgentPortal/**CRM*",
    "ClientApp/**CRM*",
    "Domain/**Lead*",
    "Domain/**Client*",
    "SHARED/**Lead*",
    "SHARED/**Client*",
)

DIAGNOSTICS_SOURCE = (
    "AgentPortal/Controllers/FounderDiagnosticsController.cs",
    "AgentPortal/Services/FounderSoftwareRemediationService*.cs",
    "AgentPortal/Services/LegendFounderToolAuthority*.cs",
    "AgentPortal/Services/Engineering/**",
    "Domain/Engineering/**",
    "Infrastructure/Diagnostics/**",
    "SHARED/Diagnostics/**",
    "SHARED/Views/Diagnostics/**",
    "SHARED/wwwroot/js/legend-site-tools.js",
)

DIAGNOSTICS_TESTS = (
    "AgentPortal.Tests/LegendSiteToolBridgeTests.cs",
    "AgentPortal.Tests/FounderRepositoryInspectionTests.cs",
    "AgentPortal.Tests/LegendFounderToolAuthorizationTests.cs",
    "AgentPortal.Tests/FounderSoftwareRepairBatchTests.cs",
    "AgentPortal.Tests/FounderSoftwareRepairCompletionTests.cs",
    "AgentPortal.Tests/FounderRemediationRevocationTests.cs",
    "AgentPortal.Tests/LegendEngineeringControlPlaneTests.cs",
    "AgentPortal.Tests/RuntimeDiagnostics*Tests.cs",
    "AgentPortal.Tests/PageHealthContractTests.cs",
    "AgentPortal.Tests/WebDiagnosticPrivacyTests.cs",
)

WORKFLOWS = {
    "masterapp-platform-architecture-validation.yml": {
        "force_all": (),
        "neutral": (
            "Docs/**",
            "*.md",
            ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
            ".github/workflows/step6-openai-ads-execution-validation.yml",
            ".github/workflows/steps7-8-governed-advertising-validation.yml",
        ),
        "gates": {
            "lifecycle": {
                "step": "Run branch lifecycle safety contracts",
                "paths": (
                    ".github/workflows/legend-release-lifecycle.yml",
                    ".github/workflows/all-intentional-direct-release-20260918.yml",
                    ".github/workflows/approved-release-security-validation.yml",
                    "scripts/release-lifecycle.py",
                    "scripts/validation-resume.py",
                    "scripts/release_policy.py",
                    "scripts/approved-release-baseline.py",
                    "scripts/deploy-approved-app.py",
                    "scripts/release-package.py",
                    "scripts/test-validation-resume.py",
                    "scripts/test-release-lifecycle.py",
                    "scripts/test-release-policy.py",
                    "scripts/test-deploy-approved-app.py",
                ) + RELEASE_EXECUTION_CONTROL_INPUTS,
            },
            "mobile": {
                "step": "Run authenticated mobile authority tests",
                "paths": (
                    "tests/layout/modal-content-region.test.mjs",
                    "tests/layout/app-navigation-contract.test.mjs",
                ),
            },
            "restore-dotnet": {
                "step": "Restore .NET graph",
                "paths": GLOBAL_DOTNET_INPUTS,
            },
            "build-infrastructure": {
                "step": "Build shared infrastructure",
                "paths": ("Infrastructure/**", "Domain/**", "SHARED/**") + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore-dotnet",),
            },
            "build-hosts": {
                "step": "Build AgentPortal and ClientApp hosts",
                "paths": ("AgentPortal/**", "ClientApp/**", "Infrastructure/**", "Domain/**", "SHARED/**") + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore-dotnet",),
            },
            "build-protect": {
                "step": "Build Protect host",
                "paths": ("Protect-Website/**", "Infrastructure/**", "Domain/**", "SHARED/**") + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore-dotnet",),
            },
            "tracking-assets": {
                "step": "Verify Protect serves the exact shared tracking assets",
                "paths": (
                    "SHARED/WebsitePlatform/tracking.js",
                    "SHARED/WebsitePlatform/meta-signal-intelligence.js",
                    "SHARED/WebsitePlatform/openai-measurement.js",
                ),
                "requires": ("restore-dotnet",),
            },
            "renderer-install": {
                "step": "Install shared website renderer dependencies",
                "paths": ("Legend-Website/package.json", "Legend-Website/package-lock.json"),
            },
            "renderer-build": {
                "step": "Build shared website renderer",
                "paths": ("Legend-Website/**", "Legend-Design/**", "SHARED/WebsitePlatform/**"),
                "requires": ("renderer-install",),
            },
            "renderer-parity": {
                "step": "Verify renderer authority parity",
                "paths": ("Legend-Website/**", "Legend-Design/**", "SHARED/WebsitePlatform/**"),
                "requires": ("renderer-build",),
            },
            "renderer-tests": {
                "step": "Run business renderer tests with approved-baseline no-regression proof",
                "paths": (
                    "Legend-Website/scripts/render-business.test.mjs",
                    "Legend-Website/scripts/render-business.mjs",
                    "Legend-Website/package-lock.json",
                    "SHARED/WebsitePlatform/**",
                    "tests/website/legend-public-cms.test.mjs",
                ),
                "requires": ("renderer-install",),
            },
            "cms-install": {
                "step": "Install canonical shared CMS test dependencies",
                "paths": ("tests/website/package.json", "tests/website/package-lock.json"),
            },
            "cms-tests": {
                "step": "Run canonical shared CMS tests",
                "paths": ("tests/website/**", "SHARED/WebsitePlatform/**", "Legend-Design/**"),
                "requires": ("cms-install",),
            },
            "compile-regression": {
                "step": "Compile full regression test project",
                "paths": WEB_DOTNET_SOURCE + ("AgentPortal.Tests/**",) + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore-dotnet",),
                "runtime_file_dependencies": False,
            },
            "founder-diagnostics-regressions": {
                "step": "Run Founder diagnostics and safe GPT Codex regressions",
                "paths": DIAGNOSTICS_SOURCE + DIAGNOSTICS_TESTS,
                "materializes": ("compile-regression",),
            },
            "domain-release": {
                "step": "Compile shared domain release refresh",
                "paths": ("scripts/DomainReleaseRefresh/**", "Domain/**", "Infrastructure/**") + GLOBAL_DOTNET_INPUTS,
            },
            "website-regressions": {
                "step": "Run website ownership and publishing regressions",
                "paths": (
                    "AgentPortal.Tests/WebsitePublishingAuthorityTests.cs",
                    "AgentPortal.Tests/WebsiteContentEditorRoundTripTests.cs",
                    "AgentPortal.Tests/WebsiteEditorTicketAuthorityIsolationTests.cs",
                    "AgentPortal.Tests/WebsiteDomainImportTests.cs",
                    "AgentPortal.Tests/WebsiteInquiryIsolationTests.cs",
                    "AgentPortal.Tests/BusinessWorkspaceTests.cs",
                    "AgentPortal.Tests/BusinessAnalyticsCompletionTests.cs",
                    "AgentPortal.Tests/AnalyticsCanonicalReconciliationTests.cs",
                    "AgentPortal.Tests/AnalyticsPageRoutingTruthTests.cs",
                    "AgentPortal.Tests/WebsiteSiteSourceV3Tests.cs",
                ) + WEBSITE_SOURCE + WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS,
                "exclude_paths": DIAGNOSTICS_SOURCE,
                "materializes": ("compile-regression",),
            },
            "meta-regressions": {
                "step": "Run Meta authority regressions",
                "paths": (
                    "AgentPortal.Tests/Meta*Tests.cs",
                    "AgentPortal.Tests/*Analytics*Tests.cs",
                    "AgentPortal.Tests/Marketing*Tests.cs",
                    "AgentPortal.Tests/OpenAi*Tests.cs",
                    "AgentPortal.Tests/Tracking*Tests.cs",
                    "AgentPortal.Tests/QuoteProductInstrumentationContractTests.cs",
                    "AgentPortal.Tests/ProtectLeadModalInquiryTests.cs",
                ) + MARKETING_SOURCE + WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS,
                "exclude_paths": DIAGNOSTICS_SOURCE,
                "materializes": ("compile-regression",),
            },
            "booking-regressions": {
                "step": "Run booking authority regressions",
                "paths": ("AgentPortal.Tests/*Booking*Tests.cs",) + BOOKING_SOURCE + WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS,
                "exclude_paths": DIAGNOSTICS_SOURCE,
                "materializes": ("compile-regression",),
            },
            "crm-regressions": {
                "step": "Run CRM outcome regressions",
                "paths": (
                    "AgentPortal.Tests/LeadsControllerTests.cs",
                    "AgentPortal.Tests/ProductionControllerTests.cs",
                    "AgentPortal.Tests/WebsiteAnalyticsScopeTests.cs",
                    "AgentPortal.Tests/LaunchAuditRiskAssessmentTests.cs",
                    "AgentPortal.Tests/CanonicalCrmOutcomeLineageTests.cs",
                ) + CRM_SOURCE + WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS,
                "exclude_paths": DIAGNOSTICS_SOURCE,
                "materializes": ("compile-regression",),
            },
            "form-tracking": {
                "step": "Run canonical form tracking tests",
                "paths": (
                    "tests/analytics/form-tracker.test.cjs",
                    "SHARED/WebsitePlatform/tracking.js",
                    "SHARED/WebsitePlatform/meta-signal-intelligence.js",
                    "SHARED/WebsitePlatform/openai-measurement.js",
                ),
            },
            "release-web-contracts": {
                "step": "Run release web contract regressions",
                "paths": (
                    "tests/analytics/csv-export.test.cjs",
                    "tests/analytics/form-tracker.test.cjs",
                    "tests/legend-connect/**",
                    "tests/messaging/message-presentation.test.mjs",
                    "tests/layout/page-health.test.mjs",
                    "Legend-Cloudflare/tests/runtime/**",
                    "Legend-Cloudflare/tests/security/**",
                    "scripts/test-diagnostic-project-impact.py",
                    "scripts/test-sync-published-checkout.py",
                ),
                "materializes": ("compile-regression",),
                "consumes": ("domain-release",),
            },
            "release-policy": {
                "step": "Verify consolidated release scope and routing policy",
                "paths": (
                    "scripts/release_policy.py",
                    "scripts/deploy-approved-app.py",
                    "scripts/release-package.py",
                    "scripts/approved-release-baseline.py",
                    "scripts/test-release-policy.py",
                    "scripts/test-deploy-approved-app.py",
                    "Docs/releases/direct-release-request.json",
                ) + RELEASE_EXECUTION_CONTROL_INPUTS,
            },
        },
    },
    "step5-isolated-conversion-mapping-validation.yml": {
        "unmatched_neutral": False,
        "force_all": (),
        "neutral": (
            "Docs/**",
            "*.md",
            ".github/workflows/masterapp-platform-architecture-validation.yml",
            ".github/workflows/step6-openai-ads-execution-validation.yml",
            ".github/workflows/steps7-8-governed-advertising-validation.yml",
            ".github/workflows/all-intentional-direct-release-20260918.yml",
            ".github/workflows/legend-release-lifecycle.yml",
            "scripts/approved-release-baseline.py",
            "scripts/release-lifecycle.py",
            "scripts/release-package.py",
            "scripts/deploy-approved-app.py",
            "scripts/test-release-lifecycle.py",
            "scripts/test-release-policy.py",
            "scripts/test-deploy-approved-app.py",
            "Legend-Cloudflare/tests/**",
            "tests/**",
        ) + RELEASE_EXECUTION_CONTROL_INPUTS,
        "gates": {
            "candidate-restore": {
                "step": "Restore AgentPortal tests",
                "paths": GLOBAL_DOTNET_INPUTS,
                "group": "candidate",
                "runtime": "dotnet",
            },
            "candidate-build": {
                "step": "Build affected test graph",
                "paths": WEB_DOTNET_SOURCE + ("AgentPortal.Tests/**",) + GLOBAL_DOTNET_INPUTS,
                "requires": ("candidate-restore",),
                "group": "candidate",
                "runtime": "dotnet",
            },
            "candidate-focused": {
                "step": "Run Step 5 focused tests",
                "paths": (
                    "AgentPortal.Tests/OpenAiMeasurementDeliveryTests.cs",
                    "AgentPortal.Tests/MarketingScopeParityContractTests.cs",
                ) + MARKETING_SOURCE + GLOBAL_DOTNET_INPUTS,
                "requires": ("candidate-build",),
                "group": "candidate",
                "runtime": "dotnet",
            },
            "candidate-full": {
                "step": "Run full AgentPortal candidate suite",
                "paths": WEB_DOTNET_SOURCE + ("AgentPortal.Tests/**",) + GLOBAL_DOTNET_INPUTS,
                "requires": ("candidate-build",),
                "group": "candidate",
                "runtime": "dotnet",
                "artifact": "candidate-trx",
            },
            "comparison": {
                "step": "Prove Step 5 adds no full-suite failures",
                "paths": (
                    ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
                    "scripts/validation-resume.py",
                    "scripts/test-validation-resume.py",
                    "scripts/test-release-policy.py",
                ),
                "consumes": ("candidate-full",),
                "group": "comparison",
            },
        },
    },
    "step6-openai-ads-execution-validation.yml": {
        "unmatched_neutral": True,
        "force_all": (
            "SHARED/Analytics/OpenAiAdsExecutionContracts.cs",
            "Infrastructure/Analytics/OpenAiAdsExecutionService.cs",
            "Infrastructure/Analytics/MarketingConnectionStore.cs",
        ),
        "neutral": ("scripts/validation-resume.py", "scripts/test-validation-resume.py"),
        "gates": {
            "restore": {"step": "Restore affected graph", "paths": ()},
            "build": {"step": "Build affected graph", "paths": (), "requires": ("restore",)},
            "tests": {
                "step": "Run Step 6 execution and standing scope-parity tests",
                "paths": (
                    "AgentPortal.Tests/OpenAiAdsExecutionServiceTests.cs",
                    "AgentPortal.Tests/OpenAiAdsAccountConnectionAuthorityTests.cs",
                    "AgentPortal.Tests/MarketingDestinationLayerTests.cs",
                    "AgentPortal.Tests/MarketingScopeParityContractTests.cs",
                ),
                "requires": ("build",),
            },
        },
    },
    "approved-release-security-validation.yml": {
        "unmatched_neutral": True,
        "force_all": (),
        "neutral": ("Docs/**", "*.md", "scripts/validation-resume.py", "scripts/test-validation-resume.py"),
        "gates": {
            "restore": {
                "step": "Restore security validation graph",
                "paths": GLOBAL_DOTNET_INPUTS,
            },
            "build": {
                "step": "Build migration validation graph",
                "paths": (
                    "scripts/db.sh",
                    "Infrastructure/Migrations/**",
                    "Infrastructure/Data/MasterAppDbContext.cs",
                    "Infrastructure/**DbContext*.cs",
                    "Domain/Entities/**",
                ) + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore",),
            },
            "db-validation": {
                "step": "Validate database migration artifacts",
                "paths": (
                    "scripts/db.sh",
                    "Infrastructure/Migrations/**",
                    "Infrastructure/Data/MasterAppDbContext.cs",
                    "Infrastructure/**DbContext*.cs",
                    "Domain/Entities/**",
                ) + GLOBAL_DOTNET_INPUTS,
                "requires": ("build",),
            },
            "no-skips": {
                "step": "Reject skipped security tests",
                "paths": (
                    "AgentPortal.Tests/*Antiforgery*.cs",
                    "AgentPortal.Tests/Phase3IdentityAuthorityTests.cs",
                    "AgentPortal.Tests/Phase4PlatformSecurityTests.cs",
                    "AgentPortal.Tests/Phase5CrossPlatformSecurityTests.cs",
                    "AgentPortal.Tests/Phase6ArchitectureInvariantTests.cs",
                    "AgentPortal.Tests/CalendarControllerTests.cs",
                ),
            },
            "vulnerabilities": {
                "step": "Audit dependency vulnerabilities",
                "paths": GLOBAL_DOTNET_INPUTS,
            },
            "secret-scan": {
                "step": "Scan committed configuration for secrets",
                "paths": ("**/appsettings*.json",),
            },
            "composition": {
                "step": "Verify shared composition authorities",
                "paths": (
                    "AgentPortal/Program.cs",
                    "ClientApp/Program.cs",
                    "Protect-Website/Program.cs",
                    "ParfaitApp/Program.cs",
                    "AgentPortal/Services/LegendFounderAiConversationService.cs",
                ),
            },
            "keyring": {
                "step": "Reject inline Azure key-ring wiring",
                "paths": (
                    "AgentPortal/Program.cs",
                    "ClientApp/Program.cs",
                    "Protect-Website/Program.cs",
                    "ParfaitApp/Program.cs",
                ),
            },
            "diff-check": {
                "step": "Verify patch whitespace integrity",
                "paths": ("**",),
            },
        },
    },
    "steps7-8-governed-advertising-validation.yml": {
        "unmatched_neutral": True,
        "force_all": (
            "Domain/Entities/AdvertisingActionAuthorization.cs",
            "SHARED/Analytics/AdvertisingActionContracts.cs",
            "Infrastructure/Analytics/AdvertisingActionAuthorizationService.cs",
            "Infrastructure/Analytics/MarketingConnectionStore.cs",
            "Infrastructure/WebsiteEditing/PromotionOrchestrationService.cs",
            "Infrastructure/WebsiteEditing/WebsitePlatformController.cs",
            "Infrastructure/Data/MasterAppDbContext.cs",
            "Infrastructure/Migrations/20260927053000_AddAdvertisingActionAuthorizations.cs",
            "Infrastructure/Analytics/AdvertisingCommandCenterService.cs",
            "AgentPortal/Controllers/WebsiteAnalyticsController.cs",
            "AgentPortal/Views/WebsiteAnalytics/Index.cshtml",
            "AgentPortal/wwwroot/js/website-analytics.js",
            "AgentPortal/wwwroot/css/website-analytics.css",
            "Infrastructure/Businesses/BusinessWorkspaceControllerBase.cs",
        ),
        "neutral": ("scripts/validation-resume.py", "scripts/test-validation-resume.py"),
        "gates": {
            "restore": {"step": "Restore affected graph", "paths": ()},
            "build": {"step": "Build affected graph", "paths": (), "requires": ("restore",)},
            "governance-tests": {
                "step": "Run Steps 7-8 governance, promotion, execution, and scope-parity tests",
                "paths": (
                    "AgentPortal.Tests/AdvertisingActionAuthorizationServiceTests.cs",
                    "AgentPortal.Tests/PromotionOrchestrationTests.cs",
                    "AgentPortal.Tests/MarketingScopeParityContractTests.cs",
                    "AgentPortal.Tests/AdvertisingCommandCenterCentralizationTests.cs",
                ),
                "requires": ("build",),
            },
            "website-ui-tests": {
                "step": "Run shared website management UI tests",
                "paths": (
                    "Legend-Design/legend-website-management.js",
                    "tests/website/**",
                ),
            },
        },
    },
}



RELEASE_WORKFLOWS = frozenset({
    "all-intentional-direct-release-20260918.yml",
    "legend-release-lifecycle.yml",
})


def _named_step_spans(text: str):
    lines = text.splitlines(keepends=True)
    rows = []
    for index, line in enumerate(lines):
        stripped = line.lstrip()
        if not stripped.startswith("- name:"):
            continue
        indent = len(line) - len(stripped)
        raw = stripped[len("- name:"):].strip()
        name = raw.strip("'\"")
        end = len(lines)
        for cursor in range(index + 1, len(lines)):
            candidate = lines[cursor]
            candidate_stripped = candidate.lstrip()
            candidate_indent = len(candidate) - len(candidate_stripped)
            if candidate_indent == indent and candidate_stripped.startswith("- "):
                end = cursor
                break
        rows.append((name, index, end))
    return lines, rows


def named_step_blocks(text: str):
    lines, rows = _named_step_spans(text)
    return {name: "".join(lines[start:end]) for name, start, end in rows}


def _mask_named_steps(text: str, names):
    lines, rows = _named_step_spans(text)
    wanted = set(names)
    spans = {start: (name, end) for name, start, end in rows if name in wanted}
    output = []
    cursor = 0
    while cursor < len(lines):
        row = spans.get(cursor)
        if row is None:
            output.append(lines[cursor])
            cursor += 1
            continue
        name, end = row
        indent = " " * (len(lines[cursor]) - len(lines[cursor].lstrip()))
        output.append(f"{indent}- name: __LEGEND_GATE__{name}\\n")
        cursor = end
    return "".join(output)


def _job_blocks(text: str):
    """Return exact top-level job blocks without treating blank lines as EOF.

    GitHub workflow jobs routinely contain blank separators. The previous parser
    treated a bare newline as a non-indented top-level key, stopped after the
    first job, and falsely reported later jobs as missing. That silently defeated
    content-addressed Step 5 baseline reuse.
    """
    lines = text.splitlines(keepends=True)
    jobs_line = next((i for i, line in enumerate(lines) if line.strip() == "jobs:" and not line.startswith(" ")), None)
    if jobs_line is None:
        return {}
    blocks = {}
    index = jobs_line + 1
    while index < len(lines):
        line = lines[index]
        if not line.strip():
            index += 1
            continue
        if not line.startswith(" "):
            break
        if line.startswith("  ") and not line.startswith("    ") and line.strip().endswith(":"):
            name = line.strip()[:-1]
            end = index + 1
            while end < len(lines):
                candidate = lines[end]
                if not candidate.strip():
                    end += 1
                    continue
                if not candidate.startswith(" "):
                    break
                if candidate.startswith("  ") and not candidate.startswith("    ") and candidate.strip().endswith(":"):
                    break
                end += 1
            blocks[name] = "".join(lines[index:end])
            index = end
            continue
        index += 1
    return blocks


def git_show_file(revision: str, path: str) -> str:
    result = subprocess.run(
        ["git", "show", f"{revision}:{path}"],
        check=True,
        text=True,
        capture_output=True,
    )
    return result.stdout


def _dynamic_release_policy(step_name: str, block: str) -> str:
    """Classify a newly added release step conservatively without a second registry.

    Explicit policies remain useful documentation for established external-effect
    steps, but correctness never depends on remembering to extend that registry.
    New named steps are discovered from the workflow itself and default to
    fail-closed execution unless their action shape is intrinsically reusable.
    """
    if "actions/upload-artifact@" in block:
        return "artifact_receipt"
    if "actions/download-artifact@" in block:
        return "artifact_restore"
    if "actions/setup-" in block or "azure/login@" in block:
        return "ephemeral_runtime"
    return "fail_closed_execute"


def verify_release_policy_coverage(workflow_name: str, workflow_text: str):
    if workflow_name not in RELEASE_WORKFLOWS:
        raise ValueError(f"Unsupported release workflow coverage: {workflow_name}")
    blocks = named_step_blocks(workflow_text)
    if not blocks:
        raise ValueError(f"Release workflow has no discoverable named steps: {workflow_name}")
    return {
        name: _dynamic_release_policy(name, block)
        for name, block in blocks.items()
    }


def matches(path: str, patterns) -> bool:
    return any(fnmatch.fnmatchcase(path, pattern) for pattern in patterns)


def _control_project_is_application_dependency(path):
    """Resolve reverse project ownership instead of treating every tool as an app."""
    import xml.etree.ElementTree as ET
    wanted = Path(path).resolve()
    pending = [Path(row["project"]) for row in RELEASE_TARGETS.values() if row["project"] != "static"]
    pending.append(Path("AgentPortal.Tests/AgentPortal.Tests.csproj"))
    seen = set()
    while pending:
        project = pending.pop().resolve()
        if project == wanted:
            return True
        if project in seen:
            continue
        seen.add(project)
        if not project.exists():
            # A missing graph is not permission to omit application dependencies.
            return True
        root = ET.parse(project).getroot()
        for node in root.iter():
            if node.tag.rsplit("}", 1)[-1] == "ProjectReference":
                include = node.get("Include", "").replace("\\", "/")
                if not include or "$" in include or "*" in include:
                    return True
                pending.append(project.parent / include)
    return False


def gate_matches(path: str, gate) -> bool:
    patterns = gate.get("paths", ())
    if path.endswith(".csproj") and matches(path, RELEASE_EXECUTION_CONTROL_INPUTS):
        # Explicit control gates still own the utility; broad .NET build inputs
        # include it only when an actual application/test ProjectReference does.
        patterns = tuple(pattern for pattern in patterns if pattern not in GLOBAL_DOTNET_INPUTS)
        if not matches(path, patterns) and not _control_project_is_application_dependency(path):
            return False
    return matches(path, gate.get("paths", ())) and not matches(path, gate.get("exclude_paths", ()))


def git_changed(prior: str, current: str) -> list[str]:
    if prior == current:
        return []
    result = subprocess.run(
        ["git", "diff", "--name-only", prior, current, "--"],
        check=True,
        text=True,
        capture_output=True,
    )
    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


class EvidenceLookupUnavailable(RuntimeError):
    """Evidence transport failed; do not infer absence or invalidate proof."""
    def __init__(self, message="Evidence read unavailable", *, status=None, endpoint=None, rate=None):
        super().__init__(message)
        self.code = status
        self.endpoint = endpoint
        self.rate = dict(rate or {})


def api_get(repository: str, path: str, token: str):
    url = f"https://api.github.com/repos/{repository}/{path.lstrip('/')}"
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "legend-validation-resume/1.0",
        },
    )
    for attempt in range(3):
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return json.load(response)
        except (TimeoutError, urllib.error.URLError, http.client.RemoteDisconnected) as exc:
            retryable = not isinstance(exc, urllib.error.HTTPError) or exc.code in {408, 429, 500, 502, 503, 504}
            if not retryable or attempt == 2:
                headers = getattr(exc, "headers", None) or {}
                rate = {key: headers.get(key) for key in (
                    "X-RateLimit-Remaining", "X-RateLimit-Reset", "Retry-After")
                    if headers.get(key) is not None}
                if rate:
                    print(json.dumps({"evidenceRateLimit": rate}, sort_keys=True))
                raise EvidenceLookupUnavailable(
                    "GitHub evidence read unavailable",
                    status=getattr(exc, "code", None),
                    endpoint=path.split("?", 1)[0],
                    rate=rate,
                ) from exc
            time.sleep(2 ** attempt)



def assert_protected_release_execution():
    """Require live GitHub evidence that this process belongs to the sole protected release workflow."""
    if os.environ.get("GITHUB_ACTIONS") != "true":
        raise RuntimeError("Production release mutation requires GitHub Actions")
    if os.environ.get("GITHUB_EVENT_NAME") != "workflow_dispatch":
        raise RuntimeError("Production release mutation requires canonical workflow_dispatch")
    expected_ref = "refs/heads/" + TRUSTED_PR_BASE
    if os.environ.get("GITHUB_REF") != expected_ref:
        raise RuntimeError("Production release mutation requires the protected approved branch")
    repository = os.environ.get("GITHUB_REPOSITORY", "")
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN") or ""
    workflow_ref = os.environ.get("GITHUB_WORKFLOW_REF", "")
    sha = os.environ.get("GITHUB_SHA", "")
    run_id_raw = os.environ.get("GITHUB_RUN_ID", "")
    if (
        not repository
        or not token
        or not re.fullmatch(r"[a-f0-9]{40}", sha)
        or not run_id_raw.isdigit()
        or not workflow_ref.endswith(
            f"/.github/workflows/{DIRECT_RELEASE_WORKFLOW}@{expected_ref}"
        )
    ):
        raise RuntimeError("Production release execution identity is incomplete")
    run_id = int(run_id_raw)
    run = api_get(repository, f"actions/runs/{run_id}", token)
    if (
        run.get("id") != run_id
        or run.get("path") != ".github/workflows/" + DIRECT_RELEASE_WORKFLOW
        or run.get("head_branch") != TRUSTED_PR_BASE
        or run.get("event") != "workflow_dispatch"
        or run.get("head_sha") != sha
        or run.get("head_repository", {}).get("full_name", "").lower() != repository.lower()
        or run.get("status") not in {"queued", "in_progress"}
    ):
        raise RuntimeError("Production release mutation is not owned by the protected canonical workflow")
    return run


def approved_head_preflight(repository: str, current_sha: str, token: str):
    """Prove the candidate already contains the exact current approved head.

    This is intentionally cheaper than validation planning. It is the first
    merge-readiness check and may not infer freshness from the PR event's
    possibly stale base SHA.
    """
    if not re.fullmatch(r"[0-9a-f]{40}", current_sha or ""):
        raise ValueError("Malformed candidate revision")
    if not token:
        raise EvidenceLookupUnavailable(
            "GitHub token unavailable for approved-head preflight",
            endpoint="approved-head-preflight",
        )
    branch = api_get(
        repository,
        "branches/" + urllib.parse.quote(TRUSTED_PR_BASE, safe=""),
        token,
    )
    approved = (branch.get("commit") or {}).get("sha")
    if not re.fullmatch(r"[0-9a-f]{40}", approved or ""):
        raise EvidenceLookupUnavailable(
            "Current approved head is unavailable",
            endpoint="branches/" + TRUSTED_PR_BASE,
        )
    if current_sha == approved:
        return {
            "schemaVersion": 1,
            "candidateSha": current_sha,
            "approvedHeadSha": approved,
            "mergeBaseSha": approved,
            "compareStatus": "identical",
            "current": True,
        }
    compare = api_get(
        repository,
        "compare/" + urllib.parse.quote(approved, safe="") + "..." +
        urllib.parse.quote(current_sha, safe=""),
        token,
    )
    merge_base = (compare.get("merge_base_commit") or {}).get("sha")
    status = compare.get("status")
    current = (
        merge_base == approved and
        status in {"ahead", "identical"}
    )
    return {
        "schemaVersion": 1,
        "candidateSha": current_sha,
        "approvedHeadSha": approved,
        "mergeBaseSha": merge_base,
        "compareStatus": status,
        "current": current,
    }


def cmd_approved_head_preflight(args):
    if args.event != "pull_request":
        result = {
            "schemaVersion": 1,
            "candidateSha": args.current_sha,
            "approvedHeadSha": None,
            "mergeBaseSha": None,
            "compareStatus": "not_applicable",
            "current": True,
            "reason": "approved_head_preflight_applies_to_pull_requests_only",
        }
    else:
        result = approved_head_preflight(
            args.repository,
            args.current_sha,
            os.environ.get("GITHUB_TOKEN", ""),
        )
    if args.output:
        Path(args.output).write_text(
            json.dumps(result, indent=2, sort_keys=True) + "\n"
        )
    print(json.dumps(result, sort_keys=True))
    if not result["current"]:
        print(
            "::error::Candidate is not based on the current "
            f"{TRUSTED_PR_BASE} head {result['approvedHeadSha']}. "
            "Trusted lifecycle must sync the approved head before validation.",
            file=sys.stderr,
        )
        raise SystemExit(78)


class _StepEvidence(dict):
    def __init__(self):
        super().__init__()
        self.producers = {}


def _step_map(jobs):
    evidence = _StepEvidence()
    for job in jobs:
        for step in job.get("steps") or []:
            name = step.get("name")
            if name:
                evidence[name] = step.get("conclusion")
                evidence.producers[name] = {
                    "result": step.get("conclusion"), "jobId": job.get("id"),
                    "runId": job.get("run_id"), "stepNumber": step.get("number"),
                }
    return evidence


def _effective_steps(newest_to_oldest):
    """Keep the newest executed result; skipped/missing later steps do not erase proof.

    A later failure/cancellation always wins over an older success. A later run
    that never reached a gate may inherit that gate's most recent executed result
    from the exact same source SHA only.
    """
    effective = _StepEvidence()
    for steps in newest_to_oldest:
        for name, outcome in steps.items():
            if name in effective or outcome in {None, "", "skipped"}:
                continue
            effective[name] = outcome
            if name in getattr(steps, "producers", {}):
                effective.producers[name] = steps.producers[name]
    return effective


def prior_evidence(args):
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        return None, {}, "github_token_unavailable"

    if args.run_attempt > 1:
        histories = []
        newest_attempt = args.run_attempt - 1
        for attempt in range(newest_attempt, 0, -1):
            payload = api_get(
                args.repository,
                f"actions/runs/{args.current_run_id}/attempts/{attempt}/jobs?per_page=100",
                token,
            )
            histories.append(_step_map(payload.get("jobs", [])))
        return {
            "id": args.current_run_id,
            "head_sha": args.current_sha,
            "run_attempt": newest_attempt,
            "event": args.event,
            "head_branch": args.head_branch,
        }, _effective_steps(histories), "prior_attempts"

    workflow = urllib.parse.quote(args.workflow, safe="")
    branch = urllib.parse.quote(args.head_branch, safe="")
    event = urllib.parse.quote(args.event, safe="")
    payload = api_get(
        args.repository,
        f"actions/workflows/{workflow}/runs?branch={branch}&event={event}&status=completed&per_page=100",
        token,
    )
    runs = []
    for run in payload.get("workflow_runs", []):
        if (
            int(run.get("id", 0)) == args.current_run_id
            or run.get("head_branch") != args.head_branch
            or run.get("event") != args.event
            or not run.get("head_sha")
        ):
            continue
        if args.event == "pull_request":
            trusted = _trusted_lineage_run(
                args.repository, run, WORKFLOW_PATHS[args.workflow], args.current_sha
            )
            if not trusted:
                try:
                    trusted = _trusted_pr_run(
                        args.repository, run, WORKFLOW_PATHS[args.workflow], token
                    )
                except urllib.error.HTTPError:
                    trusted = False
            if not trusted:
                continue
        runs.append(run)
    if not runs:
        return None, {}, "no_prior_completed_run"

    ordered = sorted(
        runs,
        key=lambda run: (run.get("created_at", ""), int(run.get("id", 0))),
        reverse=True,
    )
    prior = ordered[0]
    exact_head_runs = [run for run in ordered if run.get("head_sha") == prior.get("head_sha")][:10]
    histories = []
    for run in exact_head_runs:
        steps = _historical_plan_steps(args, run, token)
        if steps:
            histories.append(steps)

    return prior, _effective_steps(histories), "prior_exact_head_plan_artifacts"


def _plan_against_prior(workflow, current_sha, prior, prior_steps, evidence_source):
    """Build the same fail-closed gate plan against one exact historical source tree."""
    changed = git_changed(prior["head_sha"], current_sha) if prior else []
    changed_gate_steps = set()
    workflow_structure_changed = False
    workflow_path = WORKFLOW_PATHS.get(workflow)
    if prior and workflow_path and workflow_path in changed:
        prior_text = git_show_file(prior["head_sha"], workflow_path)
        current_text = Path(workflow_path).read_text()
        try:
            prior_config = _historical_workflow_definition(workflow, prior["head_sha"])
            changed_gate_steps = {
                gate["step"] for key, gate in WORKFLOWS[workflow]["gates"].items()
                if key not in prior_config["gates"] or
                _gate_execution_contract(prior_text, prior_config, key) !=
                _gate_execution_contract(current_text, WORKFLOWS[workflow], key)
            }
        except EvidenceLookupUnavailable:
            raise
        except Exception:
            workflow_structure_changed = True
        changed = [path for path in changed if path != workflow_path]
    plan = compute_plan(
        workflow,
        current_sha,
        prior,
        prior_steps,
        changed,
        evidence_source,
        changed_gate_steps,
        workflow_structure_changed,
    )

    if any(not gate.get("run") for gate in plan["gates"].values()):
        try:
            current_ids = gate_dependency_manifests(workflow, current_sha)
            prior_ids = gate_dependency_manifests(workflow, prior["head_sha"],
                _historical_workflow_definition(workflow, prior["head_sha"]))
            for key, gate in plan["gates"].items():
                if gate.get("run"):
                    continue
                if current_ids[key]["contentIdentity"] != prior_ids.get(key, {}).get("contentIdentity"):
                    gate.update({"run": True, "reason": "dependency_identity_changed"})
                else:
                    gate["dependencyIdentity"] = current_ids[key]["contentIdentity"]
        except EvidenceLookupUnavailable:
            raise
        except Exception:
            for gate in plan["gates"].values():
                if not gate.get("run"):
                    gate.update({"run": True, "reason": "dependency_identity_unproven"})
        _enforce_runtime_requirements(plan)
    for gate in plan["gates"].values():
        if not gate.get("run"):
            gate["producerReceipt"] = getattr(prior_steps, "producers", {}).get(gate["step"],
                {"result": prior_steps.get(gate["step"]), "runId": prior["id"]})
    return plan


def _stamp_evidence(plan, prior, source):
    if not prior:
        return
    for gate in plan.get("gates", {}).values():
        if gate.get("run"):
            continue
        gate["evidenceRunId"] = (gate.get("producerReceipt") or {}).get("runId") or prior.get("id")
        gate["evidenceHeadSha"] = prior.get("head_sha")
        gate["evidenceSource"] = source


def _enforce_runtime_requirements(plan):
    """Preserved proof cannot replace a prerequisite needed by a gate executing now."""
    gates = WORKFLOWS[plan["workflow"]]["gates"]
    changed = True
    while changed:
        changed = False
        for key, result in list(plan["gates"].items()):
            invalidated = next((child for child in gates[key].get("consumes", ())
                                if plan["gates"][child].get("run")), None)
            if invalidated and not result.get("run"):
                result.update({"run": True, "reason": f"evidence_dependency_invalidated:{invalidated}"})
                changed = True
            if not result.get("run"):
                continue
            for required in gates[key].get("requires", ()):
                required_result = plan["gates"][required]
                if required_result.get("run"):
                    continue
                required_result["run"] = True
                required_result["reason"] = f"required_by:{key}"
                required_result.pop("evidenceRunId", None)
                required_result.pop("evidenceHeadSha", None)
                required_result.pop("evidenceSource", None)
                changed = True


def merge_content_equivalent_evidence(plan, candidate_plan, run):
    """Reuse only gates whose exact declared inputs and gate definition are unchanged."""
    reused = False
    for key, result in plan["gates"].items():
        if not result.get("run"):
            continue
        candidate = candidate_plan["gates"].get(key)
        if not candidate or candidate.get("run"):
            continue
        result["run"] = False
        result["reason"] = "content_equivalent_success"
        result["evidenceRunId"] = run.get("id")
        result["evidenceHeadSha"] = run.get("head_sha")
        result["evidenceSource"] = "trusted_pr_history"
        result["producerReceipt"] = candidate.get("producerReceipt")
        result["dependencyIdentity"] = candidate.get("dependencyIdentity")
        reused = True
    return reused


def _trusted_lineage_run(repository, run, workflow_path, revision):
    """Authenticate an immutable producer already in the current candidate lineage.

    This avoids a separate commit->pull API lookup for evidence whose producer
    commit is already part of the exact candidate history. Workflow path, event,
    completion, repository ownership and git ancestry must all agree.
    """
    head = run.get("head_sha") or ""
    if (
        run.get("path") != workflow_path
        or run.get("event") != "pull_request"
        or run.get("status") != "completed"
        or not re.fullmatch(r"[0-9a-f]{40}", head)
        or (run.get("head_repository") or {}).get("full_name") != repository
        or not re.fullmatch(r"[0-9a-f]{40}", revision or "")
    ):
        return False
    return subprocess.run(
        ["git", "merge-base", "--is-ancestor", head, revision],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    ).returncode == 0


def _trusted_pr_run(repository, run, workflow_path, token):
    """Authenticate a producer independently of its parent workflow conclusion."""
    head = run.get("head_sha") or ""
    if (run.get("path") != workflow_path or run.get("event") != "pull_request"
            or run.get("status") != "completed"
            or not re.fullmatch(r"[0-9a-f]{40}", head)
            or (run.get("head_repository") or {}).get("full_name") != repository):
        return False
    def matches_pull(row):
        return ((row.get("base") or {}).get("ref") == TRUSTED_PR_BASE
                and (row.get("head") or {}).get("sha") == head
                and ((row.get("head") or {}).get("repo") or {}).get("full_name") == repository)
    if any(matches_pull(row) for row in run.get("pull_requests") or []):
        return True
    # The commit-associated PR endpoint binds the immutable commit even after
    # that PR advances to a repaired head; demanding its current head equal the
    # old producer SHA would discard valid child evidence on every new commit.
    return any((row.get("base") or {}).get("ref") == TRUSTED_PR_BASE
               and ((row.get("head") or {}).get("repo") or {}).get("full_name") == repository
               for row in api_get(repository, f"commits/{head}/pulls?per_page=100", token))


VALIDATION_RESUME_ARTIFACT_PREFIX = {
    "masterapp-platform-architecture-validation.yml": "validation-resume-architecture",
    "approved-release-security-validation.yml": "validation-resume-security",
    "step6-openai-ads-execution-validation.yml": "validation-resume-step6",
    "steps7-8-governed-advertising-validation.yml": "validation-resume-step78",
}


def _validation_resume_artifact_name(workflow, run):
    prefix = VALIDATION_RESUME_ARTIFACT_PREFIX.get(workflow)
    if not prefix:
        return None
    run_id = int(run.get("id") or 0)
    attempt = int(run.get("run_attempt") or 1)
    return f"{prefix}-{run_id}-{attempt}" if run_id else None


def _historical_plan_steps(args, run, token):
    """Recover gate proof from the durable validation-plan artifact.

    Successful parent completion proves gates the plan actually executed.
    A failed parent requires an exact recorded child observation for executed
    gates, or preserved proof from an older successful producer.
    """
    artifact = _validation_resume_artifact_name(args.workflow, run)
    if artifact is None:
        return None
    run_id = int(run.get("id") or 0)
    if artifact not in _run_artifact_names(args.repository, run_id, token):
        return None
    import tempfile
    with tempfile.TemporaryDirectory(prefix="validation-plan-") as temporary:
        directory = Path(temporary)
        _download_run_artifact(args.repository, run_id, artifact, directory)
        candidates = list(directory.rglob("validation-resume.json"))
        if len(candidates) != 1:
            return None
        stored = json.loads(candidates[0].read_text())

    if stored.get("workflow") != args.workflow:
        return None
    gates = stored.get("gates")
    if not isinstance(gates, dict):
        return None

    steps = _StepEvidence()
    parent_success = run.get("conclusion") == "success"
    for gate in gates.values():
        if not isinstance(gate, dict):
            continue
        step = gate.get("step")
        if not isinstance(step, str) or not step:
            continue
        if gate.get("run") is True:
            receipt = gate.get("receipt") or {}
            job_id = receipt.get("producerJobId")
            step_number = receipt.get("producerStepNumber")
            if (
                stored.get("receiptSchemaVersion") == 1
                and stored.get("recordingRunId") == run_id
                and receipt.get("producingRunId") == run_id
                and receipt.get("reused") is False
                and type(job_id) is int and job_id > 0
                and receipt.get("recordingJobId") == job_id
                and type(step_number) is int and step_number > 0
                and receipt.get("stepNumber") == step_number
                and receipt.get("result") in {"success", "failure", "cancelled", "timed_out"}
            ):
                steps[step] = receipt["result"]
                steps.producers[step] = {
                    "result": receipt["result"],
                    "jobId": job_id,
                    "runId": run_id,
                    "stepNumber": step_number,
                    "artifact": artifact,
                }
                continue
            if not parent_success:
                continue
            steps[step] = "success"
            steps.producers[step] = {
                "result": "success",
                "jobId": None,
                "runId": run_id,
                "stepNumber": None,
                "artifact": artifact,
            }
            continue

        producer = gate.get("receipt") or gate.get("producerReceipt") or {}
        if producer.get("result") != "success":
            continue
        producer_run = (
            producer.get("producingRunId")
            or producer.get("runId")
            or gate.get("evidenceRunId")
        )
        if not producer_run:
            continue
        steps[step] = "success"
        steps.producers[step] = {
            "result": "success",
            "jobId": producer.get("producerJobId") or producer.get("jobId"),
            "runId": int(producer_run),
            "stepNumber": producer.get("producerStepNumber") or producer.get("stepNumber"),
            "artifact": artifact,
        }
    return steps


def _trusted_historical_runs(args, token):
    """Return trusted completed PR parents; each child proves its own success."""
    if args.event != "pull_request":
        return []
    workflow = urllib.parse.quote(args.workflow, safe="")
    payload = api_get(
        args.repository,
        f"actions/workflows/{workflow}/runs?event=pull_request&status=completed&per_page=100",
        token,
    )
    candidates = sorted(
        payload.get("workflow_runs", []),
        key=lambda run: (run.get("updated_at") or run.get("created_at", ""), int(run.get("id", 0))),
        reverse=True,
    )
    rows = []
    for run in candidates:
        if int(run.get("id", 0)) == args.current_run_id:
            continue
        trusted = _trusted_lineage_run(
            args.repository, run, WORKFLOW_PATHS[args.workflow], args.current_sha
        )
        if not trusted:
            try:
                trusted = _trusted_pr_run(
                    args.repository, run, WORKFLOW_PATHS[args.workflow], token
                )
            except urllib.error.HTTPError:
                trusted = False
        if not trusted:
            continue
        rows.append(run)
        # Newer failed parents may salvage exact successful children. Once the
        # nearest trusted successful parent is reached, it is the canonical
        # content baseline; older generations cannot override newer source truth.
        if run.get("conclusion") == "success":
            break
    return rows


def _successful_parent_steps(workflow, run):
    """A successful PR validator proves every gate in that exact parent envelope.

    Individual artifacts are only needed to salvage green children from failed
    parents. Avoiding artifact downloads for successful parents turns history
    reuse into one content-identity comparison instead of an N-run network scan.
    """
    steps = _StepEvidence()
    run_id = int(run.get("id") or 0)
    if run.get("conclusion") != "success" or run_id < 1:
        return steps
    for gate in WORKFLOWS[workflow]["gates"].values():
        step = gate["step"]
        steps[step] = "success"
        steps.producers[step] = {
            "result": "success",
            "jobId": None,
            "runId": run_id,
            "stepNumber": None,
            "artifact": None,
        }
    return steps


def _apply_content_equivalent_evidence(args, plan):
    """Fill unresolved gates from recent successful runs with identical gate inputs.

    This is intentionally an optimization only. Any lookup/diff uncertainty keeps
    the existing plan unchanged, so historical reuse can never weaken validation.
    """
    if args.event != "pull_request" or not any(row.get("run") for row in plan["gates"].values()):
        return plan
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        return plan

    reused = False
    examined = 0
    try:
        runs = _trusted_historical_runs(args, token)
    except EvidenceLookupUnavailable:
        raise
    except Exception as exc:
        plan["historicalEvidenceError"] = type(exc).__name__
        return plan

    for run in runs:
        head_sha = run["head_sha"]
        successful_parent = run.get("conclusion") == "success"
        try:
            steps = (
                _successful_parent_steps(args.workflow, run)
                if successful_parent
                else _historical_plan_steps(args, run, token)
            )
            if not steps:
                examined += 1
                continue
            prior = {
                "id": run["id"],
                "head_sha": head_sha,
                "run_attempt": run.get("run_attempt", 1),
                "event": run.get("event"),
                "head_branch": run.get("head_branch"),
            }
            candidate_plan = _plan_against_prior(
                args.workflow,
                args.current_sha,
                prior,
                steps,
                "trusted_successful_parent" if successful_parent else "trusted_plan_artifact_history",
            )
        except EvidenceLookupUnavailable:
            raise
        except Exception:
            examined += 1
            continue
        examined += 1
        reused = merge_content_equivalent_evidence(plan, candidate_plan, run) or reused
        if not any(row.get("run") for row in plan["gates"].values()):
            break
        # The nearest trusted successful parent is the canonical comparison
        # baseline. Anything whose content identity changed from it is genuinely
        # invalidated and must execute; older generations cannot make a current
        # source change disappear.
        if successful_parent:
            break

    _enforce_runtime_requirements(plan)
    if reused:
        plan["mode"] = "content-addressed"
        plan["historicalEvidenceRunsExamined"] = examined
    return plan


def compute_plan(
    workflow: str,
    current_sha: str,
    prior,
    prior_steps,
    changed_paths,
    evidence_source,
    changed_gate_steps=None,
    workflow_structure_changed=False,
):
    config = WORKFLOWS[workflow]
    gates = config["gates"]
    plan = {
        "schemaVersion": 1,
        "workflow": workflow,
        "currentSha": current_sha,
        "priorRunId": prior.get("id") if prior else None,
        "priorHeadSha": prior.get("head_sha") if prior else None,
        "evidenceSource": evidence_source,
        "changedPaths": changed_paths,
        "mode": "full",
        "gates": {},
    }

    if prior is None:
        for key, gate in gates.items():
            plan["gates"][key] = {
                "step": gate["step"],
                "run": True,
                "reason": "no_prior_success_evidence",
            }
        return plan

    if workflow_structure_changed:
        for key, gate in gates.items():
            plan["gates"][key] = {
                "step": gate["step"],
                "run": True,
                "reason": "workflow_structure_changed",
            }
        return plan

    changed_gate_steps = set(changed_gate_steps or ())
    force_all = any(matches(path, config.get("force_all", ())) for path in changed_paths)
    known = set()
    neutral = set()
    for path in changed_paths:
        if matches(path, config.get("neutral", ())):
            neutral.add(path)
            continue
        for key, gate in gates.items():
            if gate_matches(path, gate):
                known.add(path)
                break

    unknown = [] if config.get("unmatched_neutral") else [
        path
        for path in changed_paths
        if path not in known
        and path not in neutral
        and not matches(path, config.get("force_all", ()))
    ]
    if force_all or unknown:
        reason = "source_change_requires_full_validation" if force_all else "unclassified_change_requires_full_validation"
        for key, gate in gates.items():
            plan["gates"][key] = {
                "step": gate["step"],
                "run": True,
                "reason": reason,
            }
        plan["unclassifiedPaths"] = unknown
        return plan

    run = set()
    reasons = {}
    for key, gate in gates.items():
        step = gate["step"]
        if step in changed_gate_steps:
            run.add(key)
            reasons[key] = "gate_definition_changed"
            continue
        if prior_steps.get(step) != "success":
            run.add(key)
            reasons[key] = "prior_gate_not_successful"
            continue
        if any(gate_matches(path, gate) for path in changed_paths):
            run.add(key)
            reasons[key] = "gate_inputs_changed"

    # Close only real execution/evidence edges. "requires" is a runtime
    # prerequisite and "consumes" is a semantic evidence dependency: both
    # legitimately propagate invalidation. "materializes" is deliberately
    # different; it means a child needs exact producer bytes locally, but a
    # producer rematerialization alone must not invalidate otherwise-green
    # sibling evidence.
    changed = True
    while changed:
        changed = False
        for key in list(run):
            for required in gates[key].get("requires", ()):
                if required not in run:
                    run.add(required)
                    reasons[required] = f"required_by:{key}"
                    changed = True
        for key, gate in gates.items():
            if key in run:
                continue
            invalidated = next(
                (dependency for dependency in gate.get("consumes", ()) if dependency in run),
                None,
            )
            if invalidated:
                run.add(key)
                reasons[key] = f"evidence_dependency_invalidated:{invalidated}"
                changed = True

    for key, gate in gates.items():
        should_run = key in run
        plan["gates"][key] = {
            "step": gate["step"],
            "run": should_run,
            "reason": reasons.get(key, "preserved_prior_success"),
        }
    plan["mode"] = "incremental"
    return plan


def _workflow_top_level_field(text, field):
    """Include block and inline YAML forms in the execution envelope."""
    lines = text.splitlines(keepends=True)
    starts = [i for i, line in enumerate(lines) if re.match(r"^[A-Za-z_][A-Za-z0-9_-]*:", line)]
    for index, start in enumerate(starts):
        if lines[start].startswith(field + ":"):
            end = starts[index + 1] if index + 1 < len(starts) else len(lines)
            return "".join(lines[start:end])
    return ""


def _gate_execution_contract(workflow_text, config, key):
    """Capture one gate's execution prefix and declared runtime prerequisites."""
    gates = config["gates"]
    step = gates[key]["step"]
    owners = [job for job in _job_blocks(workflow_text).values() if step in named_step_blocks(job)]
    if len(owners) != 1:
        raise ValueError("Gate does not have one executable workflow owner")
    job = owners[0]
    lines, spans = _named_step_spans(job)
    end = next(end for name, start, end in spans if name == step)
    prefix = "".join(lines[:end])
    dependencies = {key}
    pending = [key]
    while pending:
        dependency = pending.pop()
        for required in gates[dependency].get("requires", ()):
            if required not in dependencies:
                dependencies.add(required)
                pending.append(required)
    unrelated_steps = {gate["step"] for name, gate in gates.items() if name not in dependencies}
    # Named independent gates are not execution inputs of this gate. Unknown
    # setup/source-mutating commands and all unnamed steps remain fail closed.
    prefix_lines, prefix_spans = _named_step_spans(prefix)
    omitted = {index for name, start, finish in prefix_spans if name in unrelated_steps
               for index in range(start, finish)}
    prefix = "".join(line for index, line in enumerate(prefix_lines) if index not in omitted)
    envelope = []
    for field in ("env", "defaults"):
        envelope.append(_workflow_top_level_field(workflow_text, field))
    return "".join(envelope) + prefix


def _historical_workflow_definition(workflow, revision):
    """Read historical declarative gate data without executing historical code."""
    values = {}
    def data(node):
        if isinstance(node, ast.Constant):
            return node.value
        if isinstance(node, ast.Name):
            return values[node.id]
        if isinstance(node, (ast.Tuple, ast.List, ast.Set)):
            rows = [data(child) for child in node.elts]
            return tuple(rows) if isinstance(node, ast.Tuple) else rows
        if isinstance(node, ast.Dict):
            return {data(key): data(value) for key, value in zip(node.keys, node.values)}
        if isinstance(node, ast.BinOp) and isinstance(node.op, ast.Add):
            return data(node.left) + data(node.right)
        raise ValueError("Historical gate definition is not declarative")
    module = ast.parse(git_show_file(revision, "scripts/validation-resume.py"))
    for statement in module.body:
        if isinstance(statement, ast.Assign) and len(statement.targets) == 1 and isinstance(statement.targets[0], ast.Name):
            name = statement.targets[0].id
            try:
                values[name] = data(statement.value)
            except (ValueError, KeyError, TypeError):
                if name == "WORKFLOWS":
                    raise ValueError("Cannot authenticate historical gate definition")
    return values["WORKFLOWS"][workflow]


def gate_dependency_manifests(workflow, revision, definition=None):
    exact = subprocess.run(["git", "rev-parse", revision + "^{commit}"], check=True, capture_output=True, text=True).stdout.strip()
    return _gate_dependency_manifests(workflow, exact, json.dumps(definition or WORKFLOWS[workflow], sort_keys=True))


@functools.lru_cache(maxsize=128)
def _gate_dependency_manifests(workflow, revision, definition_json):
    """Materialize the existing gate model into auditable content identities.

    These manifests accompany the canonical decision, never substitute a new
    gate registry. Executable workflow/toolchain policy is recorded separately
    from application content; commit identity is provenance, not invalidation.
    """
    config = json.loads(definition_json)
    raw = subprocess.run(["git", "ls-tree", "-r", "-z", revision],
        check=True, capture_output=True).stdout.decode()
    tree = {}
    for entry in raw.split("\0"):
        if entry:
            meta, path = entry.split("\t", 1)
            mode, kind, oid = meta.split()
            tree[path] = {"mode": mode, "kind": kind, "oid": oid}
    workflow_text = git_show_file(revision, WORKFLOW_PATHS[workflow])
    gate_steps = [gate["step"] for gate in config["gates"].values()]
    import io
    import tarfile
    test_tokens = {}
    test_file_patterns = {}
    if any(path.startswith("AgentPortal.Tests/") for path in tree):
        archive = subprocess.run(["git", "archive", revision, "AgentPortal.Tests"], check=True, capture_output=True).stdout
        with tarfile.open(fileobj=io.BytesIO(archive)) as contents:
            for entry in contents.getmembers():
                if entry.isfile() and entry.name.endswith(".cs"):
                    source = contents.extractfile(entry).read().decode("utf-8-sig")
                    test_tokens[entry.name] = set(re.findall(r"[A-Za-z0-9_.-]+", source))
                    test_file_patterns[entry.name] = _test_file_dependency_patterns(source)
    import posixpath
    import xml.etree.ElementTree as ET
    copied_inputs = {}
    project_path = "AgentPortal.Tests/AgentPortal.Tests.csproj"
    if project_path in tree:
        project = ET.fromstring(git_show_file(revision, project_path))
        for node in project.iter():
            include = node.get("Include", "").replace("\\", "/")
            if include and (node.get("CopyToOutputDirectory") or node.find("CopyToOutputDirectory") is not None):
                path = posixpath.normpath("AgentPortal.Tests/" + include)
                link = node.get("Link") or node.findtext("Link") or posixpath.basename(path)
                copied_inputs[path] = posixpath.basename(link)
    result = {}
    for key, gate in config["gates"].items():
        inputs = {path: identity for path, identity in tree.items()
                  if gate_matches(path, gate) or matches(path, config.get("force_all", ()))}
        # Source-contract tests read copied and direct repository files. Preserve
        # those content inputs alongside the C# fixture, even for neutral owners.
        tokens = set()
        dynamic_patterns = set()
        if gate.get("runtime_file_dependencies", True):
            for path in tuple(inputs):
                if path.startswith("AgentPortal.Tests/") and path.endswith(".cs"):
                    tokens.update(test_tokens.get(path, ()))
                    dynamic_patterns.update(test_file_patterns.get(path, ()))
            if tokens:
                inputs.update({path: identity for path, identity in tree.items()
                               if Path(path).name in tokens})
                inputs.update({path: tree[path] for path, alias in copied_inputs.items()
                               if path in tree and alias in tokens})
            if dynamic_patterns:
                inputs.update({path: identity for path, identity in tree.items() if matches(path, dynamic_patterns)})
        source_digest = hashlib.sha256(json.dumps(inputs, sort_keys=True).encode()).hexdigest()
        control = _gate_execution_contract(workflow_text, config, key)
        control_digest = hashlib.sha256(control.encode()).hexdigest()
        definition_digest = hashlib.sha256(json.dumps(gate, sort_keys=True).encode()).hexdigest()
        result[key] = {
            "schemaVersion": 1, "unitId": workflow + ":" + key,
            "parentId": workflow, "sourceRevision": revision,
            "sourceInputs": inputs, "sourceIdentity": source_digest,
            "controlPlanePath": WORKFLOW_PATHS[workflow],
            "executionContractIdentity": control_digest,
            "gateDefinitionIdentity": definition_digest,
            "contentIdentity": hashlib.sha256((source_digest + control_digest + definition_digest).encode()).hexdigest(),
            "requires": list(gate.get("requires", ())),
            "consumes": list(gate.get("consumes", ())),
            "materializes": list(gate.get("materializes", ())),
            "toolchainPolicy": "producer-execution-contract",
        }
    return result


def _gate_cache_payload(plan, workflow, head_sha, run_id, run_attempt):
    if workflow not in WORKFLOWS or plan.get("workflow") != workflow:
        raise ValueError("Validation cache workflow mismatch")
    if not re.fullmatch(r"[0-9a-f]{40}", head_sha or "") or run_id < 1 or run_attempt < 1:
        raise ValueError("Malformed validation cache identity")
    gates = {}
    for key, row in (plan.get("gates") or {}).items():
        if key not in WORKFLOWS[workflow]["gates"] or not isinstance(row, dict):
            continue
        receipt = row.get("receipt") or row.get("producerReceipt") or {}
        if receipt.get("result") == "success":
            gates[key] = {"result": "success"}
    return {
        "schemaVersion": 2,
        "workflow": workflow,
        "headSha": head_sha,
        "runId": run_id,
        "runAttempt": run_attempt,
        "gates": gates,
    }


def _write_gate_cache(plan, workflow, head_sha, run_id, run_attempt, output):
    payload = _gate_cache_payload(plan, workflow, head_sha, run_id, run_attempt)
    path = Path(output)
    if not payload["gates"]:
        if path.exists():
            path.unlink()
        return payload
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n")
    temporary.replace(path)
    return payload


def cmd_record_evidence(args):
    """Enrich the plan and checkpoint successful children even if the parent later fails."""
    plan = json.loads(Path(args.plan).read_text())
    observed = {}
    local_context = os.environ.get("VALIDATION_STEP_CONTEXT")

    if local_context:
        context = json.loads(local_context)
        if not isinstance(context, dict):
            raise ValueError("Validation step context must be an object")
        for key in WORKFLOWS[plan["workflow"]]["gates"]:
            step_id = "gate_" + key.replace("-", "_")
            row = context.get(step_id) or {}
            outcome = row.get("outcome") or row.get("conclusion")
            if outcome:
                observed[WORKFLOWS[plan["workflow"]]["gates"][key]["step"]] = {
                    "result": outcome,
                    "jobId": None,
                    "stepNumber": None,
                }
    else:
        token = os.environ.get("GITHUB_TOKEN", "")
        if not token:
            raise ValueError("Evidence recording requires authenticated producer observations")
        try:
            jobs = api_get(args.repository, f"actions/runs/{args.run_id}/jobs?filter=latest&per_page=100", token).get("jobs", [])
        except (urllib.error.HTTPError, EvidenceLookupUnavailable) as exc:
            if exc.code != 403:
                raise
            plan["receiptSchemaVersion"] = 1
            plan["recordingRunId"] = args.run_id
            plan["receiptRecordingDeferred"] = "current_run_actions_observation_forbidden"
            Path(args.output).write_text(json.dumps(plan, indent=2, sort_keys=True) + "\n")
            print("Deferred current-run child receipt enrichment; completed-run evidence remains canonical.")
            return
        for job in jobs:
            for step in job.get("steps", []):
                if step.get("conclusion"):
                    observed[step.get("name")] = {
                        "result": step["conclusion"],
                        "jobId": job.get("id"),
                        "stepNumber": step.get("number"),
                    }

    runtime = {"python": sys.version.split()[0]}
    for tool in ("dotnet", "node"):
        try:
            observation = subprocess.run([tool, "--version"], capture_output=True, text=True, timeout=20)
            if observation.returncode == 0:
                runtime[tool] = observation.stdout.strip()
        except (OSError, subprocess.TimeoutExpired):
            pass

    plan["receiptSchemaVersion"] = 1
    plan["recordingRunId"] = args.run_id
    for key, gate in plan.get("gates", {}).items():
        observation = observed.get(gate["step"], {})
        producer = gate.get("producerReceipt") or {}
        gate["receipt"] = {
            "result": observation.get("result", "unproven") if gate.get("run") else producer.get("result", "unproven"),
            "producerJobId": observation.get("jobId") if gate.get("run") else producer.get("jobId"),
            "producerStepNumber": observation.get("stepNumber") if gate.get("run") else producer.get("stepNumber"),
            "producingRunId": args.run_id if gate.get("run") else gate.get("evidenceRunId", plan.get("priorRunId")),
            "recordingJobId": observation.get("jobId"),
            "stepNumber": observation.get("stepNumber"),
            "reused": not gate.get("run"),
            "actualToolchain": runtime if gate.get("run") else None,
            "toolchainPolicy": "observed-producer" if gate.get("run") else "preserved-producer-contract",
        }

    Path(args.output).write_text(json.dumps(plan, indent=2, sort_keys=True) + "\n")
    cache_output = getattr(args, "cache_output", None)
    if cache_output:
        payload = _write_gate_cache(
            plan,
            plan["workflow"],
            plan["currentSha"],
            args.run_id,
            int(os.environ.get("GITHUB_RUN_ATTEMPT", "1")),
            cache_output,
        )
        print(json.dumps({"cachedSuccessfulGates": sorted(payload["gates"])}, sort_keys=True))



def _stop_unresolved_planning(args, exc):
    """Non-evidence planner defects remain blocking; never invent success."""
    record = {"schemaVersion": 2, "mode": "blocked",
              "reason": "planner_unavailable_resume_planning_only",
              "plannerError": type(exc).__name__}
    if isinstance(exc, EvidenceLookupUnavailable):
        record["evidenceHttpStatus"] = exc.code
        record["evidenceEndpoint"] = exc.endpoint
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(record, indent=2, sort_keys=True) + "\n")
    print(json.dumps(record, sort_keys=True))
    raise SystemExit(1)


def _fresh_plan_when_evidence_unavailable(args, exc):
    """Historical evidence is optional optimization; unavailable proof means run fresh."""
    plan = compute_plan(
        args.workflow,
        args.current_sha,
        None,
        {},
        [],
        "evidence_unavailable_fresh_validation",
    )
    plan["schemaVersion"] = 2
    plan["evidenceFallback"] = {
        "reason": "historical_evidence_unavailable_run_fresh",
        "error": type(exc).__name__,
        "httpStatus": exc.code,
        "endpoint": exc.endpoint,
    }
    plan["dependencyManifests"] = gate_dependency_manifests(args.workflow, args.current_sha)
    return plan


def _cached_success_evidence(args):
    """Use PR-local gate evidence before any remote history lookup.

    Schema 1 is the older all-parent-success capsule. Schema 2 is the canonical
    child ledger: only gates explicitly checkpointed successful are reusable.
    Git ancestry plus _plan_against_prior still revalidates source, execution and
    dependency identity before any cached success can suppress work.
    """
    raw = getattr(args, "resume_cache", None)
    if not raw:
        return None
    path = Path(raw)
    if not path.is_file():
        return None
    data = json.loads(path.read_text())
    if (
        data.get("workflow") != args.workflow
        or not re.fullmatch(r"[0-9a-f]{40}", data.get("headSha") or "")
        or type(data.get("runId")) is not int
        or data["runId"] < 1
    ):
        raise ValueError("Malformed PR-local validation cache")
    prior_sha = data["headSha"]
    if subprocess.run(
        ["git", "merge-base", "--is-ancestor", prior_sha, args.current_sha],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    ).returncode:
        raise ValueError("PR-local validation cache is not in the current candidate lineage")

    steps = _StepEvidence()
    schema = data.get("schemaVersion")
    if schema == 1:
        successful = set(WORKFLOWS[args.workflow]["gates"])
    elif schema == 2:
        rows = data.get("gates")
        if not isinstance(rows, dict):
            raise ValueError("Malformed PR-local child gate ledger")
        successful = {
            key for key, row in rows.items()
            if key in WORKFLOWS[args.workflow]["gates"]
            and isinstance(row, dict)
            and row.get("result") == "success"
        }
    else:
        raise ValueError("Unsupported PR-local validation cache schema")

    for key in successful:
        gate = WORKFLOWS[args.workflow]["gates"][key]
        step = gate["step"]
        steps[step] = "success"
        steps.producers[step] = {
            "result": "success",
            "jobId": None,
            "runId": data["runId"],
            "stepNumber": None,
            "artifact": "pr-local-gate-cache",
        }
    if not steps:
        return None
    prior = {
        "id": data["runId"],
        "head_sha": prior_sha,
        "run_attempt": int(data.get("runAttempt") or 1),
        "event": args.event,
        "head_branch": args.head_branch,
    }
    return prior, steps, "pr_local_gate_cache"


def cmd_cache_success(args):
    if args.workflow not in WORKFLOWS:
        raise SystemExit(f"Unsupported validation workflow: {args.workflow}")
    plan = {
        "workflow": args.workflow,
        "currentSha": args.head_sha,
        "gates": {
            key: {"receipt": {"result": "success"}}
            for key in WORKFLOWS[args.workflow]["gates"]
        },
    }
    payload = _write_gate_cache(
        plan,
        args.workflow,
        args.head_sha,
        args.run_id,
        args.run_attempt,
        args.output,
    )
    print(json.dumps(payload, sort_keys=True))


def _compute_validation_plan_once(args):
    cached = _cached_success_evidence(args)
    if cached is not None:
        prior, steps, source = cached
        plan = _plan_against_prior(args.workflow, args.current_sha, prior, steps, source)
        _stamp_evidence(plan, prior, source)
        # A failed parent can checkpoint only the children it observed successful.
        # Missing cache entries are unknown, not invalidated. Backfill only those
        # unresolved gates from trusted content-equivalent historical producers;
        # changed inputs/definitions remain runnable and fail closed.
        plan = _apply_content_equivalent_evidence(args, plan)
    else:
        prior, steps, source = prior_evidence(args)
        if prior:
            plan = _plan_against_prior(args.workflow, args.current_sha, prior, steps, source)
            _stamp_evidence(plan, prior, source)
        else:
            plan = compute_plan(
                args.workflow,
                args.current_sha,
                None,
                {},
                [],
                source,
            )
        plan = _apply_content_equivalent_evidence(args, plan)
    plan["schemaVersion"] = 2
    plan["dependencyManifests"] = gate_dependency_manifests(args.workflow, args.current_sha)
    return plan


def _evidence_retry_delay(exc, attempt):
    rate = getattr(exc, "rate", {}) or {}
    retry = rate.get("Retry-After")
    reset = rate.get("X-RateLimit-Reset")
    try:
        if retry is not None:
            return max(1, int(retry) + 2)
        if reset is not None:
            return max(1, int(reset) - int(time.time()) + 3)
    except (TypeError, ValueError):
        pass
    return (15, 30, 60, 120)[min(attempt, 3)]


def cmd_plan(args):
    if args.workflow not in WORKFLOWS:
        raise SystemExit(f"Unsupported validation workflow: {args.workflow}")

    plan = None
    last_evidence_error = None
    for attempt in range(4):
        try:
            plan = _compute_validation_plan_once(args)
            break
        except EvidenceLookupUnavailable as exc:
            last_evidence_error = exc
            if getattr(args, "event", None) != "pull_request":
                plan = _fresh_plan_when_evidence_unavailable(args, exc)
                break
            if attempt == 3:
                break
            delay = _evidence_retry_delay(exc, attempt)
            # The workflow timeout remains the outer safety bound. Do not turn a
            # long provider reset into broad validation; preserve child proof and
            # retry only the planning boundary.
            if delay > 900:
                break
            print(
                f"Validation evidence lookup unavailable; preserving known child proof "
                f"and retrying planner boundary in {delay}s (attempt {attempt + 1}/4)."
            )
            time.sleep(delay)
        except Exception as exc:
            _stop_unresolved_planning(args, exc)

    if plan is None:
        _stop_unresolved_planning(args, last_evidence_error or RuntimeError("Validation planning unavailable"))

    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(plan, indent=2, sort_keys=True) + "\n")
    print(json.dumps({key: value for key, value in plan.items() if key != "dependencyManifests"}, indent=2, sort_keys=True))



def cmd_preserved(args):
    plan = json.loads(Path(args.plan).read_text())
    gate = plan.get("gates", {}).get(args.gate)
    if gate is None:
        print(f"Unknown gate {args.gate}; run it.", file=sys.stderr)
        raise SystemExit(1)
    if gate.get("run"):
        print(f"RUN {args.gate}: {gate.get('reason')}")
        raise SystemExit(1)
    print(
        f"PRESERVED {args.gate}: successful evidence from "
        f"run {gate.get('evidenceRunId', plan.get('priorRunId'))} at "
        f"{gate.get('evidenceHeadSha', plan.get('priorHeadSha'))} "
        f"({gate.get('evidenceSource', plan.get('evidenceSource'))})"
    )
    raise SystemExit(0)



MERGE_VALIDATION_NEUTRAL_PATHS = frozenset({
    ".github/workflows/masterapp-platform-architecture-validation.yml",
    ".github/workflows/approved-release-security-validation.yml",
    ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
    ".github/workflows/all-intentional-direct-release-20260918.yml",
    "Docs/releases/direct-release-request.json",
    "scripts/approved-release-baseline.py",
    "scripts/release-package.py",
    "scripts/validation-resume.py",
    "scripts/test-validation-resume.py",
    "scripts/release-lifecycle.py",
    "scripts/test-release-policy.py",
    "scripts/test-release-lifecycle.py",
    "scripts/test-deploy-approved-app.py",
    "tests/website/legend-public-cms.test.mjs",
    "AgentPortal.Tests/WebsiteStudioV3ContractTests.cs",
})

STEP5_RELEASE_EVIDENCE_PATHS = frozenset({
    ".github/workflows/all-intentional-direct-release-20260918.yml",
    ".github/workflows/masterapp-platform-architecture-validation.yml",
    "scripts/approved-release-baseline.py",
    "scripts/release-package.py",
    "scripts/validation-resume.py",
})

SECURITY_RELEASE_EVIDENCE_PATHS = frozenset({
    "scripts/validation-resume.py",
})

PUBLIC_WEBSITE_EXACT_PATHS = frozenset({
    "AgentPortal.Tests/WebsiteContentEditorRoundTripTests.cs",
    "AgentPortal.Tests/WebsiteSiteSourceV3Tests.cs",
    "Legend-Design/legend-web-foundation.css",
    "Legend-Website/scripts/build.mjs",
    "Legend-Website/public/web.config",
    "Protect-Website/Views/Shared/_Layout.cshtml",
    "SHARED/WebsitePlatform/legend-public-cms.js",
    "SHARED/WebsitePlatform/legend-public-web.css",
    "Infrastructure/WebsiteEditing/WebsiteContentSanitizer.cs",
    "Infrastructure/WebsiteEditing/WebsiteEditorContracts.cs",
    "Infrastructure/WebsiteEditing/WebsitePlatformController.cs",
    "Infrastructure/WebsiteEditing/WebsiteSiteSource.cs",
    "Infrastructure/WebsiteEditing/WebsiteStudioAgentContract.cs",
    "Infrastructure/WebsiteRuntime/BusinessWebsiteMiddleware.cs",
})


def validation_neutral_path(path: str) -> bool:
    return path in MERGE_VALIDATION_NEUTRAL_PATHS or path.startswith("tests/")


def _workflow_affected_by_path(workflow_name: str, path: str) -> bool:
    config = WORKFLOWS[workflow_name]
    if matches(path, config.get("force_all", ())):
        return True
    return any(gate_matches(path, gate) for gate in config["gates"].values())


def required_validation_topology(changed_paths):
    """Return the exact validation workflows required before integration.

    This is the single merge-readiness topology. Release lifecycle consumes the
    answer; it does not maintain subsystem path registries of its own.
    """
    names = tuple(dict.fromkeys(changed_paths))
    architecture = ".github/workflows/masterapp-platform-architecture-validation.yml"
    step5 = ".github/workflows/step5-isolated-conversion-mapping-validation.yml"
    step6 = ".github/workflows/step6-openai-ads-execution-validation.yml"
    step78 = ".github/workflows/steps7-8-governed-advertising-validation.yml"
    security = ".github/workflows/approved-release-security-validation.yml"

    required = {architecture}
    release_control_authority_change = any(release_control_authority_path(name) for name in names)
    step5_release_evidence_change = any(name in STEP5_RELEASE_EVIDENCE_PATHS for name in names)
    security_release_evidence_change = any(name in SECURITY_RELEASE_EVIDENCE_PATHS for name in names)
    release_evidence_change = (
        release_control_authority_change
        or step5_release_evidence_change
        or security_release_evidence_change
    )

    scope_neutral = MERGE_VALIDATION_NEUTRAL_PATHS | {
        "scripts/deploy-approved-app.py",
        "scripts/release-package.py",
    }
    product_names = [
        name for name in names
        if name not in scope_neutral and not name.startswith("tests/")
    ]
    public_website_only = bool(product_names) and all(
        name in PUBLIC_WEBSITE_EXACT_PATHS for name in product_names
    )

    if step5 in names or step5_release_evidence_change:
        required.add(step5)

    shared_resume_authority_change = "scripts/validation-resume.py" in names

    step6_name = "step6-openai-ads-execution-validation.yml"
    if (
        shared_resume_authority_change
        or step6 in names
        or any(_workflow_affected_by_path(step6_name, name) for name in names)
    ):
        required.add(step6)

    step78_name = "steps7-8-governed-advertising-validation.yml"
    if (
        not public_website_only
        and (
            shared_resume_authority_change
            or step78 in names
            or any(_workflow_affected_by_path(step78_name, name) for name in names)
        )
    ):
        required.add(step78)

    broad_product_change = any(
        name.startswith((
            "AgentPortal/",
            "ClientApp/",
            "Protect-Website/",
            "ParfaitApp/",
            "SHARED/",
            "Infrastructure/",
            "Domain/",
        ))
        or name == step5
        for name in product_names
    )
    if broad_product_change and not public_website_only:
        required.add(step5)
        required.add(security)
    if release_control_authority_change or security in names or security_release_evidence_change:
        required.add(security)

    return {
        "required": tuple(sorted(required)),
        "publicWebsiteOnly": public_website_only,
        "releaseEvidenceChange": release_evidence_change,
        "releaseControlAuthorityChange": release_control_authority_change,
        "broadProductChange": broad_product_change,
    }


def _selected_release_targets(raw: str):
    return selected_release_target_keys(json.loads(raw))


def _read_provenance(host: str, target, revision: str):
    path = target["provenancePath"]
    request = urllib.request.Request(
        f"https://{host}{path}?release={revision}",
        headers={"Cache-Control": "no-cache", "User-Agent": "LEGEND-release-proof/1.0"},
    )
    with urllib.request.urlopen(request, timeout=15) as response:
        if target["static"]:
            return response.read().decode().strip()
        payload = json.load(response)
        return payload.get("sourceRevision")


def cmd_gate_identity(args):
    if args.workflow not in WORKFLOWS:
        raise ValueError("Unknown validation workflow: " + args.workflow)
    if args.gate not in WORKFLOWS[args.workflow]["gates"]:
        raise ValueError("Unknown validation gate: " + args.gate)
    identity = gate_dependency_manifests(args.workflow, args.revision)[args.gate]["contentIdentity"]
    print(identity)
    if args.github_output:
        with Path(args.github_output).open("a") as output:
            output.write(f"identity={identity}\n")


def cmd_live_state(args):
    keys = _selected_release_targets(args.selected_targets)
    result = {
        "schemaVersion": 1,
        "revision": args.revision,
        "targets": {},
    }

    def probe(key):
        target = RELEASE_TARGETS[key]
        try:
            actual = _read_provenance(target["host"], target, args.revision)
            return key, actual, actual == args.revision
        except Exception as exc:
            return key, type(exc).__name__, False

    selected_results = {}
    if keys:
        with concurrent.futures.ThreadPoolExecutor(max_workers=min(8, len(keys))) as pool:
            selected_results = {
                key: (actual, live)
                for key, actual, live in pool.map(probe, keys)
            }

    # Preserve the canonical inventory order in receipts/GITHUB_OUTPUT even
    # though independent network reads execute concurrently.
    for key, target in RELEASE_TARGETS.items():
        selected = key in keys
        actual, live = selected_results.get(key, (None, False))
        result["targets"][key] = {
            "releaseName": target["releaseName"],
            "selected": selected,
            "alreadyLive": live,
            "actual": actual,
        }
        print(json.dumps({
            "target": target["releaseName"],
            "selected": selected,
            "alreadyLive": live,
            "actual": actual,
            "expected": args.revision if selected else None,
        }, sort_keys=True))
    # Keep canonical RELEASE_TARGETS insertion order in the persisted receipt.
    # Sorting nested object keys would alphabetize target names and destroy the
    # deterministic inventory order even though the probes themselves are parallel.
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")
    if args.github_output:
        with Path(args.github_output).open("a") as output:
            for key in RELEASE_TARGETS:
                output.write(f"{key}_live={str(result['targets'][key]['alreadyLive']).lower()}\n")


def cmd_verify_live(args):
    keys = _selected_release_targets(args.selected_targets)
    work = [
        (key, host)
        for key in keys
        for host in RELEASE_TARGETS[key]["proofHosts"]
    ]

    def verify(item):
        key, host = item
        target = RELEASE_TARGETS[key]
        end = time.monotonic() + args.timeout_seconds
        actual = None
        while time.monotonic() < end:
            try:
                actual = _read_provenance(host, target, args.revision)
                if actual == args.revision:
                    return {
                        "target": target["releaseName"],
                        "host": host,
                        "passed": True,
                        "actual": actual,
                    }
            except Exception as exc:
                actual = type(exc).__name__
            time.sleep(args.poll_seconds)
        return {
            "target": target["releaseName"],
            "host": host,
            "passed": False,
            "actual": actual,
        }

    with concurrent.futures.ThreadPoolExecutor(max_workers=min(8, len(work))) as pool:
        rows = list(pool.map(verify, work))
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(rows, indent=2, sort_keys=True) + "\n")
    for row in rows:
        print(json.dumps(row, sort_keys=True))
    if not rows or not all(row["passed"] for row in rows):
        raise SystemExit("Final live provenance proof failed")


def release_target_rows():
    return tuple(
        (
            key,
            row["host"],
            row["project"],
        )
        for key, row in RELEASE_TARGETS.items()
    )


def lifecycle_authority_identity():
    """Hash both present authority bytes and intentionally absent protected paths."""
    digest = hashlib.sha256()
    for path in LIFECYCLE_AUTHORITY_PATHS:
        source = Path(path)
        digest.update(path.encode())
        digest.update(b"\0")
        if source.is_file():
            digest.update(b"present\0")
            digest.update(hashlib.sha256(source.read_bytes()).digest())
        elif source.exists():
            raise RuntimeError(f"Lifecycle authority path is not a regular file: {path}")
        else:
            # Retired production bypasses remain in the protected authority set.
            # Their required absence is therefore part of the canonical identity,
            # and any future reintroduction changes the identity instead of
            # crashing receipt generation or silently escaping protection.
            digest.update(b"absent\0")
    return digest.hexdigest()


def compute_lifecycle_evidence(repository: str):
    result = {
        "schemaVersion": 1,
        "identity": lifecycle_authority_identity(),
        "artifact": None,
        "runId": None,
        "reusable": False,
    }
    result["artifact"] = f"legend-lifecycle-contracts-{result['identity']}"
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        result["reason"] = "github_token_unavailable"
        return result
    workflow_path = ".github/workflows/legend-release-lifecycle.yml"
    try:
        artifacts = _artifact_rows(repository, result["artifact"], token)
        for artifact in artifacts:
            run_id = int((artifact.get("workflow_run") or {}).get("id") or 0)
            if not run_id:
                continue
            run = api_get(repository, f"actions/runs/{run_id}", token)
            if (
                run.get("path") == workflow_path
                and run.get("status") == "completed"
                and run.get("conclusion") == "success"
                and (run.get("head_repository") or {}).get("full_name") == repository
            ):
                result.update({
                    "runId": run_id,
                    "reusable": True,
                    "reason": "exact_lifecycle_authority_receipt",
                })
                return result
    except Exception as exc:
        # Evidence lookup failure must disable reuse, not erase the deterministic
        # local lifecycle identity needed to run and retain fresh safety proof.
        result.update({
            "reason": "lifecycle_evidence_lookup_unavailable",
            "plannerError": type(exc).__name__,
        })
        return result
    result["reason"] = "no_exact_lifecycle_authority_receipt"
    return result


def cmd_lifecycle_evidence(args):
    try:
        result = compute_lifecycle_evidence(args.repository)
    except Exception as exc:
        result = {
            "schemaVersion": 1,
            "identity": None,
            "artifact": None,
            "runId": None,
            "reusable": False,
            "reason": "planner_error_fail_closed",
            "plannerError": type(exc).__name__,
        }
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")
    print(json.dumps(result, indent=2, sort_keys=True))


def package_identity_for_revision(revision: str) -> str:
    completed = subprocess.run(
        [
            sys.executable,
            str(Path(__file__).with_name("release-package.py")),
            "identity",
            "--revision",
            revision,
        ],
        text=True,
        capture_output=True,
    )
    if completed.returncode:
        raise RuntimeError(completed.stderr or "package_identity_failed")
    payload = json.loads(completed.stdout)
    identity = payload.get("identity")
    if not isinstance(identity, str) or not re.fullmatch(r"[0-9a-f]{64}", identity):
        raise RuntimeError("package_identity_invalid")
    return identity


def compute_package_canary_plan(repository, current_sha, base_sha, current_run_id, head_branch):
    result = {"schemaVersion": 1, "needed": True, "currentSha": current_sha,
              "evidenceRunId": None, "evidenceHeadSha": None, "changedInputs": [],
              "reason": "compatible_immutable_package_missing"}
    # Package production is driven by package-producing inputs, not artifact
    # discovery. A control/test/lifecycle-only head has no new application bytes
    # to stamp with the current commit and therefore must not manufacture a
    # redundant immutable package merely because historical artifact lookup misses.
    result['changedInputs'] = sorted(
        path for path in git_changed(base_sha, current_sha)
        if package_canary_input_path(path)
    )
    if not result['changedInputs']:
        result.update({
            "needed": False,
            "reason": "no_package_producing_inputs_changed",
        })
        return result

    token = os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN') or ''
    if token:
        compatible = compatible_package_producer(repository, current_sha, token)
        if compatible:
            result.update({"needed": False, "reason": compatible['reason'],
                           "evidenceRunId": compatible['runId'],
                           "evidenceHeadSha": compatible['revision'],
                           "packageIdentity": compatible['packageIdentity'],
                           "exactPackageRunId": compatible['runId'],
                           "exactPackageArtifact": compatible['artifact']})
            return result
    return result


def cmd_package_canary_plan(args):
    try:
        result = compute_package_canary_plan(
            args.repository,
            args.current_sha,
            args.base_sha,
            args.current_run_id,
            args.head_branch,
        )
    except Exception as exc:
        result = {
            "schemaVersion": 1,
            "needed": True,
            "currentSha": args.current_sha,
            "evidenceRunId": None,
            "evidenceHeadSha": None,
            "changedInputs": [],
            "reason": "planner_error_fail_closed",
            "plannerError": type(exc).__name__,
        }
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")
    print(json.dumps(result, indent=2, sort_keys=True))


def package_backfill_receipt_name(revision: str, package_identity: str) -> str:
    return f"validated-package-backfill-{revision}-{package_identity}"


def compute_package_backfill_plan(repository: str, revision: str, current_sha: str):
    """Authorize package-only recovery for an already-green historical product head.

    The current approved control plane may package the historical revision only when
    the exact product head has a successful Architecture PR validation and every
    change since that revision is release/test/control-only. Any application-source
    drift fails closed.
    """
    result = {
        "schemaVersion": 1,
        "revision": revision,
        "currentSha": current_sha,
        "allowed": False,
        "validationRunId": None,
        "changedApplicationInputs": [],
    }
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN") or ""
    if not token:
        result["reason"] = "github_token_unavailable"
        return result

    ancestor = subprocess.run(
        ["git", "merge-base", "--is-ancestor", revision, current_sha],
        text=True,
        capture_output=True,
    )
    if ancestor.returncode != 0:
        result["reason"] = "revision_not_preserved_in_current_approved_history"
        return result

    changed = git_changed(revision, current_sha)
    application_changes = sorted(
        path for path in changed
        if not release_control_only_path(path)
    )
    result["changedApplicationInputs"] = application_changes
    if application_changes:
        result["reason"] = "application_inputs_changed_since_validated_revision"
        return result

    workflow_path = ".github/workflows/" + PACKAGE_VALIDATION_WORKFLOW
    encoded = urllib.parse.quote(revision, safe="")
    payload = api_get(
        repository,
        f"actions/runs?head_sha={encoded}&event=pull_request&status=completed&per_page=100",
        token,
    )
    runs = [
        run for run in payload.get("workflow_runs", [])
        if run.get("path") == workflow_path
        and run.get("event") == "pull_request"
        and run.get("status") == "completed"
        and run.get("conclusion") == "success"
        and run.get("head_sha") == revision
        and (run.get("head_repository") or {}).get("full_name") == repository
    ]
    pulls = api_get(repository, f"commits/{revision}/pulls", token)
    bound = [
        pr for pr in pulls
        if pr.get("merged_at")
        and pr.get("base", {}).get("ref") == TRUSTED_PR_BASE
        and pr.get("head", {}).get("sha") == revision
        and pr.get("head", {}).get("repo", {}).get("full_name") == repository
    ]
    if not runs or len(bound) != 1:
        result["reason"] = "exact_green_architecture_pr_evidence_missing"
        return result

    runs.sort(
        key=lambda run: (run.get("updated_at") or run.get("created_at", ""), int(run.get("id", 0))),
        reverse=True,
    )
    result.update({
        "allowed": True,
        "validationRunId": int(runs[0]["id"]),
        "sourcePr": int(bound[0]["number"]),
        "reason": "exact_green_revision_with_control_only_descendants",
    })
    return result


def cmd_package_backfill_plan(args):
    try:
        result = compute_package_backfill_plan(
            args.repository,
            args.revision,
            args.current_sha,
        )
    except Exception as exc:
        result = {
            "schemaVersion": 1,
            "revision": args.revision,
            "currentSha": args.current_sha,
            "allowed": False,
            "validationRunId": None,
            "changedApplicationInputs": [],
            "reason": "planner_error_fail_closed",
            "plannerError": type(exc).__name__,
        }
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")
    print(json.dumps(result, indent=2, sort_keys=True))




class MigrationProbeAuthorityMissing(ValueError):
    """A revision predates the required probe child and cannot supply its evidence."""


def migration_probe_identity(tool_revision, application_revision):
    """One dependency model: derive the probe runtime from infrastructure build ownership."""
    if not all(re.fullmatch(r'[0-9a-f]{40}', value or '') for value in (tool_revision, application_revision)):
        raise ValueError('Exact probe and application source revisions required')
    paths = WORKFLOWS[PACKAGE_VALIDATION_WORKFLOW]['gates']['build-infrastructure']['paths']
    # Other projects' project files do not change this direct-reference closure.
    runtime_patterns = tuple(path for path in paths if path not in {'**/*.csproj', 'MASTERAPP.sln'})
    def content(revision, patterns):
        rows = subprocess.check_output(['git', 'ls-tree', '-rz', '--full-tree', revision], text=True).split('\0')
        selected = []
        for row in rows:
            if not row:
                continue
            metadata, name = row.split('\t', 1)
            if matches(name, patterns):
                selected.append(row)
        return hashlib.sha256('\0'.join(sorted(selected)).encode()).hexdigest()
    runtime = content(application_revision, runtime_patterns)
    if runtime != content(tool_revision, runtime_patterns):
        raise ValueError('Probe was compiled against different candidate migration/runtime inputs')
    tooling = content(tool_revision, ('scripts/MigrationReleaseProbe/**', 'scripts/migration-probe-package.py'))
    workflow = git_show_file(tool_revision, '.github/workflows/' + PACKAGE_VALIDATION_WORKFLOW)
    job = _job_blocks(workflow).get('validated-migration-probe')
    if not job:
        raise MigrationProbeAuthorityMissing('Validated migration probe child authority missing')
    payload = {'schemaVersion': 1, 'runtimeIdentity': runtime, 'toolIdentity': tooling,
               'executionIdentity': hashlib.sha256(job.rstrip().encode()).hexdigest()}
    identity = hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    return dict(payload, identity=identity, artifact='legend-migration-probe-' + identity)


def migration_probe_evidence(repository, identity):
    token = os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN') or ''
    if not token:
        raise ValueError('Probe evidence authentication unavailable')
    workflow_path = '.github/workflows/' + PACKAGE_VALIDATION_WORKFLOW
    workflow = urllib.parse.quote(PACKAGE_VALIDATION_WORKFLOW, safe='')
    payload = api_get(repository,
        f'actions/workflows/{workflow}/runs?event=pull_request&status=completed&per_page=100', token)
    runs = sorted(payload.get('workflow_runs', []),
        key=lambda row: (row.get('updated_at') or row.get('created_at', ''), int(row.get('id', 0))),
        reverse=True)
    for run in runs:
        run_id = int(run.get('id') or 0)
        current_revision = subprocess.check_output(
            ['git', 'rev-parse', 'HEAD'], text=True).strip()
        if not run_id or not _trusted_lineage_run(repository, run, workflow_path, current_revision):
            continue
        try:
            producer = migration_probe_identity(run['head_sha'], run['head_sha'])
        except MigrationProbeAuthorityMissing:
            # Historical runs before this child existed cannot prove probe reuse.
            # Continue searching; without a compatible producer the caller builds.
            continue
        if producer != identity:
            continue
        if identity['artifact'] not in _run_artifact_names(repository, run_id, token):
            continue
        # The canonical child uploads only after build + manifest verification.
        # Exact execution/runtime/tool identity plus retained artifact presence is
        # the durable success proof; release re-verifies the manifest after load.
        return {'reusable': True, 'runId': run_id, 'artifact': identity['artifact'], 'identity': identity['identity']}
    return {'reusable': False, 'artifact': identity['artifact']}

def package_inputs_compatible(prior: str, current: str) -> bool:
    """Compare real package-producing inputs without relabeling producer bytes."""
    import ast
    identity_only = {'contract_hash', 'package_identity', 'artifact_name', 'verify_all'}
    def observer_contract(node):
        # Only the reviewed read/hash/compare grammar may be excluded from byte
        # production. Unknown calls/syntax invalidate reuse; file-mode writes stay
        # in the producing contract rather than being called read-only.
        pure_names = {'normalize_revision', 'validate_revision', 'contract_hash', 'package_identity',
                      'sha256_file', 'embedded_revision', 'ValueError', 'FileNotFoundError',
                      'Path', 'set', 'isinstance', 'str', 'len', 'sorted'}
        pure_methods = {'exists', 'encode', 'read_bytes', 'read_text', 'hexdigest', 'update',
                        'get', 'items', 'split', 'splitlines', 'strip'}
        qualified = {'json.dumps', 'json.loads', 'hashlib.sha256', 're.fullmatch', 'os.access',
                     '_RELEASE_AUTHORITY.package_builder_workflow_contract',
                     '_RELEASE_AUTHORITY.package_inputs_compatible'}
        effects = []
        for child in ast.walk(node):
            if isinstance(child, (ast.Global, ast.Nonlocal, ast.Import, ast.ImportFrom, ast.With,
                                  ast.AsyncWith, ast.Await, ast.Yield, ast.YieldFrom)):
                return None
            if isinstance(child, (ast.Assign, ast.AnnAssign, ast.AugAssign)):
                targets = child.targets if isinstance(child, ast.Assign) else [child.target]
                if any(isinstance(target, ast.Attribute) for target in targets):
                    return None
            if not isinstance(child, ast.Call):
                continue
            function = ast.unparse(child.func)
            if function == 'bundle.chmod':
                effects.append(ast.dump(child, include_attributes=False))
            elif function == 'subprocess.check_output':
                command = child.args[0] if child.args else None
                if (not isinstance(command, ast.List) or len(command.elts) < 2 or
                        not all(isinstance(part, ast.Constant) for part in command.elts[:2]) or
                        [part.value for part in command.elts[:2]] != ['git', 'rev-parse'] or
                        any(keyword.arg not in {'cwd', 'text'} for keyword in child.keywords)):
                    return None
            elif function in pure_names or function in qualified:
                continue
            elif isinstance(child.func, ast.Attribute) and child.func.attr in pure_methods:
                continue
            else:
                return None
        return sorted(effects)

    def builder(text):
        tree = ast.parse(text)
        rows = []
        for node in tree.body:
            if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name in identity_only:
                observer = observer_contract(node)
                if observer is not None:
                    rows.append(('observer', node.name, observer))
                    continue
            if isinstance(node, ast.Expr) and isinstance(node.value, ast.Constant) and isinstance(node.value.value, str):
                continue
            if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'CONTRACT_INPUTS' for t in node.targets):
                continue
            rows.append(ast.dump(node, include_attributes=False))
        return rows
    for path in git_changed(prior, current):
        if path == 'scripts/release-package.py':
            if builder(git_show_file(prior, path)) != builder(git_show_file(current, path)):
                return False
        elif path == PACKAGE_BUILD_WORKFLOW:
            try:
                prior_contract = package_builder_workflow_contract(git_show_file(prior, path))
                current_contract = package_builder_workflow_contract(git_show_file(current, path))
            except ValueError:
                # A historical workflow that predates the current canonical
                # builder shape is incompatible evidence, not a planner fault.
                return False
            if prior_contract != current_contract:
                return False
        elif path == 'scripts/validation-resume.py':
            # Topology is literal canonical data, never execute historical code.
            def topology(text):
                wanted = {'RELEASE_TARGETS', 'MIGRATION_BUNDLE_NAME'}
                data = {}
                for node in ast.parse(text).body:
                    if isinstance(node, ast.Assign):
                        for target in node.targets:
                            if isinstance(target, ast.Name) and target.id in wanted:
                                data[target.id] = ast.literal_eval(node.value)
                if set(data) != wanted:
                    raise ValueError('Package topology missing')
                data['RELEASE_TARGETS'] = {key: {field: row[field] for field in ('project', 'package', 'static', 'sourceRoot')}
                                           for key, row in data['RELEASE_TARGETS'].items()}
                return data
            if topology(git_show_file(prior, path)) != topology(git_show_file(current, path)):
                return False
        elif path in PACKAGE_AUTHORITY_PATHS or not release_control_only_path(path):
            return False
    return True


def _successful_package_child(repository, run_id, token):
    jobs = api_get(repository, f'actions/runs/{run_id}/jobs?filter=latest&per_page=100', token).get('jobs', [])
    children = [job for job in jobs if job.get('name') == 'validated-release-package']
    if len(children) != 1:
        return False
    steps = {step.get('name'): step.get('conclusion') for step in children[0].get('steps', [])}
    return all(steps.get(name) == 'success' for name in (
        'Build immutable validated release package',
        'Verify immutable validated release package',
        'Preserve immutable validated release package',
    ))


def compatible_package_producer(repository, revision, token):
    """Locate authenticated immutable bytes from a content-equivalent producer.

    Enumerate trusted completed producer runs first, then inspect each run's own
    artifacts. Repository-wide artifact listing is not required for provenance
    and can be unavailable to an active PR token.
    """
    workflow_path = '.github/workflows/' + PACKAGE_VALIDATION_WORKFLOW
    workflow = urllib.parse.quote(PACKAGE_VALIDATION_WORKFLOW, safe='')
    payload = api_get(repository,
        f'actions/workflows/{workflow}/runs?event=pull_request&status=completed&per_page=100', token)
    runs = sorted(payload.get('workflow_runs', []),
        key=lambda row: (row.get('updated_at') or row.get('created_at', ''), int(row.get('id', 0))),
        reverse=True)
    for run in runs:
        run_id = int(run.get('id') or 0)
        if not run_id or not _trusted_lineage_run(repository, run, workflow_path, revision):
            continue
        if not _successful_package_child(repository, run_id, token):
            continue
        producer = run['head_sha']
        package_input_changes = [
            path for path in git_changed(producer, revision)
            if package_canary_input_path(path)
        ]
        if package_input_changes and not package_inputs_compatible(producer, revision):
            continue
        names = sorted(name for name in _run_artifact_names(repository, run_id, token)
                       if re.fullmatch(r'founder-diagnostics-packages-[0-9a-f]{64}', name))
        if not names:
            continue
        name = names[-1]
        return {'schemaVersion': 1, 'revision': producer, 'requestedRevision': revision,
                'packageIdentity': name.removeprefix('founder-diagnostics-packages-'),
                'artifact': name, 'runId': run_id, 'reusable': True,
                'reason': 'dependency_equivalent_immutable_package_producer'}
    return None

def compute_validated_package_evidence(repository: str, revision: str, package_identity: str, *, allow_equivalent=True):
    result = {
        "schemaVersion": 1,
        "revision": revision,
        "packageIdentity": package_identity,
        "artifact": f"founder-diagnostics-packages-{package_identity}",
        "runId": None,
        "reusable": False,
    }
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN") or ""
    if not token:
        result["reason"] = "github_token_unavailable"
        return result
    workflow_path = ".github/workflows/" + PACKAGE_VALIDATION_WORKFLOW
    for artifact in _artifact_rows(repository, result["artifact"], token):
        run_id = int((artifact.get("workflow_run") or {}).get("id") or 0)
        if not run_id:
            continue
        run = api_get(repository, f"actions/runs/{run_id}", token)
        common = (
            run.get("path") == workflow_path
            and run.get("status") == "completed"
            and (run.get("head_repository") or {}).get("full_name") == repository
        )
        exact_pr = (
            common
            and run.get("event") == "pull_request"
            and run.get("head_sha") == revision
            and _trusted_pr_run(repository, run, workflow_path, token)
        )
        backfill = False
        if (
            common
            and run.get("event") == "workflow_dispatch"
            and run.get("head_branch") == TRUSTED_PR_BASE
        ):
            receipt = package_backfill_receipt_name(revision, package_identity)
            backfill = receipt in _run_artifact_names(repository, run_id, token)
        if (exact_pr or backfill) and _successful_package_child(repository, run_id, token):
            result.update({
                "runId": run_id,
                "reusable": True,
                "reason": (
                    "exact_validated_application_package"
                    if exact_pr
                    else "validated_package_backfill_from_exact_green_revision"
                ),
            })
            return result
    compatible = compatible_package_producer(repository, revision, token) if allow_equivalent else None
    if compatible:
        return compatible
    result["reason"] = "exact_validated_package_missing"
    return result


def cmd_validated_package(args):
    try:
        result = compute_validated_package_evidence(
            args.repository,
            args.revision,
            args.package_identity,
        )
    except Exception as exc:
        result = {
            "schemaVersion": 1,
            "revision": args.revision,
            "packageIdentity": args.package_identity,
            "artifact": f"founder-diagnostics-packages-{args.package_identity}",
            "runId": None,
            "reusable": False,
            "reason": "planner_error_fail_closed",
            "plannerError": type(exc).__name__,
        }
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")
    print(json.dumps(result, indent=2, sort_keys=True))


def compute_rollback_evidence(repository: str, revision: str, app: str):
    if app not in RELEASE_TARGETS:
        raise ValueError(f"Unknown release target: {app}")
    result = {
        "schemaVersion": 2,
        "revision": revision,
        "app": app,
        "packageName": RELEASE_TARGETS[app]["package"],
        "runId": None,
        "releaseRunId": None,
        "packageArtifact": None,
        "packageIdentity": None,
        "reusable": False,
    }
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        result["reason"] = "github_token_unavailable"
        return result
    release_name = RELEASE_TARGETS[app]["releaseName"]
    receipt_names = (
        f"legend-approved-release-{revision}-{release_name}",
        f"legend-approved-release-{revision}",
    )
    workflow_path = ".github/workflows/all-intentional-direct-release-20260918.yml"
    seen_runs = set()
    for receipt_name in receipt_names:
        for artifact in _artifact_rows(repository, receipt_name, token):
            run_id = int((artifact.get("workflow_run") or {}).get("id") or 0)
            if not run_id or run_id in seen_runs:
                continue
            seen_runs.add(run_id)
            run = api_get(repository, f"actions/runs/{run_id}", token)
            if not (
                run.get("path") == workflow_path
                and run.get("head_branch") == TRUSTED_PR_BASE
                and run.get("status") == "completed"
                and run.get("conclusion") == "success"
                and (run.get("head_repository") or {}).get("full_name") == repository
            ):
                continue
            artifact_names = _run_artifact_names(repository, run_id, token)
            link_prefix = f"legend-approved-package-link-{revision}-"
            package_links = sorted(
                name for name in artifact_names
                if name.startswith(link_prefix)
            )
            for link_name in package_links:
                package_identity = link_name[len(link_prefix):]
                if not re.fullmatch(r"[0-9a-f]{64}", package_identity):
                    continue
                validated = compute_validated_package_evidence(
                    repository,
                    revision,
                    package_identity,
                )
                if not validated.get("reusable"):
                    continue
                result.update({
                    "runId": int(validated["runId"]),
                    "releaseRunId": run_id,
                    "packageArtifact": validated["artifact"],
                    "packageIdentity": package_identity,
                    "reusable": True,
                    "reason": (
                        "exact_target_release_receipt_with_validated_package_link"
                        if receipt_name.endswith("-" + release_name)
                        else "legacy_release_receipt_with_validated_package_link"
                    ),
                })
                return result

            # Backward compatibility for releases created before package-link
            # receipts existed. New releases do not duplicate validated package
            # bytes into the release run.
            names = sorted(
                name for name in artifact_names
                if name.startswith("founder-diagnostics-packages-")
            )
            if names:
                package_identity = names[-1].removeprefix("founder-diagnostics-packages-")
                result.update({
                    "runId": run_id,
                    "releaseRunId": run_id,
                    "packageArtifact": names[-1],
                    "packageIdentity": (
                        package_identity
                        if re.fullmatch(r"[0-9a-f]{64}", package_identity)
                        else None
                    ),
                    "reusable": True,
                    "reason": (
                        "exact_target_release_receipt"
                        if receipt_name.endswith("-" + release_name)
                        else "legacy_exact_successful_release_receipt"
                    ),
                })
                return result
    result["reason"] = "no_exact_successful_release_package"
    return result


def cmd_rollback_evidence(args):
    try:
        result = compute_rollback_evidence(args.repository, args.revision, args.app)
    except Exception as exc:
        result = {
            "schemaVersion": 2,
            "revision": args.revision,
            "app": args.app,
            "packageName": RELEASE_TARGETS.get(args.app, {}).get("package"),
            "runId": None,
            "releaseRunId": None,
            "packageArtifact": None,
            "packageIdentity": None,
            "reusable": False,
            "reason": "planner_error_fail_closed",
            "plannerError": type(exc).__name__,
        }
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")
    print(json.dumps(result, indent=2, sort_keys=True))


def _step5_execution_contract(text):
    """Separate suite execution authority from artifact/planner orchestration."""
    jobs = _job_blocks(text)
    contract = {}
    contract["environment"] = _workflow_top_level_field(text, "env")
    contract["defaults"] = _workflow_top_level_field(text, "defaults")
    for name in ("candidate", "baseline"):
        if name not in jobs:
            raise ValueError("Missing Step 5 execution job")
        job = jobs[name]
        header = job.split("    steps:", 1)[0]
        # Only scheduling dependencies/conditions are outside execution identity.
        header = re.sub(r"(?m)^    (?:if|needs):[^\n]*\n(?:      [^\n]*\n)*", "", header)
        # Retain all executable steps, including unnamed/new steps. An unknown
        # source mutation or environment write must never disappear from proof.
        contract[name] = {"runtime": header, "steps": job.split("    steps:", 1)[1]}
    return contract


def _step5_child_contract(text, child):
    contract = _step5_execution_contract(text)
    return {key: contract[key] for key in ('environment', 'defaults', child)}


def _step5_child_unchanged(prior_sha, workflow_path, child):
    return _step5_child_contract(git_show_file(prior_sha, workflow_path), child) == _step5_child_contract(Path(workflow_path).read_text(), child)


def _step5_merge_fingerprint(source):
    import ast
    module = ast.parse(source)
    names = {'merge_step5_class_results', 'read_step5_results', 'cmd_step5_merge'}
    nodes = [ast.dump(node, include_attributes=False) for node in module.body
             if isinstance(node, ast.FunctionDef) and node.name in names]
    if len(nodes) != len(names):
        return None
    return hashlib.sha256('\n'.join(nodes).encode()).hexdigest()


def _step5_materialization_identity(workflow, source):
    """Bind every validate step that can overwrite candidate artifacts and its parser."""
    import ast
    names = {'merge_step5_class_results', 'read_step5_results', 'cmd_step5_merge'}
    nodes = {node.name: ast.dump(node, include_attributes=False)
             for node in ast.parse(source).body
             if isinstance(node, ast.FunctionDef) and node.name in names}
    contract = {'validate': _job_blocks(workflow).get('validate'), 'helpers': nodes}
    return hashlib.sha256(json.dumps(contract, sort_keys=True).encode()).hexdigest()


def _step5_materialization_compatible(prior_sha, workflow_path):
    prior = _step5_materialization_identity(
        git_show_file(prior_sha, workflow_path), git_show_file(prior_sha, 'scripts/validation-resume.py'))
    current = _step5_materialization_identity(
        Path(workflow_path).read_text(), Path('scripts/validation-resume.py').read_text())
    # Candidate artifacts may have been overwritten with effective repaired TRX.
    # Admit unchanged authority or only the exact reviewed directional upgrade;
    # never infer that an artifact is raw from its name or its parent conclusion.
    return prior == current or (prior, current) == (
        '95fca8be2c095ce5c7720dc6276c09384521930cb6e93d985f1366ec3f8ffcad', '6b3bbec67ebdfa63dc9d05b18ecdfc3e32855fd9946682ea6adb6b708b5a91de')


def _step5_candidate_producer_compatible(prior_sha, workflow_path):
    return (_step5_child_unchanged(prior_sha, workflow_path, 'candidate')
            and _step5_materialization_compatible(prior_sha, workflow_path))


def _step5_baseline_producer_compatible(prior_sha, workflow_path):
    if not _step5_materialization_compatible(prior_sha, workflow_path):
        return False
    prior = _step5_child_contract(git_show_file(prior_sha, workflow_path), 'baseline')
    current = _step5_child_contract(Path(workflow_path).read_text(), 'baseline')
    current_helper = _step5_merge_fingerprint(Path('scripts/validation-resume.py').read_text())
    if prior == current:
        return current_helper == _step5_merge_fingerprint(git_show_file(prior_sha, 'scripts/validation-resume.py'))
    # Directional admission of the reviewed full-suite producer into this exact
    # bounded replacement consumer. Identities remain distinct; unknown edits
    # (including environment, checkout, SDK, restore, build or test) fail closed.
    digest = lambda value: hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()
    return current_helper == 'de5a902349eb7e3dbf8e2efd010489cba156f08d9424c626d13cb250224f5ed6' and (digest(prior), digest(current)) == (
        '93bb34927ed933497128baeffc1e3bd61f7d692deb621e406a1986a1b9c7dc93', 'ae069422498edc792983b05eea46e9714d4a73bdd2c69ff15370eb195f028a01')


def _step5_jobs_unchanged(prior_sha: str, workflow_path: str) -> bool:
    return (_step5_execution_contract(git_show_file(prior_sha, workflow_path)) == _step5_execution_contract(Path(workflow_path).read_text())
            and _step5_candidate_producer_compatible(prior_sha, workflow_path)
            and _step5_baseline_producer_compatible(prior_sha, workflow_path))


class ReleaseOperationHistoryUnproven(RuntimeError):
    """No first-write proof; exact-candidate read-only reconciliation is allowed."""


_RELEASE_HISTORY_SOURCES = {}
_RELEASE_HISTORY_VERIFIED_PACKAGES = {}
_RELEASE_HISTORY_API = {}
_RELEASE_HISTORY_TERMINAL_RUNS = set()
_RELEASE_HISTORY_RECEIPTS = {}
_RELEASE_HISTORY_LOG_BINDINGS = {}
_RELEASE_HISTORY_EXCLUSIONS = {}


def export_release_history_snapshot(candidate_revision):
    rows = _RELEASE_HISTORY_EXCLUSIONS.get(candidate_revision, {})
    # Bound only the optimization payload, never the history search/proof.
    entries = [dict(row, targets=sorted(row['targets'])) for _, row in sorted(rows.items())[-2000:]]
    body = {'schemaVersion': 1, 'candidateRevision': candidate_revision, 'entries': entries}
    body['digest'] = hashlib.sha256(json.dumps(body, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    return body


def import_release_history_snapshot(snapshot, candidate_revision):
    """Consume only the snapshot in an authenticated canonical transaction plan.

    It excludes immutable terminal history, never active or newly retried runs.
    release_operation_history still reads the fresh complete run inventory.
    """
    if not isinstance(snapshot, dict) or set(snapshot) != {'schemaVersion', 'candidateRevision', 'entries', 'digest'}:
        raise RuntimeError('Malformed transaction history snapshot')
    body = {key: value for key, value in snapshot.items() if key != 'digest'}
    digest = hashlib.sha256(json.dumps(body, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    if snapshot['schemaVersion'] != 1 or snapshot['candidateRevision'] != candidate_revision or snapshot['digest'] != digest:
        raise RuntimeError('Transaction history snapshot identity mismatch')
    if not isinstance(snapshot['entries'], list) or len(snapshot['entries']) > 2000:
        raise RuntimeError('Transaction history snapshot is unbounded')
    rows = {}
    for row in snapshot['entries']:
        if (not isinstance(row, dict) or set(row) != {'runId', 'runAttempt', 'headSha', 'targets'} or
                type(row['runId']) is not int or row['runId'] < 1 or row['runId'] in rows or
                type(row['runAttempt']) is not int or row['runAttempt'] < 1 or
                not re.fullmatch('[a-f0-9]{40}', row['headSha']) or
                not isinstance(row['targets'], list) or not row['targets'] or
                len(row['targets']) != len(set(row['targets'])) or any(key not in RELEASE_TARGETS for key in row['targets'])):
            raise RuntimeError('Invalid terminal history exclusion')
        rows[row['runId']] = dict(row, targets=set(row['targets']))
    existing = _RELEASE_HISTORY_EXCLUSIONS.setdefault(candidate_revision, {})
    for run_id, row in rows.items():
        prior = existing.get(run_id)
        if prior is None or prior['runAttempt'] < row['runAttempt']:
            existing[run_id] = row
        elif prior['runAttempt'] == row['runAttempt'] and prior['headSha'] == row['headSha']:
            prior['targets'].update(row['targets'])


def _remember_release_exclusion(candidate_revision, run, target):
    if run.get('status') != 'completed':
        return
    rows = _RELEASE_HISTORY_EXCLUSIONS.setdefault(candidate_revision, {})
    prior = rows.get(run['id'])
    if prior is None or prior['runAttempt'] != run.get('run_attempt', 1) or prior['headSha'] != run['head_sha']:
        prior = {'runId': run['id'], 'runAttempt': run.get('run_attempt', 1), 'headSha': run['head_sha'], 'targets': set()}
        rows[run['id']] = prior
    prior['targets'].add(target)


def _release_history_api(repository, path, token):
    """Share immutable terminal proof in one preparation process, not active state."""
    key = (repository, path)
    if key in _RELEASE_HISTORY_API:
        return _RELEASE_HISTORY_API[key]
    payload = api_get(repository, path, token)
    runs = payload.get('workflow_runs') if isinstance(payload, dict) else None
    if isinstance(runs, list):
        _RELEASE_HISTORY_TERMINAL_RUNS.update((repository, row['id']) for row in runs if row.get('status') == 'completed')
    run_path = re.fullmatch(r'actions/runs/(\d+)', path)
    if run_path and payload.get('status') == 'completed':
        _RELEASE_HISTORY_TERMINAL_RUNS.add((repository, int(run_path.group(1))))
    artifact_path = re.fullmatch(r'actions/runs/(\d+)/artifacts\?per_page=100', path)
    jobs = payload.get('jobs') if isinstance(payload, dict) else None
    immutable = (
        path.startswith('compare/') or
        (run_path is not None and payload.get('status') == 'completed') or
        (isinstance(jobs, list) and all(row.get('status') == 'completed' for row in jobs)) or
        (artifact_path is not None and (repository, int(artifact_path.group(1))) in _RELEASE_HISTORY_TERMINAL_RUNS)
    )
    if immutable:
        _RELEASE_HISTORY_API[key] = payload
    return payload


def _release_history_source(repository, revision, path, token):
    """Read immutable public-safe source, never a runtime payload or log."""
    import base64
    key = (repository, revision, path)
    if key in _RELEASE_HISTORY_SOURCES:
        return _RELEASE_HISTORY_SOURCES[key]
    try:
        source = git_show_file(revision, path)
    except subprocess.SubprocessError:
        row = api_get(repository, f"contents/{path}?ref={revision}", token)
        if row.get("encoding") != "base64" or row.get("size", 0) > 300000:
            raise ReleaseOperationHistoryUnproven("Historical release source is unavailable")
        source = base64.b64decode(row["content"]).decode()
    _RELEASE_HISTORY_SOURCES[key] = source
    return source


def _release_history_json(repository, run_id, artifact, filename):
    import tempfile
    if artifact.get("expired"):
        raise ReleaseOperationHistoryUnproven("Historical publication receipt expired")
    key = (repository, artifact.get('id'), filename)
    if artifact.get('id') and key in _RELEASE_HISTORY_RECEIPTS:
        return _RELEASE_HISTORY_RECEIPTS[key]
    with tempfile.TemporaryDirectory(prefix="legend-release-history-") as directory:
        _download_run_artifact(repository, run_id, artifact["name"], Path(directory))
        file = Path(directory) / filename
        if not file.is_file() or file.stat().st_size > 131072:
            raise RuntimeError("Malformed historical publication receipt")
        receipt = json.loads(file.read_text())
        if artifact.get('id'):
            _RELEASE_HISTORY_RECEIPTS[key] = receipt
        return receipt


def _historical_publication_names(target):
    # Read-only recognition of retired workflow formats; current writers still
    # have one canonical owner and never execute these historical actions.
    labels = {'portal': 'AgentPortal', 'client': 'ClientApp', 'protect': 'Protect',
              'parfait': 'Parfait', 'website': 'Website'}
    label = labels[target]
    return {'Publish selected head as one transaction', f'Publish canonical target ({target})',
            f'Direct deploy {label}', f'Direct deploy {label} immutable ZIP'}


def _legacy_inline_package_revision(workflow, release_job, target, checkout):
    """Recognize the retired inline build -> exact local package -> upload path."""
    release = _job_blocks(workflow).get('release', '')
    blocks = named_step_blocks(release)
    steps = {row['name']: row for row in release_job.get('steps', [])}
    publications = [name for name in _historical_publication_names(target)
                    if name.startswith('Direct deploy ') and name in blocks and name in steps]
    if len(publications) != 1:
        return None
    if steps.get('Load exact preserved deployable package', {}).get('conclusion') == 'success':
        return None  # Reused bytes require their independent producer proof.
    package_name = 'Publish exact selected application packages'
    if steps.get(package_name, {}).get('conclusion') != 'success':
        return None
    package = blocks.get(package_name, '')
    publication = blocks[publications[0]]
    if (release.index(package) >= release.index(publication) or
            re.search(r'git (?:checkout|reset|switch)\b', release) or
            re.search(r'(?m)^\s+RELEASE_SHA:', release)):
        return None
    if target == 'website':
        output = 'Legend-Website/dist'
        if ('"$RELEASE_SHA" > Legend-Website/dist/_deployment-provenance.txt' not in package or
                steps.get('Verify selected website catalog and build', {}).get('conclusion') != 'success'):
            return None
        filename = 'website.zip'
    else:
        project = re.escape(RELEASE_TARGETS[target]['project'])
        match = re.search(r'dotnet publish ' + project +
            r' -c Release --no-build --no-restore -o (/tmp/[a-z]+-publish) -p:SourceRevisionId="\$RELEASE_SHA"', package)
        build = blocks.get('Build exact selected release candidate', '')
        if (not match or steps.get('Build exact selected release candidate', {}).get('conclusion') != 'success' or
                '-p:SourceRevisionId="$RELEASE_SHA"' not in build):
            return None
        output = match.group(1)
        alias = output.removeprefix('/tmp/').removesuffix('-publish')
        if 'apps+=(' + alias + ')' not in package:
            return None
        filename = alias + '.zip'
    if (output not in publication and '/tmp/diagnostics-packages/' + filename not in publication and
            'python3 scripts/deploy-approved-app.py --target ' + target not in publication):
        return None
    if ('zip -qr' not in package or 'sha256sum /tmp/diagnostics-packages/*.zip' not in package):
        return None
    return checkout


class _ReleaseLogRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        if urllib.parse.urlparse(url).scheme != 'https':
            raise ReleaseOperationHistoryUnproven('Historical log redirect must use HTTPS')
        redirected = super().redirect_request(request, response, code, message, headers, url)
        if redirected is not None:
            redirected.remove_header('Authorization')
        return redirected


def _release_job_log(repository, job_id, token):
    """Read the authenticated log redirect with bounded, credential-safe transport."""
    request = urllib.request.Request(
        f'https://api.github.com/repos/{repository}/actions/jobs/{job_id}/logs',
        headers={'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28',
                 'User-Agent': 'legend-validation-resume/1.0'})
    request.add_unredirected_header('Authorization', f'Bearer {token}')
    opener = urllib.request.build_opener(_ReleaseLogRedirect())
    limit = 32 * 1024 * 1024
    for attempt in range(3):
        try:
            with opener.open(request, timeout=30) as response:
                body = response.read(limit + 1)
            if len(body) > limit:
                raise ReleaseOperationHistoryUnproven('Historical checkout log exceeds evidence limit')
            return body.decode('utf-8')
        except (OSError, urllib.error.URLError, UnicodeError) as exc:
            status = getattr(exc, 'code', None)
            retryable = not isinstance(exc, (urllib.error.HTTPError, UnicodeError)) or status in {408, 429, 500, 502, 503, 504}
            if not retryable or attempt == 2:
                reason = f'HTTP {status}' if isinstance(status, int) else type(exc).__name__
                raise ReleaseOperationHistoryUnproven(
                    f'Historical checkout log unavailable (job {job_id}; {reason})') from None
            time.sleep(2 ** attempt)


def _release_checkout_from_job_log(repository, release_job, application, token):
    """Recover only revision metadata from authenticated retained Actions logs.

    Raw logs stay in memory and are never returned, printed, or persisted. This
    is checkout evidence only; source and immutable-package proof still follow.
    """
    job_id = release_job.get('id')
    if (type(job_id) is not int or job_id < 1 or
            not any(step.get('name') == 'Run actions/checkout@v4' and
                    step.get('conclusion') == 'success' for step in release_job.get('steps', []))):
        raise ReleaseOperationHistoryUnproven('Historical successful checkout job proof unavailable')
    cache_key = (repository, job_id, application)
    if release_job.get('status') == 'completed' and cache_key in _RELEASE_HISTORY_LOG_BINDINGS:
        return _RELEASE_HISTORY_LOG_BINDINGS[cache_key]
    raw = _release_job_log(repository, job_id, token)
    lines = [re.sub(r'^\d{4}-\d{2}-\d{2}T[0-9:.]+Z ', '', line)
             for line in raw.splitlines()]
    text = '\n'.join(lines)
    heads = set(re.findall(r'(?m)^\[command\]/usr/bin/git log -1 --format=%H\n([a-f0-9]{40})$', text))
    authorities = set(re.findall(r'(?m)^  RELEASE_SHA: ([a-f0-9]{40})$', text))
    packages = set(re.findall(r'(?m)^  APPLICATION_RELEASE_SHA: ([a-f0-9]{40})$', text))
    if len(heads) != 1 or heads != authorities or packages != {application}:
        raise ReleaseOperationHistoryUnproven('Historical checkout log revision binding is missing or contradictory')
    checkout = heads.pop()
    if release_job.get('status') == 'completed':
        _RELEASE_HISTORY_LOG_BINDINGS[cache_key] = checkout
    return checkout


def _historical_publication_failed_before_first_write(repository, release_job, application_revision, target, checkout, token):
    """Prove one historical canonical target attempt failed before any upload intent/write."""
    import ast

    job_id = release_job.get('id')
    if (type(job_id) is not int or job_id < 1 or release_job.get('status') != 'completed'):
        return False

    # This negative proof is valid only for the exact write-ahead uploader
    # contract: deployment reconciliation checks provider state first, then
    # durably publishes/read-backs intent, and only then calls Azure submit.
    historical_deploy = _release_history_source(
        repository, checkout, 'scripts/deploy-approved-app.py', token)
    historical_evidence = _release_history_source(
        repository, checkout, 'scripts/release-operation-evidence.py', token)
    current_deploy = Path(__file__).with_name('deploy-approved-app.py').read_text()
    current_evidence = Path(__file__).with_name('release-operation-evidence.py').read_text()

    def function_dump(source, name):
        tree = ast.parse(source)
        node = next((row for row in tree.body
                     if isinstance(row, (ast.FunctionDef, ast.AsyncFunctionDef)) and row.name == name), None)
        return ast.dump(node) if node is not None else None

    def method_dump(source, class_name, method_name):
        tree = ast.parse(source)
        owner = next((row for row in tree.body if isinstance(row, ast.ClassDef) and row.name == class_name), None)
        node = next((row for row in owner.body
                     if isinstance(row, (ast.FunctionDef, ast.AsyncFunctionDef)) and row.name == method_name), None) if owner else None
        return ast.dump(node) if node is not None else None

    if (function_dump(historical_deploy, 'reconcile') != function_dump(current_deploy, 'reconcile') or
            method_dump(historical_evidence, 'OperationJournal', 'before_submit') !=
            method_dump(current_evidence, 'OperationJournal', 'before_submit')):
        return False

    # Authenticated retained logs are only execution evidence after source
    # contract identity is proven. Require the exact target/revision start,
    # the fail-closed pre-upload exception, and no submit marker in that target
    # execution segment.
    raw = _release_job_log(repository, job_id, token)
    lines = [re.sub(r'^\d{4}-\d{2}-\d{2}T[0-9:.]+Z ', '', line)
             for line in raw.splitlines()]
    text = '\n'.join(lines)
    release_name = RELEASE_TARGETS[target]['releaseName']
    start_marker = f'{release_name}: approved revision {application_revision}, ZIP sha256 '
    starts = [match.start() for match in re.finditer(re.escape(start_marker), text)]
    if len(starts) != 1:
        return False
    start = starts[0]
    end_marker = '\n##[error]Process completed with exit code 1.'
    end = text.find(end_marker, start)
    if end < 0:
        return False
    segment = text[start:end + len(end_marker)]
    expected = (
        'DeploymentStatusUnavailable: Azure deployment status remained unavailable for 3 consecutive reads '
        '(before any upload). No deployment or rollback write was replayed; resume by reconciling the exact revision.'
    )
    return expected in segment and 'Submitting the verified immutable ZIP once.' not in segment


def _historical_parallel_verifier_compatible(functions, current_functions):
    """Recognize reviewed publication generations without restoring old success semantics."""
    import ast
    for name in ('publish_prepared_target', 'publish_prepared_targets_parallel'):
        if name not in functions or name not in current_functions:
            return False
        historical = ast.dump(functions[name])
        current = ast.dump(current_functions[name])
        if historical == current:
            continue
        # 417f278 changed only the parallel result-reporting contract from a
        # first-pass success flag to explicit nonterminal live/receipt evidence.
        # Both reviewed generations call the identical immutable target verifier.
        # This proves package identity only; old success flags never prove live
        # deployment, settle an intent, or authorize replay.
        if name != 'publish_prepared_targets_parallel' or (
            hashlib.sha256(historical.encode()).hexdigest(),
            hashlib.sha256(current.encode()).hexdigest(),
        ) != (
            'cb3f333f35a82e47bb8e6e2bf178399022451de69550923348d77252a1c35392',
            '9c63956c377037597ee1c5480d8066090fce4ded791f7138dc3c05932129b22a',
        ):
            return False
    return True


def _release_attempt_package_revision(repository, run, attempt, release_job, token, target):
    """Bind legacy publication to the package's verified embedded revision.

    Workflow event SHA is never substituted for checkout/package authority.
    Existing state and package receipts remain the only evidence channel.
    """
    import ast
    run_id = run['id']
    cache_key = (repository, run_id, attempt, run['head_sha'], target, json.dumps(release_job, sort_keys=True))
    if cache_key in _RELEASE_HISTORY_VERIFIED_PACKAGES:
        return _RELEASE_HISTORY_VERIFIED_PACKAGES[cache_key]
    inventory = _release_history_api(repository, f"actions/runs/{run_id}/artifacts?per_page=100", token)
    artifacts = inventory.get('artifacts')
    if not isinstance(artifacts, list) or inventory.get('total_count', 0) > len(artifacts):
        raise ReleaseOperationHistoryUnproven('Incomplete historical receipt inventory')
    states = [row for row in artifacts if re.fullmatch(
        rf"legend-release-step-state-[a-f0-9]{{40}}-{run_id}-{attempt}", row.get('name', ''))]
    if len(states) > 1:
        raise ReleaseOperationHistoryUnproven('Historical state receipt names are ambiguous')
    receipt = None
    if len(states) == 1:
        state = _release_history_json(repository, run_id, states[0], 'legend-release-step-state.json')
        if state.get('schemaVersion') != 2 or state.get('runId') != run_id or state.get('runAttempt') != attempt:
            raise RuntimeError('Historical release state producer identity mismatch')
        # This state file's releaseHeadSha is the workflow event SHA, not checkout.
        # Corroborate its step proof against independently retained Actions data.
        actual = [(row.get('name'), row.get('status'), row.get('conclusion')) for row in release_job.get('steps', [])]
        claimed = [(row.get('name'), row.get('status'), row.get('conclusion')) for row in state.get('steps', [])]
        if actual != claimed or state.get('releaseJobConclusion') != release_job.get('conclusion'):
            raise RuntimeError('Historical release state contradicts Actions execution proof')
        receipt = state
    elif attempt == 1 and run.get('run_attempt', 1) == 1:
        approved = [row for row in artifacts if re.fullmatch(r'legend-approved-release-[a-f0-9]{40}', row.get('name', ''))]
        if len(approved) == 1 and release_job.get('conclusion') == 'success':
            receipt = _release_history_json(repository, run_id, approved[0], 'approved-release-receipt.json')
            if receipt.get('schemaVersion') != 2:
                # Older receipts are not interpreted as current proof. The
                # independently verified RELEASE_SHA package contract below
                # may still prove which bytes that legacy run published.
                receipt = None
            elif receipt.get('transaction') not in {'committed', 'preserved'}:
                raise RuntimeError('Historical approved receipt contract mismatch')
    if receipt is not None and not re.fullmatch('[a-f0-9]{40}', receipt.get('applicationReleaseSha', '')):
        raise RuntimeError('Historical receipt package revision is malformed')
    application = receipt['applicationReleaseSha'] if receipt else None
    translations = {row['name'].removeprefix('translation-direct-release-') for row in artifacts
                    if not row.get('expired') and re.fullmatch('translation-direct-release-[a-f0-9]{40}', row.get('name', ''))}
    rollbacks = set()
    for artifact in artifacts:
        match = re.fullmatch(r'diagnostics-rollback-([a-z]+)-([a-f0-9]{40})', artifact.get('name', ''))
        if match and not artifact.get('expired') and match.group(1) in RELEASE_TARGETS:
            rollbacks.add(match.group(2))
    checkouts = translations | rollbacks
    from_log = not checkouts and receipt is not None
    if from_log:
        checkout = _release_checkout_from_job_log(repository, release_job, application, token)
    elif len(checkouts) == 1:
        checkout = checkouts.pop()
    else:
        raise ReleaseOperationHistoryUnproven('No authenticated legacy package checkout/producer revision (missing or ambiguous)')
    workflow = _release_history_source(repository, run['head_sha'], '.github/workflows/' + DIRECT_RELEASE_WORKFLOW, token)
    # Both existing artifact families bind the execution authority, not the
    # rollback package's embedded revision. Package proof below remains required.
    rollback_job = _job_blocks(workflow).get('preserve-rollback', '')
    release = _job_blocks(workflow).get('release', '')
    checkout_step = re.search(r'(?m)^      - uses: actions/checkout@v4\n((?:        [^\n]*\n|\n)*)', release)
    event_checkout = (
        'RELEASE_SHA: ${{ github.sha }}' in workflow and checkout == run['head_sha'] and
        checkout_step is not None and (not re.search(r'(?m)^\s+ref:', checkout_step.group(1)) or
            re.findall(r'(?m)^\s+ref: (.*)$', checkout_step.group(1)) == ['${{ github.sha }}']) and
        not re.search(r'(?m)^\s+RELEASE_SHA:', release))
    producer_binding = (
        (event_checkout and bool(translations) and
         'name: translation-direct-release-${{ github.sha }}' in release) or
        (from_log and 'uses: actions/checkout@v4' in release and
         'ref: ${{ env.RELEASE_SHA }}' in release) or
        (bool(translations) and 'name: translation-direct-release-${{ env.RELEASE_SHA }}' in workflow) or
        (bool(rollbacks) and 'name: diagnostics-rollback-${{ matrix.app }}-${{ env.RELEASE_SHA }}' in
         rollback_job and not re.search(r'(?m)^\s+RELEASE_SHA:', rollback_job)))
    if (('RELEASE_SHA: ${{ inputs.merge_sha || github.sha }}' not in workflow and not event_checkout) or
            not producer_binding or re.search(r'(?<![A-Z_])RELEASE_SHA\s*=', workflow)):
        raise ReleaseOperationHistoryUnproven('Historical checkout receipt has no recognized producer binding')
    release = _job_blocks(workflow).get('release', '')
    inline_revision = _legacy_inline_package_revision(workflow, release_job, target, checkout)
    if inline_revision is not None:
        if receipt is not None and application != inline_revision:
            raise RuntimeError('Historical inline package proof contradicts retained receipt')
        if 'python3 scripts/deploy-approved-app.py --target ' + target in release:
            deployment_source = _release_history_source(repository, checkout, 'scripts/deploy-approved-app.py', token)
            tree = ast.parse(deployment_source)
            functions = {node.name: node for node in tree.body if isinstance(node, ast.FunctionDef)}
            current = ast.parse(Path(__file__).with_name('deploy-approved-app.py').read_text())
            verifier = next(node for node in current.body if isinstance(node, ast.FunctionDef) and node.name == 'verify_package')
            legacy_main = ast.parse('''def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--target', choices=TARGETS, required=True)
    args = parser.parse_args()
    app, filename, url = TARGETS[args.target]
    revision = os.environ['RELEASE_SHA']
    package = Path('/tmp/diagnostics-packages') / filename
    digest = verify_package(package, revision, args.target == 'website')
    print(f'{app}: approved revision {revision}, ZIP sha256 {digest}', flush=True)
    result = reconcile(Azure(app, package, url, revision, args.target == 'website'))
    print(f'{app}: {result}; exact revision healthy and no Azure deployment pending.', flush=True)
''').body[0]
            if (not all(name in functions for name in ('main', 'verify_package')) or
                    ast.dump(functions['main']) != ast.dump(legacy_main) or
                    ast.dump(functions['verify_package']) != ast.dump(verifier)):
                raise ReleaseOperationHistoryUnproven('Historical inline uploader contract is incompatible')
        if release_job.get('status') == 'completed':
            _RELEASE_HISTORY_VERIFIED_PACKAGES[cache_key] = inline_revision
        return inline_revision
    if 'APPLICATION_RELEASE_SHA' in release:
        if ('APPLICATION_RELEASE_SHA: ${{ needs.discover-live.outputs.application_release_sha }}' not in release or
                release.count('APPLICATION_RELEASE_SHA:') != 1 or re.search(r'APPLICATION_RELEASE_SHA\s*=', release)):
            raise ReleaseOperationHistoryUnproven('Historical package revision binding is unknown')
        if application is None:
            raise ReleaseOperationHistoryUnproven('No authenticated legacy package producer revision')
        revision_variable = 'APPLICATION_RELEASE_SHA'
    else:
        # Prior to content-equivalent package reuse, the package's embedded SHA
        # was exactly the checked-out RELEASE_SHA. The source + successful verify
        # proof is required; a translation artifact name alone is never enough.
        revision_variable = 'RELEASE_SHA'
        if re.search(r'(?m)^\s+RELEASE_SHA:', release):
            raise ReleaseOperationHistoryUnproven('Historical release overrides its verified checkout revision')
        if application is not None and application != checkout:
            raise RuntimeError('Legacy package receipt contradicts verified checkout revision')
        application = checkout
    lines, spans = _named_step_spans(release)
    steps = {row['name']: row for row in release_job.get('steps', [])}
    target_evidence_names = set(_historical_publication_names(target))
    target_evidence_names.add(f'Confirm first-pass durable publication receipt ({target})')
    publication = next(((name, start, end) for name, start, end in spans
                        if name in target_evidence_names), None)
    if publication is None:
        raise ReleaseOperationHistoryUnproven('Historical publication source is unknown')
    target_body = ''.join(lines[publication[1]:publication[2]])
    publish_body = target_body
    publication_start = publication[1]
    parallel_mode = False
    if 'python3 scripts/deploy-approved-app.py' not in publish_body:
        parallel_names = {
            'Publish canonical selected targets in parallel',
            'Submit canonical selected targets in parallel',
        }
        parallel = next(((name, start, end) for name, start, end in spans
                         if name in parallel_names), None)
        release_name = RELEASE_TARGETS[target]['releaseName']
        if (
            parallel is None or parallel[1] >= publication[1] or
            f'/tmp/release-target-results/{target}.json' not in target_body or
            f"contains(fromJSON(env.SELECTED_TARGETS), '{release_name}')" not in target_body
        ):
            raise ReleaseOperationHistoryUnproven('Historical publication does not use canonical immutable verifier')
        parallel_body = ''.join(lines[parallel[1]:parallel[2]])
        if (
            'python3 scripts/deploy-approved-app.py' not in parallel_body or
            '--publish-prepared-parallel' not in parallel_body or
            '--targets-json "$SELECTED_TARGETS"' not in parallel_body or
            '--transaction-plan /tmp/release-transaction.json' not in parallel_body
        ):
            raise ReleaseOperationHistoryUnproven('Historical parallel publication contract is incompatible')
        publish_body = parallel_body
        publication_start = parallel[1]
        parallel_mode = True
    verified = any(start < publication_start and steps.get(name, {}).get('conclusion') == 'success' and
                   'scripts/release-package.py verify' in ''.join(lines[start:end]) and
                   f'--revision "${revision_variable}"' in ''.join(lines[start:end])
                   for name, start, end in spans)
    if not verified:
        raise ReleaseOperationHistoryUnproven('Legacy package did not pass embedded revision verification')
    deployment_source = _release_history_source(repository, checkout, 'scripts/deploy-approved-app.py', token)
    tree = ast.parse(deployment_source)
    functions = {node.name: node for node in tree.body if isinstance(node, ast.FunctionDef)}
    current = ast.parse(Path(__file__).with_name('deploy-approved-app.py').read_text())
    current_functions = {node.name: node for node in current.body if isinstance(node, ast.FunctionDef)}
    current_verify = current_functions['verify_package']
    if parallel_mode and not _historical_parallel_verifier_compatible(functions, current_functions):
        raise ReleaseOperationHistoryUnproven('Historical parallel publication verifier contract is incompatible')
    expected = [ast.parse("revision = os.environ.get('APPLICATION_RELEASE_SHA') or os.environ.get('RELEASE_SHA')").body[0]]
    if revision_variable == 'RELEASE_SHA':
        expected.extend(ast.parse(text).body[0] for text in ("revision = os.environ['RELEASE_SHA']", "revision = os.environ.get('RELEASE_SHA')"))
    main = functions.get('main')
    if ('verify_package' not in functions or ast.dump(functions['verify_package']) != ast.dump(current_verify) or
            main is None or not any(ast.dump(node) == ast.dump(binding) for node in main.body for binding in expected)):
        raise ReleaseOperationHistoryUnproven('Historical deployment verifier contract is incompatible')
    one = functions.get('deploy_one')
    if one is None or not any(isinstance(node, ast.Assign) and isinstance(node.value, ast.Call) and
                             isinstance(node.value.func, ast.Name) and node.value.func.id == 'verify_package'
                             for node in one.body):
        raise ReleaseOperationHistoryUnproven('Historical publication does not reverify immutable bytes')
    if release_job.get('status') == 'completed':
        _RELEASE_HISTORY_VERIFIED_PACKAGES[cache_key] = application
    return application


def _release_history_runs(repository, token):
    """Fresh complete unfiltered inventory; active/page movement is never cached."""
    workflow = urllib.parse.quote(DIRECT_RELEASE_WORKFLOW, safe="")
    page, seen, total = 1, set(), None
    while True:
        payload = _release_history_api(repository, f"actions/workflows/{workflow}/runs?per_page=100&page={page}", token)
        rows, count = payload.get('workflow_runs'), payload.get('total_count')
        if not isinstance(rows, list) or type(count) is not int or count < 0:
            raise ReleaseOperationHistoryUnproven('Missing release execution history')
        if total is None:
            total = count
        elif total != count:
            raise ReleaseOperationHistoryUnproven('Release inventory changed during pagination; retry read-only discovery')
        for row in rows:
            if type(row.get('id')) is not int or row['id'] in seen:
                raise ReleaseOperationHistoryUnproven('Release inventory pagination repeated a run')
            seen.add(row['id'])
            yield row
        if len(rows) < 100:
            if len(seen) != total:
                raise ReleaseOperationHistoryUnproven('Release execution history is truncated')
            return
        page += 1


def _release_nonentry_observers(jobs, source):
    """Authenticate observers with no capability to publish within this attempt."""
    names = {'release-state-receipt', 'target-release-receipts (${{ matrix.app }})',
             'wake-release-lifecycle-after-terminal-release'}
    present = [job for job in jobs if job.get('name') in names]
    if not present:
        return jobs
    if (not isinstance(source, str) or {job['name'] for job in present} != names
        or len(present) != len(names)):
        return None
    blocks = _job_blocks(source)
    if (set(blocks) != {'admission', 'discover-live', 'preserve-rollback', 'release',
                        'release-state-receipt', 'target-release-receipts',
                        'wake-release-lifecycle-after-terminal-release'}
        or any(job.get('status') != 'completed' for job in jobs)):
        return None
    contract = {name: blocks.get(name) for name in (
        'release-state-receipt', 'target-release-receipts',
        'wake-release-lifecycle-after-terminal-release')}
    contract.update(environment=_workflow_top_level_field(source, 'env'),
                    defaults=_workflow_top_level_field(source, 'defaults'))
    # Exact a07afe8 observer capability contract: inline state capture/upload,
    # plus a scheduler wake with contents:read/actions:write only. The wake is
    # not read-only, but has no Azure authentication, secrets or OIDC permission
    # to publish here. Its protected checkout can dispatch another workflow;
    # that separate run remains in complete release history and admission.
    # Unknown permissions, commands or ambient env fail closed. A job name
    # alone never grants this non-publication classification.
    if hashlib.sha256(json.dumps(contract, sort_keys=True).encode()).hexdigest() != 'ce5e7dfe3d4100116bae366833687d6055dd51b91357cc268c73d33105b999c7':
        return None
    for job in present:
        if job.get('status') != 'completed' or job.get('conclusion') not in {'success', 'failure', 'cancelled', 'skipped'}:
            return None
        steps = job.get('steps')
        if not isinstance(steps, list):
            return None
        if job['name'].startswith('target-release-receipts'):
            if job['conclusion'] != 'skipped' or steps:
                return None
            continue
        permitted = set(named_step_blocks(blocks[job['name']])) | {'Set up job', 'Complete job'}
        if job['name'] == 'wake-release-lifecycle-after-terminal-release':
            permitted.add('Post Checkout protected lifecycle wake authority')
        step_names = []
        for step in steps:
            if (not isinstance(step, dict) or step.get('name') not in permitted
                or step.get('status') != 'completed'
                or step.get('conclusion') not in {'success', 'failure', 'cancelled', 'skipped'}):
                return None
            step_names.append(step['name'])
        if len(step_names) != len(set(step_names)):
            return None
    return [job for job in jobs if job['name'] not in names]


def release_attempt_never_entered(jobs, source=None):
    """Recognize a complete trusted attempt whose downstream jobs never entered.

    Callers own authenticated producer/source and complete attempt enumeration.
    GitHub can omit both impossible downstream jobs after admission. Only its
    exact admission plus skipped rollback wrapper shape proves that omission;
    partial, duplicate, unknown, or entered job shapes remain unproven.
    """
    if not isinstance(jobs, list) or not jobs or any(not isinstance(job, dict) for job in jobs):
        return False
    jobs = _release_nonentry_observers(jobs, source)
    if jobs is None:
        return False
    names = [job.get('name') for job in jobs]
    allowed = {'admission', 'discover-live', 'release', 'preserve-rollback'}
    if (any(name not in allowed for name in names) or
            len(names) != len(set(names)) or names.count('admission') != 1):
        return False
    downstream = set(names) - {'admission', 'preserve-rollback'}
    if downstream not in (set(), {'discover-live', 'release'}):
        return False
    if not downstream and set(names) != {'admission', 'preserve-rollback'}:
        return False
    if not downstream:
        # Omission is meaningful only for this historical admission/needs
        # generation. An evolved or unavailable source is not absence proof.
        if not isinstance(source, str):
            return False
        blocks = _job_blocks(source)
        if set(blocks) != {'admission', 'discover-live', 'preserve-rollback', 'release',
                           'target-release-receipts', 'release-state-receipt',
                           'wake-release-lifecycle-after-terminal-release'}:
            return False
        gates = {
            'discover-live': {
                'needs': 'admission',
                'if': "needs.admission.outputs.admitted == 'true' && needs.admission.outputs.state == 'RELEASE_READY'",
            },
            'preserve-rollback': {
                'needs': 'discover-live',
                'if': "needs.discover-live.outputs.preserve_live_targets != 'true' && needs.discover-live.outputs.exact_live != 'true'",
            },
            'release': {
                'needs': '[discover-live, preserve-rollback]',
                'if': "${{ always() && needs.discover-live.result == 'success' && (needs.preserve-rollback.result == 'success' || needs.preserve-rollback.result == 'skipped') }}",
            },
        }
        for name, fields in gates.items():
            for field, expected in fields.items():
                values = re.findall(r'^    ' + field + r': (.*)$', blocks[name], re.MULTILINE)
                if values != [expected]:
                    return False
    for job in jobs:
        if job['name'] == 'admission':
            continue
        if job.get('conclusion') != 'skipped':
            return False
        # Contradictory execution detail cannot be treated as non-entry.
        steps = job.get('steps', [])
        if not isinstance(steps, list) or any(
                not isinstance(step, dict) or step.get('conclusion') != 'skipped'
                for step in steps):
            return False
    return True


def release_operation_history(repository, operation_id, application_revision, target, current_run, current_attempt, token, *, phase="intent"):
    """Return retained write-ahead intent, or prove publication never started.

    Absence/expiry is never negative deployment evidence. The independent Actions
    execution history must show the target publication never started. Unknown or
    truncated history is an error, not permission to upload again.
    """
    import tempfile
    def read_api(path):
        try:
            return _release_history_api(repository, path, token)
        except OSError as exc:
            raise ReleaseOperationHistoryUnproven("Release evidence provider unavailable") from exc
    if phase not in {"intent", "success"}:
        raise ValueError("Unknown deployment evidence phase")
    name = f"legend-release-operation-{phase}-" + operation_id
    encoded = urllib.parse.quote(name, safe="")
    payload = read_api(f"actions/artifacts?name={encoded}&per_page=100")
    artifacts = payload.get("artifacts")
    if not isinstance(artifacts, list) or payload.get("total_count", 0) > len(artifacts):
        raise ReleaseOperationHistoryUnproven("Incomplete deployment intent artifact inventory")
    records = []
    for artifact in artifacts:
        if artifact.get("name") != name:
            raise RuntimeError("Deployment intent artifact identity mismatch")
        run_id = artifact.get("workflow_run", {}).get("id")
        if not run_id:
            raise RuntimeError("Deployment intent producer is missing")
        run = read_api(f"actions/runs/{run_id}")
        if (run.get("path") != ".github/workflows/" + DIRECT_RELEASE_WORKFLOW or
                run.get("head_branch") != TRUSTED_PR_BASE or run.get("event") != "workflow_dispatch" or
                run.get("head_repository", {}).get("full_name", "").lower() != repository.lower()):
            raise RuntimeError("Deployment intent has an untrusted producer")
        if artifact.get("expired"):
            raise ReleaseOperationHistoryUnproven("Deployment intent expired; reconciliation required")
        with tempfile.TemporaryDirectory(prefix="legend-operation-read-") as directory:
            try:
                _download_run_artifact(repository, run_id, name, Path(directory))
            except (OSError, subprocess.SubprocessError) as exc:
                raise ReleaseOperationHistoryUnproven("Deployment intent download unavailable") from exc
            path = Path(directory) / "operation.json"
            if path.stat().st_size > 32768:
                raise RuntimeError("Oversized deployment intent")
            record = json.loads(path.read_text())
        if (record.get("operationId") != operation_id or record.get("target") != target or
                record.get("applicationRevision") != application_revision or
                record.get("producingRun") != run_id or record.get("phase") != phase):
            raise RuntimeError("Deployment intent does not bind its producer and operation")
        records.append(record)
    if records:
        identity_keys = ("operationId", "applicationRevision", "target", "packageDigest", "baseline")
        if any(any(record.get(key) != records[0].get(key) for key in identity_keys) for record in records[1:]):
            raise RuntimeError("Competing deployment intents require reconciliation")
        if phase == "intent" and any(record != records[0] for record in records[1:]):
            raise RuntimeError("Competing deployment intent producers require reconciliation")
        return records[0]
    if phase == "success":
        return None

    for run in _release_history_runs(repository, token):
        if run.get("head_branch") != TRUSTED_PR_BASE or run.get("event") != "workflow_dispatch":
            continue
        run_id = run["id"]
        if run_id == current_run and current_attempt == 1:
            continue
        if (run.get('path') != '.github/workflows/' + DIRECT_RELEASE_WORKFLOW or
                run.get('head_repository', {}).get('full_name', '').lower() != repository.lower()):
            raise RuntimeError('Release execution history has an untrusted producer')
        prior_proof = _RELEASE_HISTORY_EXCLUSIONS.get(application_revision, {}).get(run_id)
        if (prior_proof and run.get('status') == 'completed' and prior_proof['headSha'] == run.get('head_sha') and
                prior_proof['runAttempt'] == run.get('run_attempt', 1) and target in prior_proof['targets']):
            continue
        # Modern titles bind actual checkout authority. Legacy receipts below
        # bind the verified package producer instead of guessing event SHA.
        title = re.fullmatch(r"LEGEND release pr=[0-9]+ candidate=[a-f0-9]{40} authority=([a-f0-9]{40})", run.get("display_title", ""))
        if title:
            comparison = read_api(f"compare/{application_revision}...{title.group(1)}")
            if comparison.get("status") in {"behind", "diverged"}:
                _remember_release_exclusion(application_revision, run, target)
                continue
            if comparison.get("status") not in {"ahead", "identical"}:
                raise ReleaseOperationHistoryUnproven("Unknown candidate ancestry in release history")
        attempts = run.get("run_attempt", 1)
        if type(attempts) is not int or attempts < 1:
            raise ReleaseOperationHistoryUnproven("Invalid prior deployment attempt inventory")
        for attempt in range(1, attempts + 1):
            if run_id == current_run and attempt == current_attempt:
                continue
            jobs_payload = read_api(f"actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100")
            jobs = jobs_payload.get("jobs")
            count = jobs_payload.get("total_count")
            if (not isinstance(jobs, list) or type(count) is not int or count != len(jobs)):
                raise ReleaseOperationHistoryUnproven("Incomplete prior deployment job history")
            if (not jobs and jobs_payload.get('total_count') == 0 and
                    (run.get('status') == 'completed' or attempt < attempts)):
                # Complete Actions enumeration proves this attempt never started a job.
                continue
            if release_attempt_never_entered(jobs):
                continue
            if (any(job.get('name') == 'admission' for job in jobs) and
                    not any(job.get('name') in {'release', 'discover-live'} for job in jobs)):
                source = _release_history_source(repository, run['head_sha'], '.github/workflows/' + DIRECT_RELEASE_WORKFLOW, token)
                if release_attempt_never_entered(jobs, source):
                    continue
            owners = [job for job in jobs if job.get("name") in {"release", f"publish-target ({target})"}]
            if len(owners) != 1:
                raise ReleaseOperationHistoryUnproven("Release execution generation lacks one canonical publication owner")
            job = owners[0]
            if (job.get('conclusion') == 'skipped' or job.get('status') == 'queued' or
                    (job.get('status') == 'completed' and job.get('conclusion') == 'cancelled' and
                     job.get('steps') == [])):
                continue
            names = _historical_publication_names(target)
            publication = [step for step in job.get('steps', []) if step.get('name') in names]
            shared_fanout = [
                step for step in job.get('steps', [])
                if step.get('name') in {
                    'Publish canonical selected targets in parallel',
                    'Submit canonical selected targets in parallel',
                }
            ]
            if len(shared_fanout) == 1 and (
                shared_fanout[0].get('conclusion') == 'skipped'
                or shared_fanout[0].get('status') == 'queued'
            ):
                # The shared mutation owner never entered, so none of its selected
                # app targets can have written even if target receipt observers exist.
                continue
            if len(publication) == 1 and (publication[0].get('conclusion') == 'skipped' or publication[0].get('status') == 'queued'):
                # Positive target execution proof works for retired target-owned
                # publication formats even when old receipt artifacts expired.
                continue
            if job.get('name') != 'release':
                raise ReleaseOperationHistoryUnproven('Target publication entered without retained intent')
            try:
                prior_revision = _release_attempt_package_revision(repository, run, attempt, job, token, target)
            except (OSError, subprocess.SubprocessError) as exc:
                raise ReleaseOperationHistoryUnproven('Historical package proof provider unavailable') from exc
            if prior_revision == application_revision:
                if _historical_publication_failed_before_first_write(
                        repository, job, application_revision, target, prior_revision, token):
                    # The exact historical source contract plus authenticated log
                    # prove this target failed before durable intent and before Azure submit.
                    continue
                raise ReleaseOperationHistoryUnproven('Prior publication may have written this immutable package; missing intent is not absence proof')
        _remember_release_exclusion(application_revision, run, target)
    return None



def _failed_transaction_preparation_without_writes(source, owner):
    """A failed preflight is not a lost plan when every later effect was skipped."""
    if owner.get('status') != 'completed' or owner.get('conclusion') != 'failure':
        return False
    blocks = named_step_blocks(_job_blocks(source).get('release', ''))
    prepare = 'Prepare complete immutable release transaction'
    if prepare not in blocks or '--prepare-only' not in blocks[prepare]:
        return False
    steps = owner.get('steps')
    if not isinstance(steps, list):
        return False
    outcomes = {}
    for step in steps:
        outcomes.setdefault(step.get('name'), []).append(step)
    failed = outcomes.get(prepare, [])
    if len(failed) != 1 or failed[0].get('conclusion') != 'failure':
        return False
    # These observers may run after failure. Require their original bodies to
    # match the canonical source before excluding them from execution proof.
    observers = {'Refresh Azure OIDC before transactional publication',
                 'Enforce complete direct deployment outcome'}
    current = named_step_blocks(_job_blocks(
        Path('.github/workflows/' + DIRECT_RELEASE_WORKFLOW).read_text()).get('release', ''))
    later = list(blocks)[list(blocks).index(prepare) + 1:]
    legacy_targets = {f'Publish canonical target ({key})' for key in RELEASE_TARGETS}
    receipt_targets = {
        f'Confirm first-pass durable publication receipt ({key})'
        for key in RELEASE_TARGETS
    }
    legacy_shape = legacy_targets.issubset(set(later))
    current_shape = (
        'Submit canonical selected targets in parallel' in later
        and receipt_targets.issubset(set(later))
    )
    if not (legacy_shape or current_shape):
        return False
    for name in later:
        if name in observers and blocks[name] == current.get(name):
            continue
        matches = outcomes.get(name, [])
        if len(matches) != 1 or matches[0].get('conclusion') != 'skipped':
            return False
    return True


def release_transaction_plan_history(repository, plan_id, revision, target_digests, run, attempt, token):
    """Restore the complete original transaction, including untouched targets.

    None only means this exact plan artifact is absent. It is never first-write
    authorization; per-target history and canonical admission still must pass.
    """
    identity = {'candidateRevision': revision, 'packageDigests': dict(sorted(target_digests.items()))}
    expected = hashlib.sha256(json.dumps(identity, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    if plan_id != expected or not target_digests or any(key not in RELEASE_TARGETS for key in target_digests):
        raise ValueError('Transaction plan scope/content identity mismatch')
    name = 'legend-release-transaction-plan-' + plan_id
    inventory = _release_history_api(repository, f"actions/artifacts?name={name}&per_page=100", token)
    artifacts = inventory.get('artifacts')
    if not isinstance(artifacts, list) or inventory.get('total_count', 0) > len(artifacts):
        raise ReleaseOperationHistoryUnproven('Incomplete transaction plan inventory')
    retained = None
    for artifact in artifacts:
        if artifact.get('name') != name:
            raise RuntimeError('Transaction plan artifact identity mismatch')
        producer = artifact.get('workflow_run', {}).get('id')
        if not producer:
            raise RuntimeError('Transaction plan producer is missing')
        producing_run = _release_history_api(repository, f'actions/runs/{producer}', token)
        if (producing_run.get('path') != '.github/workflows/' + DIRECT_RELEASE_WORKFLOW or
                producing_run.get('head_branch') != TRUSTED_PR_BASE or producing_run.get('event') != 'workflow_dispatch' or
                producing_run.get('head_repository', {}).get('full_name', '').lower() != repository.lower()):
            raise RuntimeError('Transaction plan producer is untrusted')
        plan = _release_history_json(repository, producer, artifact, 'release-transaction.json')
        if (plan.get('schemaVersion') != 1 or plan.get('planId') != plan_id or
                plan.get('candidateRevision') != revision or plan.get('producingRun') != producer or
                type(plan.get('producingAttempt')) is not int or plan['producingAttempt'] < 1 or
                plan['producingAttempt'] > producing_run.get('run_attempt', 1)):
            raise RuntimeError('Transaction plan does not bind immutable producer/scope')
        rows = plan.get('targets')
        if not isinstance(rows, list) or len(rows) != len(target_digests):
            raise RuntimeError('Original transaction cannot be silently split')
        seen = set()
        for row in rows:
            key = row.get('app')
            if (key in seen or key not in target_digests or row.get('packageDigest') != target_digests[key] or
                    not re.fullmatch('[a-f0-9]{40}', row.get('revision', ''))):
                raise RuntimeError('Transaction original baseline/package binding is invalid')
            seen.add(key)
            rollback = row.get('rollbackEvidence')
            if row['revision'] == revision:
                if rollback is not None:
                    raise RuntimeError('Candidate target has unexpected rollback evidence')
            elif (not isinstance(rollback, dict) or set(rollback) != {'artifact', 'runId', 'revision', 'packageDigest'} or
                  rollback.get('revision') != row['revision'] or type(rollback.get('runId')) is not int or rollback['runId'] < 1 or
                  not re.fullmatch('[a-zA-Z0-9_.-]{1,256}', rollback.get('artifact', '')) or
                  not re.fullmatch('[a-f0-9]{64}', rollback.get('packageDigest', ''))):
                raise RuntimeError('Transaction rollback package proof is invalid')
        if retained is not None and retained.get('targets') != rows:
            raise RuntimeError('Competing original transaction baselines require reconciliation')
        retained = plan if retained is None else retained
    if retained is not None:
        if 'historySnapshot' not in retained:
            raise RuntimeError('Transaction plan lacks authenticated history snapshot')
        import_release_history_snapshot(retained['historySnapshot'], revision)
        return retained
    # A deleted full plan is not permission to bless reobserved live baselines
    # for an untouched sibling. Prove no prior overlapping original transaction.
    for prior in _release_history_runs(repository, token):
        if prior.get('head_branch') != TRUSTED_PR_BASE or prior.get('event') != 'workflow_dispatch':
            continue
        if (prior.get('path') != '.github/workflows/' + DIRECT_RELEASE_WORKFLOW or
                prior.get('head_repository', {}).get('full_name', '').lower() != repository.lower()):
            raise RuntimeError('Transaction execution history has an untrusted producer')
        if prior['id'] == run and attempt == 1:
            continue
        source = _release_history_source(repository, prior['head_sha'], '.github/workflows/' + DIRECT_RELEASE_WORKFLOW, token)
        if 'Prepare complete immutable release transaction' not in named_step_blocks(_job_blocks(source).get('release', '')):
            # This historical generation had no durable all-target plan to lose.
            # Its writes remain governed by the operation history proof below.
            continue
        attempts = prior.get('run_attempt', 1)
        if type(attempts) is not int or attempts < 1:
            raise ReleaseOperationHistoryUnproven('Invalid original transaction attempt inventory')
        for previous_attempt in range(1, attempts + 1):
            if prior['id'] == run and previous_attempt == attempt:
                continue
            jobs = _release_history_api(repository, f"actions/runs/{prior['id']}/attempts/{previous_attempt}/jobs?per_page=100", token)
            rows = jobs.get('jobs')
            count = jobs.get('total_count')
            if (not isinstance(rows, list) or type(count) is not int or count != len(rows)):
                raise ReleaseOperationHistoryUnproven('Original transaction execution history is incomplete')
            if release_attempt_never_entered(rows, source):
                continue
            owners = [row for row in rows if row.get('name') == 'release']
            if len(owners) != 1:
                raise ReleaseOperationHistoryUnproven('Original transaction publication owner is missing')
            owner = owners[0]
            if owner.get('conclusion') == 'skipped' or owner.get('status') == 'queued':
                continue
            prepared = [row for row in owner.get('steps', []) if row.get('name') == 'Prepare complete immutable release transaction']
            if len(prepared) != 1:
                raise ReleaseOperationHistoryUnproven('Original transaction preparation history is missing')
            if prepared[0].get('conclusion') == 'skipped' or prepared[0].get('status') == 'queued':
                continue
            prior_revision = _release_attempt_package_revision(repository, prior, previous_attempt, owner, token, next(iter(target_digests)))
            if prior_revision != revision:
                continue
            artifacts = _release_history_api(repository, f"actions/runs/{prior['id']}/artifacts?per_page=100", token)
            items = artifacts.get('artifacts')
            if not isinstance(items, list) or artifacts.get('total_count', 0) > len(items):
                raise ReleaseOperationHistoryUnproven('Original transaction artifact history is incomplete')
            plans = [row for row in items if re.fullmatch('legend-release-transaction-plan-[a-f0-9]{64}', row.get('name', ''))]
            if not plans and _failed_transaction_preparation_without_writes(source, owner):
                continue
            if not plans:
                raise ReleaseOperationHistoryUnproven('Original transaction plan is missing; untouched target baseline cannot be replaced')
            for item in plans:
                original = _release_history_json(repository, prior['id'], item, 'release-transaction.json')
                if original.get('candidateRevision') != revision:
                    continue
                if original.get('producingRun') != prior['id'] or original.get('producingAttempt') != previous_attempt:
                    raise ReleaseOperationHistoryUnproven('Original transaction plan attempt is not bound')
                previous_targets = {row.get('app') for row in original.get('targets', [])}
                if not previous_targets or previous_targets.intersection(target_digests):
                    raise ReleaseOperationHistoryUnproven('Overlapping original transaction scope cannot be silently split or replaced')
    return None


def _artifact_rows(repository: str, name: str, token: str):
    encoded = urllib.parse.quote(name, safe="")
    payload = api_get(
        repository,
        f"actions/artifacts?name={encoded}&per_page=100",
        token,
    )
    return [
        row for row in payload.get("artifacts", [])
        if not row.get("expired")
    ]


def _run_artifact_names(repository: str, run_id: int, token: str):
    payload = api_get(
        repository,
        f"actions/runs/{run_id}/artifacts?per_page=100",
        token,
    )
    return {
        row.get("name")
        for row in payload.get("artifacts", [])
        if row.get("name") and not row.get("expired")
    }


def _download_run_artifact(repository: str, run_id: int, name: str, directory: Path):
    directory.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    if env.get("GITHUB_TOKEN") and not env.get("GH_TOKEN"):
        env["GH_TOKEN"] = env["GITHUB_TOKEN"]
    try:
        subprocess.run(
            [
                "gh", "run", "download", str(run_id),
                "--repo", repository,
                "--name", name,
                "--dir", str(directory),
            ],
            check=True,
            env=env,
        )
    except (subprocess.CalledProcessError, OSError) as exc:
        raise EvidenceLookupUnavailable("Artifact evidence read unavailable") from exc


def read_step5_results(path):
    """Reject incomplete, aborted or malformed TRX before using any child proof."""
    import xml.etree.ElementTree as ET
    root = ET.parse(path).getroot()
    rows = [node for node in root.iter() if node.tag.endswith("UnitTestResult")]
    counters = next((node for node in root.iter() if node.tag.endswith("Counters")), None)
    summary = next((node for node in root.iter() if node.tag.endswith("ResultSummary")), None)
    if summary is None or summary.get("outcome") not in {"Completed", "Passed", "Failed"}:
        raise ValueError("Incomplete test evidence: run did not complete")
    if not rows or counters is None or int(counters.get("total", "0")) != len(rows):
        raise ValueError("Incomplete test evidence: missing results or mismatched total")
    if any(int(counters.get(key, "0")) for key in ("error", "timeout", "aborted", "disconnected", "inProgress", "pending")):
        raise ValueError("Incomplete test evidence: execution errors remain")
    if any(node.get("outcome") not in {"Passed", "Failed", "NotExecuted"} or not node.get("testName") for node in rows):
        raise ValueError("Incomplete test evidence: unknown test outcome or identity")
    result = {}
    for node in rows:
        name = node.get("testName")
        outcome = node.get("outcome")
        previous = result.get(name)
        if previous is None:
            result[name] = outcome
        elif previous != outcome:
            # TRX may contain repeated rows for one logical test identity
            # (for example framework retry/reporting duplication). Reusing the
            # evidence is safe only when every terminal observation agrees.
            raise ValueError("Ambiguous duplicate test identity")
    return result


def merge_step5_class_results(prior_path, repair_path, classes, output_path):
    """Replace only complete affected class evidence; retain untouched rows."""
    import copy
    import xml.etree.ElementTree as ET
    classes = sorted(set(classes))
    if not classes:
        raise ValueError('Bounded repair requires affected classes')
    prior = read_step5_results(prior_path)
    repair = read_step5_results(repair_path)
    affected = lambda name: any(name.startswith(value + '.') for value in classes)
    if any(not affected(name) for name in repair):
        raise ValueError('Repair contains tests outside affected classes')
    for value in classes:
        if not any(name.startswith(value + '.') for name in repair):
            raise ValueError('Repair omitted affected class: ' + value)
    if any(affected(name) and name not in repair for name in prior):
        raise ValueError('Repair omitted prior tests from affected class')
    tree = ET.parse(prior_path)
    root = tree.getroot()
    results_node = next((node for node in root.iter() if node.tag.endswith('Results')), None)
    if results_node is None:
        raise ValueError('Prior TRX has no Results node')
    for node in list(results_node):
        if node.tag.endswith('UnitTestResult') and affected(node.get('testName', '')):
            results_node.remove(node)
    repair_root = ET.parse(repair_path).getroot()
    for node in repair_root.iter():
        if node.tag.endswith('UnitTestResult'):
            results_node.append(copy.deepcopy(node))
    rows = [node for node in root.iter() if node.tag.endswith('UnitTestResult')]
    counters = next(node for node in root.iter() if node.tag.endswith('Counters'))
    counters.set('total', str(len(rows)))
    counters.set('executed', str(sum(node.get('outcome') != 'NotExecuted' for node in rows)))
    for outcome in ('Passed', 'Failed', 'NotExecuted'):
        counters.set(outcome[0].lower() + outcome[1:], str(sum(node.get('outcome') == outcome for node in rows)))
    summary = next(node for node in root.iter() if node.tag.endswith('ResultSummary'))
    summary.set('outcome', 'Failed' if any(node.get('outcome') != 'Passed' for node in rows) else 'Passed')
    target = Path(output_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    tree.write(target, encoding='utf-8', xml_declaration=True)
    read_step5_results(target)


def cmd_step5_merge(args):
    merge_step5_class_results(args.prior, args.repair, args.classes.split(';'), args.output)
    Path(args.output + '.provenance.json').write_text(json.dumps({
        'materializationAuthoritySha256': _step5_merge_fingerprint(Path(__file__).read_text()),
        'sourceRunId': args.source_run, 'sourceArtifact': args.source_artifact,
        'sourceRevision': args.source_revision, 'approvedRevision': args.approved_revision,
        'replacedClasses': args.classes.split(';'),
        'priorSha256': hashlib.sha256(Path(args.prior).read_bytes()).hexdigest(),
        'repairSha256': hashlib.sha256(Path(args.repair).read_bytes()).hexdigest(),
        'effectiveSha256': hashlib.sha256(Path(args.output).read_bytes()).hexdigest(),
    }, sort_keys=True) + '\n')


@functools.lru_cache(maxsize=128)
def _step5_artifact_results(repository, run_id, artifact, kind, token):
    """Authenticate the retained child by its artifact bytes, not parent status.

    The canonical workflow uploads this artifact only after the child suite has
    completed. Reuse additionally parses the TRX and rejects incomplete,
    conflicting or malformed evidence, so a later parent failure cannot erase
    the successful child and no jobs-API self-read is required.
    """
    import tempfile
    with tempfile.TemporaryDirectory(prefix="step5-child-") as temporary:
        directory = Path(temporary)
        _download_run_artifact(repository, run_id, artifact, directory)
        return read_step5_results(directory / (kind + ".trx"))


def _step5_artifact_complete(repository, run_id, artifact, kind, token):
    return bool(_step5_artifact_results(repository, run_id, artifact, kind, token))


def _step5_prior_candidate_evidence(
    repository: str,
    current_run_id: int,
    head_branch: str,
    token: str,
    current_sha: str,
):
    """Find the newest compatible child artifact across trusted PR branches.

    Parent failure never erases a completed candidate child. Compatibility is
    checked before selection, so a newer incompatible run cannot hide an older
    equivalent producer.
    """
    workflow_name = "step5-isolated-conversion-mapping-validation.yml"
    workflow_path = WORKFLOW_PATHS[workflow_name]
    workflow = urllib.parse.quote(workflow_name, safe="")
    payload = api_get(repository,
        f"actions/workflows/{workflow}/runs?event=pull_request&status=completed&per_page=100", token)
    runs = sorted(payload.get("workflow_runs", []),
        key=lambda row: (row.get("updated_at") or row.get("created_at", ""), int(row.get("id", 0))),
        reverse=True)
    for run in runs:
        run_id = int(run.get("id") or 0)
        if not run_id or run_id == current_run_id:
            continue
        try:
            head_sha = run["head_sha"]
            if not _trusted_lineage_run(repository, run, workflow_path, current_sha):
                if not _trusted_pr_run(repository, run, workflow_path, token):
                    continue
            impact = step5_dependency_change(head_sha, current_sha)
            if impact is None or not _step5_candidate_producer_compatible(head_sha, workflow_path):
                continue
            artifact = f"step5-candidate-{head_sha}"
            if artifact not in _run_artifact_names(repository, run_id, token):
                continue
            if not _step5_artifact_complete(repository, run_id, artifact, "candidate", token):
                continue
            rows = _step5_artifact_results(repository, run_id, artifact, "candidate", token)
            return {"runId": run_id, "headSha": head_sha, "artifact": artifact,
                    "testNames": tuple(rows)}
        except EvidenceLookupUnavailable:
            raise
        except Exception:
            continue
    return None


def _test_file_dependency_patterns(source):
    """Conservatively bind repository files actually read by source-contract tests.

    Compile proof never consumes these runtime dependencies. For executing tests,
    literal files/directories and copied AppContext fixtures are exact inputs.
    Computed single-file paths are resolved by the existing literal-token and
    copied-alias pass below instead of poisoning the gate with the whole repo.
    Computed directory enumeration remains fail-closed because it can observe an
    arbitrary repository subtree whose members are not statically enumerable.
    """
    calls = re.compile(r"\b(Directory\.(?:Get|Enumerate)(?:Files|Directories|FileSystemEntries)|File\.(?:Read\w*|Open\w*|Exists)|(?:new\s+)?(?:StreamReader|FileStream))\s*\(")
    patterns = set()
    for match in calls.finditer(source):
        tail = source[match.end():]
        literal = re.match(r'\s*(@?)"((?:[^"\\]|\\.)*)"\s*(?=[,)])', tail)
        directory = match.group(1).startswith("Directory.")
        if literal:
            path = literal.group(2).replace("\\\\", "/").replace("\\", "/").strip("/")
            if not path or path == "." or ".." in path.split("/"):
                patterns.add("**")
            else:
                patterns.add(path.rstrip("/") + "/**" if directory else path)
                # A relative runtime file can be a csproj Link alias; retain all
                # matching source basenames as well as exact repository paths.
                if not directory:
                    patterns.add("**/" + Path(path).name)
            continue
        copied = re.match(r'\s*Path\.Combine\(\s*AppContext\.BaseDirectory\s*,\s*"([^"\r\n]+)"\s*\)', tail)
        if copied and not directory:
            patterns.add("**/" + Path(copied.group(1)).name)
            continue
        if directory:
            patterns.add("**")
    return tuple(sorted(patterns))


def _step5_source_classes(source):
    namespace = re.search(r"\bnamespace\s+([\w.]+)\s*[;{]", source)
    classes = re.findall(r"\b(?:public|internal)\s+(?:(?:sealed|partial|abstract|static)\s+)*class\s+(\w+)", source)
    if not namespace or not classes:
        return set()
    return {namespace.group(1) + "." + name for name in classes}


_READONLY_FIXTURE_HELPER = re.compile(
    r'private\s+static\s+string\s+\w+\s*\(\s*\)\s*=>\s*'
    r'File\.ReadAllText\(Path\.Combine\(AppContext\.BaseDirectory,\s*"[^"\r\n]+"\)\);')


def _step5_csharp_structure(source):
    """Mask comments and literals while preserving C# declaration geometry."""
    chars = list(source)
    length = len(source)

    def mask(begin, finish):
        for index in range(begin, min(finish, length)):
            if chars[index] not in "\r\n":
                chars[index] = " "

    i = 0
    while i < length:
        if source.startswith("//", i):
            finish = source.find("\n", i + 2)
            finish = length if finish < 0 else finish
            mask(i, finish)
            i = finish
            continue
        if source.startswith("/*", i):
            finish = source.find("*/", i + 2)
            finish = length if finish < 0 else finish + 2
            mask(i, finish)
            i = finish
            continue
        if source[i] == '"':
            quote_count = 1
            while i + quote_count < length and source[i + quote_count] == '"':
                quote_count += 1
            if quote_count >= 3:
                delimiter = '"' * quote_count
                finish = source.find(delimiter, i + quote_count)
                finish = length if finish < 0 else finish + quote_count
                mask(i, finish)
                i = finish
                continue

            verbatim = i > 0 and source[i - 1] == "@"
            finish = i + 1
            while finish < length:
                if verbatim and source.startswith('""', finish):
                    finish += 2
                    continue
                if source[finish] == '"' and (verbatim or source[finish - 1] != "\\"):
                    finish += 1
                    break
                if not verbatim and source[finish] == "\\":
                    finish += 2
                else:
                    finish += 1
            mask(i, finish)
            i = finish
            continue
        if source[i] == "'":
            finish = i + 1
            while finish < length:
                if source[finish] == "\\":
                    finish += 2
                    continue
                if source[finish] == "'":
                    finish += 1
                    break
                finish += 1
            mask(i, finish)
            i = finish
            continue
        i += 1
    return "".join(chars)


def _step5_isolated_test_source(source):
    """Admit only standalone test classes, never arbitrary C# dependency guesses.

    Comments and literals are masked before structural analysis, so prose cannot
    impersonate exported/static declarations while compact one-line C# remains
    valid input. Exported helpers, inherited/partial fixtures, extension types,
    static state and shared registrations still require full-suite proof.
    """
    source = _READONLY_FIXTURE_HELPER.sub("", source)
    structure = _step5_csharp_structure(source)
    if len(re.findall(r"\bclass\s+\w+", structure)) != 1:
        return False
    if re.search(
        r"\[Collection(?:\(|Attribute)|\b(?:partial|abstract|static)\s+class|"
        r"\bclass\s+\w+\s*[:<]|\b(?:record|struct|interface|enum|delegate)\s+\w+|"
        r"\[\s*(?:assembly|module)\s*:|\bglobal\s+using|ModuleInitializer|"
        r"CollectionDefinition|ICollectionFixture",
        structure,
    ):
        return False

    # Reject every static construct except a private static method declaration.
    for marker in re.finditer(r"\bstatic\b", structure):
        tail = structure[marker.start():]
        line_start = structure.rfind("\n", 0, marker.start()) + 1
        prefix = structure[line_start:marker.start()]
        private_method = (
            re.search(r"\bprivate\s*$", prefix) is not None
            and re.match(
                r"static\s+(?:async\s+)?[\w.<>,?\[\]]+\s+\w+\s*(?:<[^>]+>)?\s*\(",
                tail,
            ) is not None
        )
        if not private_method:
            return False

    # Every exported member must be the test class or an attributed test method.
    for visibility in re.finditer(r"\b(?:public|internal|protected)\b", structure):
        tail = structure[visibility.start():]
        if re.match(r"(?:public|internal)\s+(?:sealed\s+)?class\s+", tail):
            continue
        prefix = structure[:visibility.start()]
        attributes = re.search(r"((?:\[[^\]]+\]\s*)+)$", prefix)
        if not attributes or not re.search(r"\[(?:Fact|Theory)(?:\]|\()", attributes.group(1)):
            return False
        if not re.match(
            r"public\s+(?:async\s+)?(?:void|Task(?:<[^>]+>)?|ValueTask(?:<[^>]+>)?)\s+\w+\s*\(",
            tail,
        ):
            return False
    return bool(re.search(r"\[(?:Fact|Theory)(?:\]|\()", structure))

def _step5_extension_method_names(source):
    # Ordinary calls follow their declaring type through the source closure.
    # Only extension syntax can omit that type at its call site. A shared name
    # on an unrelated override (e.g. SendAsync) is not a dependency edge.
    return set(re.findall(
        r"\b(?:public|internal)\s+static\s+(?:async\s+)?[\w.<>,?\[\]]+\s+"
        r"(\w+)\s*(?:<[^>]+>)?\s*\(\s*this\s+", source))


def step5_dependency_change(prior_sha, current_sha, *, stop_on_change=False):
    """Return bounded invalidated classes, or None when suite proof is required.

    Project-copied data is a real test dependency, even when its owning release
    workflow is neutral to application binaries. Resolve consumers from source,
    including partial files and references between fixture classes. Unknown
    source/build inputs and unbounded helpers deliberately require full proof.
    """
    import posixpath
    import xml.etree.ElementTree as ET
    workflow = "step5-isolated-conversion-mapping-validation.yml"
    config = WORKFLOWS[workflow]
    changed = git_changed(prior_sha, current_sha)

    # Fast path for exact reuse/control-plane continuation. Do not archive and
    # analyze the entire AgentPortal.Tests graph when no Step 5 test/application
    # input changed. Workflow execution compatibility is authenticated separately
    # by _step5_jobs_unchanged before child evidence is reused.
    if not changed:
        return []
    # Narrow control-only fast path. These files govern Step 5 planning and
    # comparison, but are not runtime inputs to the AgentPortal.Tests suite.
    # Candidate/baseline workflow execution compatibility is authenticated
    # separately by _step5_jobs_unchanged. Other nominally "control" files
    # (for example the direct-release workflow/request) may be copied/read by
    # regression tests and therefore MUST continue through consumer discovery.
    fast_neutral = {
        WORKFLOW_PATHS[workflow],
        "scripts/validation-resume.py",
        "scripts/test-validation-resume.py",
        "scripts/test-release-policy.py",
    }
    if all(path in fast_neutral for path in changed):
        return []

    # One archive read avoids hundreds of subprocesses per historical producer.
    import io
    import tarfile
    archive = subprocess.run(["git", "archive", current_sha, "AgentPortal.Tests"],
        check=True, capture_output=True).stdout
    with tarfile.open(fileobj=io.BytesIO(archive)) as tree:
        contents = {entry.name: tree.extractfile(entry).read().decode("utf-8-sig")
                    for entry in tree.getmembers() if entry.isfile()
                    and (entry.name.endswith(".cs") or entry.name.endswith(".csproj"))}
    sources = {path: source for path, source in contents.items() if path.endswith(".cs")}
    classes = {path: _step5_source_classes(source) for path, source in sources.items()}
    project = ET.fromstring(contents["AgentPortal.Tests/AgentPortal.Tests.csproj"])
    copied = {}
    for node in project.iter():
        include = node.get("Include", "").replace("\\", "/")
        if not include or not (node.get("CopyToOutputDirectory") or node.find("CopyToOutputDirectory") is not None):
            continue
        path = posixpath.normpath("AgentPortal.Tests/" + include)
        link = node.get("Link") or node.findtext("Link") or posixpath.basename(path)
        copied[path] = link
    dynamic_inputs = {file: _test_file_dependency_patterns(source) for file, source in sources.items()}
    affected = set()
    for path in changed:
        # These four files govern Step 5 planning/comparison only. They remain
        # neutral even when mixed with a real test correction; otherwise one
        # bounded test edit plus planner maintenance falsely escalates to a full
        # AgentPortal suite. Other control files still flow through consumer
        # discovery because tests may copy/read them directly.
        if path in fast_neutral:
            continue
        if path in sources:
            old = git_show_file(prior_sha, path)
            own = classes[path]
            if (_READONLY_FIXTURE_HELPER.findall(old) != _READONLY_FIXTURE_HELPER.findall(sources[path])
                    or not own or own != _step5_source_classes(old)
                    or not _step5_isolated_test_source(old)
                    or not _step5_isolated_test_source(sources[path])):
                return None
            # A class referenced elsewhere is a shared fixture, not isolated.
            if any(re.search(r"\b" + re.escape(name.rsplit(".", 1)[-1]) + r"\b", source)
                   for file, source in sources.items() if file != path for name in own):
                return None
            affected.update(own)
        elif gate_matches(path, config["gates"]["candidate-full"]):
            return None
        elif path not in copied and not (matches(path, config["neutral"])
                or gate_matches(path, config["gates"]["comparison"])
                or release_control_only_path(path)):
            return None
        # Tests also read repository files directly without csproj copying.
        # Both forms share the same source-derived consumer closure.
        link = copied.get(path, path)
        consumers = [file for file, source in sources.items()
                     if link in source or posixpath.basename(link) in source
                     or matches(path, dynamic_inputs[file])]
        if path in copied and not consumers:
            return None
        if any(not classes[file] for file in consumers):
            return None
        for file in consumers:
            affected.update(classes[file])
        # Baseline equivalence needs only a yes/no answer. Once any consumer is
        # affected, no later path or transitive closure can restore equivalence.
        if stop_on_change and affected:
            return sorted(affected)
    # Follow shared helpers' exported method names too: extension-method users
    # need not spell the declaring static type at the call site.
    test_classes = set().union(*(classes[file] for file, source in sources.items()
        if re.search(r"\[(?:Fact|Theory)(?:\s|\]|\()", source)))
    while True:
        expanded = set(affected)
        referenced = {name.rsplit(".", 1)[-1] for name in affected}
        for file, source in sources.items():
            if classes[file] & affected and not classes[file] & test_classes:
                referenced.update(_step5_extension_method_names(source))
        for file, source in sources.items():
            if any(re.search(r"\b" + re.escape(name) + r"\b", source) for name in referenced):
                if not classes[file]:
                    return None
                expanded.update(classes[file])
        if expanded == affected:
            bounded = affected & test_classes
            return sorted(bounded) if bounded or not affected else None
        affected = expanded


def _step5_discovered_repair_classes(classes, test_names, changed_paths, source_revision=None):
    """Use complete prior discovery to distinguish tests from co-located helpers.

    Prior xUnit discovery is authoritative for unchanged source files, so helper
    classes pulled in through a changed workflow/config consumer can be dropped.
    Fail closed only when an undiscovered proposed class is declared by a C# file
    that actually changed, because that edit could have introduced new tests.
    """
    discovered = [
        name for name in classes
        if any(test.startswith(name + ".") for test in test_names)
    ]
    undiscovered = set(classes) - set(discovered)
    if not undiscovered:
        return discovered

    changed_declared = set()
    for path in changed_paths:
        if not path.endswith((".cs", ".csproj", ".props", ".targets")):
            continue
        if not path.endswith(".cs"):
            # Project/build graph edits can alter discovery globally.
            return None
        source_path = Path(path)
        if source_revision is not None:
            try:
                source = git_show_file(source_revision, path)
            except Exception:
                return None
            changed_declared.update(_step5_source_classes(source))
            continue
        if not source_path.is_file():
            # Added/deleted/renamed C# source cannot be narrowed from prior
            # discovery alone.
            return None
        changed_declared.update(_step5_source_classes(source_path.read_text()))

    if undiscovered & changed_declared:
        return None
    return discovered


def _step5_cached_decision(current_sha: str, base_sha: str, resume_cache: str):
    """Resolve PR-local child evidence through the same canonical Step 5 authority."""
    capsule = Path(resume_cache)
    required = (
        capsule / "metadata.json",
        capsule / "candidate.trx",
        capsule / "baseline.trx",
    )
    if not all(path.is_file() for path in required):
        return None, "resume_cache_incomplete"

    try:
        metadata = json.loads((capsule / "metadata.json").read_text())
    except Exception:
        return None, "resume_cache_metadata_invalid"

    prior = metadata.get("headSha") or ""
    prior_base = metadata.get("baseSha") or ""
    run_id = metadata.get("runId")
    if (
        not re.fullmatch(r"[0-9a-f]{40}", prior)
        or not re.fullmatch(r"[0-9a-f]{40}", prior_base)
        or type(run_id) is not int
        or run_id < 1
    ):
        return None, "resume_cache_identity_invalid"
    if subprocess.run(
        ["git", "merge-base", "--is-ancestor", prior, current_sha],
        capture_output=True,
    ).returncode:
        return None, "resume_cache_not_ancestor"

    workflow_path = WORKFLOW_PATHS["step5-isolated-conversion-mapping-validation.yml"]
    if not _step5_jobs_unchanged(prior, workflow_path):
        return None, "resume_cache_execution_contract_changed"
    if not _step5_baseline_inputs_equivalent(prior_base, base_sha):
        return None, "resume_cache_baseline_inputs_changed"

    try:
        candidate = read_step5_results(capsule / "candidate.trx")
        read_step5_results(capsule / "baseline.trx")
    except Exception:
        return None, "resume_cache_test_evidence_invalid"

    changed = git_changed(prior, current_sha)
    classes = step5_dependency_change(prior, current_sha)
    if classes is None:
        return None, "resume_cache_unbounded_dependency_change"
    classes = _step5_discovered_repair_classes(classes, tuple(candidate), changed)
    if classes is None:
        return None, "resume_cache_test_discovery_changed"

    return {
        "schemaVersion": 4,
        "mode": "repair" if classes else "reuse",
        "priorRunId": run_id,
        "priorHeadSha": prior,
        "baselineEvidenceRunId": run_id,
        "baselineEvidenceArtifact": None,
        "repairClasses": classes,
        "repairFilter": "|".join(f"FullyQualifiedName~{name}" for name in classes) or None,
        "resumeCache": True,
        "parentSuccessReuse": False,
        "reason": "pr_local_cached_child_evidence",
    }, None


def _step5_graphql_parent_success_decision(
    repository: str,
    current_sha: str,
    base_sha: str,
    token: str,
):
    """Cheap control-only success proof using one check-rollup request.

    This is an optimization inside the canonical planner, never a second workflow
    decision implementation. It may prove exact reuse only; repair/full decisions
    continue through retained child artifacts below.
    """
    owner, name = repository.split("/", 1)
    query = r"""
    query($owner:String!,$name:String!,$oid:GitObjectID!){
      repository(owner:$owner,name:$name){
        object(oid:$oid){
          ... on Commit {
            history(first:30) {
              nodes {
                oid
                statusCheckRollup {
                  contexts(first:100) {
                    nodes {
                      __typename
                      ... on CheckRun { name conclusion detailsUrl }
                    }
                  }
                }
              }
            }
          }
        }
      }
    }"""
    payload = json.dumps({
        "query": query,
        "variables": {"owner": owner, "name": name, "oid": current_sha},
    }).encode()
    request = urllib.request.Request(
        "https://api.github.com/graphql",
        data=payload,
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "User-Agent": "legend-step5-parent-proof/1.0",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            body = json.load(response)
    except Exception:
        return None

    history = (((((body.get("data") or {}).get("repository") or {}).get("object") or {})
                .get("history") or {}).get("nodes", []))
    workflow_path = WORKFLOW_PATHS["step5-isolated-conversion-mapping-validation.yml"]
    for row in history:
        prior = row.get("oid") or ""
        if prior == current_sha or not re.fullmatch(r"[0-9a-f]{40}", prior):
            continue
        nodes = ((row.get("statusCheckRollup") or {}).get("contexts") or {}).get("nodes", [])
        success = next((
            check for check in nodes
            if check.get("__typename") == "CheckRun"
            and check.get("name") == "step5-validation"
            and check.get("conclusion") == "SUCCESS"
        ), None)
        if not success:
            continue
        if subprocess.run(
            ["git", "merge-base", "--is-ancestor", base_sha, prior],
            capture_output=True,
        ).returncode:
            continue
        try:
            if not _step5_jobs_unchanged(prior, workflow_path):
                continue
            if step5_dependency_change(prior, current_sha) != []:
                continue
        except Exception:
            continue
        run_id = None
        match = re.search(r"/actions/runs/(\d+)", success.get("detailsUrl") or "")
        if match:
            run_id = int(match.group(1))
        return {
            "schemaVersion": 4,
            "mode": "reuse",
            "priorRunId": run_id,
            "priorHeadSha": prior,
            "baselineEvidenceRunId": None,
            "baselineEvidenceArtifact": None,
            "repairClasses": [],
            "repairFilter": None,
            "resumeCache": False,
            "parentSuccessReuse": True,
            "reason": "graphql_parent_success_dependency_equivalent",
        }
    return None


def compute_step5_decision(
    repository: str,
    current_sha: str,
    base_sha: str,
    current_run_id: int,
    head_branch: str,
    resume_cache: str | None = None,
):
    """Choose only Step 5's cross-run comparison mode.

    Candidate full-suite evidence and approved-baseline evidence are independent
    canonical artifacts. A final-comparison repair therefore never requires both
    artifacts to have originated from the same historical run. Bounded dependency
    changes replace only affected test-class rows; all other child rows survive
    comparison failures. A missing baseline is scheduled independently.
    """
    decision = {
        "schemaVersion": 3,
        "mode": "full",
        "priorRunId": None,
        "priorHeadSha": None,
        "baselineEvidenceRunId": None,
        "baselineEvidenceArtifact": None,
        "repairClasses": [],
        "repairFilter": None,
    }
    cache_fallback_reason = None
    if resume_cache:
        cached, cache_fallback_reason = _step5_cached_decision(
            current_sha,
            base_sha,
            resume_cache,
        )
        if cached:
            return cached
        decision["cacheFallbackReason"] = cache_fallback_reason

    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        decision["reason"] = "github_token_unavailable"
        return decision

    workflow_name = "step5-isolated-conversion-mapping-validation.yml"
    workflow_path = WORKFLOW_PATHS[workflow_name]

    parent_success = _step5_graphql_parent_success_decision(
        repository,
        current_sha,
        base_sha,
        token,
    )
    if parent_success:
        if cache_fallback_reason:
            parent_success["cacheFallbackReason"] = cache_fallback_reason
        return parent_success

    candidate_evidence = _step5_prior_candidate_evidence(
        repository,
        current_run_id,
        head_branch,
        token,
        current_sha,
    )
    if not candidate_evidence:
        decision["reason"] = "no_reusable_candidate_evidence"
        return decision

    prior_run_id = int(candidate_evidence["runId"])
    prior_head_sha = candidate_evidence["headSha"]
    candidate_name = candidate_evidence["artifact"]

    decision.update({
        "priorRunId": prior_run_id,
        "priorHeadSha": prior_head_sha,
    })

    # Reject incompatible candidate job definitions before any expensive
    # historical baseline scan. The baseline is an independent child and will
    # be produced by the baseline-evidence job when full proof is required.
    if not _step5_candidate_producer_compatible(prior_head_sha, workflow_path):
        decision["reason"] = "candidate_or_baseline_job_changed"
        return decision

    baseline_evidence = compute_step5_baseline_evidence(repository, base_sha)
    # Candidate proof remains valid even when the independent baseline needs
    # fresh execution. The workflow produces that missing child in this run.
    if baseline_evidence.get("repairClasses"):
        decision["baselineRepair"] = baseline_evidence
    if not baseline_evidence.get("reusable"):
        baseline_evidence = {
            "evidenceRunId": current_run_id,
            "evidenceArtifact": f"step5-baseline-{base_sha}",
        }

    baseline_run_id = int(baseline_evidence["evidenceRunId"])
    baseline_name = baseline_evidence["evidenceArtifact"]

    decision.update({
        "baselineEvidenceRunId": baseline_run_id,
        "baselineEvidenceArtifact": baseline_name,
    })
    classes = step5_dependency_change(prior_head_sha, current_sha)
    if classes is None:
        decision["reason"] = "changed_or_unproven_suite_dependencies"
        return decision
    if "testNames" in candidate_evidence:
        classes = _step5_discovered_repair_classes(
            classes, candidate_evidence["testNames"], git_changed(prior_head_sha, current_sha))
        if classes is None:
            decision["reason"] = "changed_test_discovery_requires_full_proof"
            return decision
    decision.update({
        "mode": "repair" if classes else "reuse",
        "repairClasses": classes,
        "repairFilter": "|".join(f"FullyQualifiedName~{name}" for name in classes) or None,
        "reason": "dependency_equivalent_child_evidence" if not classes else "replace_only_dependency_invalidated_classes",
    })
    return decision


def cmd_step5_decision(args):
    try:
        decision = compute_step5_decision(
            args.repository,
            args.current_sha,
            args.base_sha,
            args.current_run_id,
            args.head_branch,
            getattr(args, "resume_cache", None),
        )
    except EvidenceLookupUnavailable as exc:
        decision = {
            "schemaVersion": 3,
            "mode": "full",
            "priorRunId": None,
            "priorHeadSha": None,
            "baselineEvidenceRunId": None,
            "baselineEvidenceArtifact": None,
            "repairClasses": [],
            "repairFilter": None,
            "reason": "historical_evidence_unavailable_run_full_step5",
            "evidenceFallback": {
                "error": type(exc).__name__,
                "httpStatus": exc.code,
                "endpoint": exc.endpoint,
            },
        }
    except Exception as exc:
        _stop_unresolved_planning(args, exc)

    Path(args.output).write_text(json.dumps(decision, indent=2, sort_keys=True) + "\n")
    print(json.dumps(decision, indent=2, sort_keys=True))


def _step5_baseline_inputs_equivalent(prior_base_sha: str, current_base_sha: str) -> bool:
    """Compare only inputs that can change the full Step 5 baseline result."""
    if prior_base_sha == current_base_sha:
        return True
    return step5_dependency_change(prior_base_sha, current_base_sha, stop_on_change=True) == []


def compute_step5_baseline_evidence(repository: str, base_sha: str):
    result = {
        "schemaVersion": 3,
        "approvedBaseSha": base_sha,
        "reusable": False,
        "evidenceRunId": None,
        "evidenceArtifact": None,
        "evidenceBaseSha": None,
    }
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        result["reason"] = "github_token_unavailable"
        return result

    workflow_name = "step5-isolated-conversion-mapping-validation.yml"
    workflow_path = WORKFLOW_PATHS[workflow_name]

    bounded = None

    def accept(run, artifact_name, evidence_base_sha):
        nonlocal bounded
        run_id = int(run.get("id") or 0)
        run_head = run.get("head_sha") or ""
        if not run_id or not re.fullmatch(r"[0-9a-f]{40}", evidence_base_sha):
            return False
        if not _trusted_pr_run(repository, run, workflow_path, token):
            return False
        kind = "candidate" if artifact_name.startswith("step5-candidate-") else "baseline"
        if kind == "candidate":
            if evidence_base_sha != run_head or not _step5_candidate_producer_compatible(run_head, workflow_path):
                return False
        elif not _step5_baseline_producer_compatible(run_head, workflow_path):
            return False
        classes = step5_dependency_change(evidence_base_sha, base_sha)
        if classes is None:
            return False
        rows = _step5_artifact_results(repository, run_id, artifact_name, kind, token)
        if classes:
            classes = _step5_discovered_repair_classes(classes, tuple(rows), git_changed(evidence_base_sha, base_sha), source_revision=base_sha)
        if classes is None:
            return False
        evidence = {
            "reusable": not bool(classes),
            "evidenceRunId": run_id,
            "evidenceArtifact": artifact_name,
            "evidenceBaseSha": evidence_base_sha,
            "repairClasses": classes,
            "repairFilter": "|".join(f"FullyQualifiedName~{name}" for name in classes),
            "reason": "bounded_approved_baseline_repair" if classes else (
                "exact_approved_baseline_evidence" if evidence_base_sha == base_sha else "content_identical_step5_inputs"),
        }
        if classes:
            if bounded is None:
                bounded = evidence
            return False
        result.update(evidence)
        return True

    # Fast path: an artifact already keyed to the exact approved base.
    exact_name = f"step5-baseline-{base_sha}"
    for artifact in _artifact_rows(repository, exact_name, token):
        run_id = int((artifact.get("workflow_run") or {}).get("id") or 0)
        if not run_id:
            continue
        try:
            run = api_get(repository, f"actions/runs/{run_id}", token)
            if accept(run, exact_name, base_sha):
                return result
        except EvidenceLookupUnavailable:
            raise
        except Exception:
            continue

    # Control-only commits must not invalidate a full baseline suite. Search recent
    # completed Step 5 runs for a durable baseline artifact whose declared test
    # inputs are tree-equivalent to the current approved base.
    workflow = urllib.parse.quote(workflow_name, safe="")
    payload = api_get(
        repository,
        f"actions/workflows/{workflow}/runs?event=pull_request&status=completed&per_page=100",
        token,
    )
    runs = sorted(
        payload.get("workflow_runs", []),
        key=lambda row: (row.get("updated_at") or row.get("created_at", ""), int(row.get("id", 0))),
        reverse=True,
    )
    for run in runs:
        run_id = int(run.get("id") or 0)
        if not run_id:
            continue
        try:
            names = _run_artifact_names(repository, run_id, token)
        except EvidenceLookupUnavailable:
            raise
        except Exception:
            continue
        for artifact_name in sorted(names):
            prefix = next((value for value in ("step5-baseline-", "step5-candidate-") if artifact_name.startswith(value)), None)
            if prefix is None:
                continue
            evidence_base_sha = artifact_name[len(prefix):]
            if len(evidence_base_sha) != 40 or any(ch not in "0123456789abcdef" for ch in evidence_base_sha):
                continue
            try:
                if accept(run, artifact_name, evidence_base_sha):
                    return result
            except EvidenceLookupUnavailable:
                raise
            except Exception:
                continue

    result["reason"] = "no_content_identical_baseline_artifact"
    if bounded:
        result.update(bounded)
    return result


def cmd_step5_baseline(args):
    try:
        result = compute_step5_baseline_evidence(args.repository, args.base_sha)
    except EvidenceLookupUnavailable as exc:
        result = {
            "schemaVersion": 3,
            "approvedBaseSha": args.base_sha,
            "reusable": False,
            "evidenceRunId": None,
            "evidenceArtifact": None,
            "evidenceBaseSha": None,
            "reason": "historical_evidence_unavailable_run_fresh_baseline",
            "evidenceFallback": {
                "error": type(exc).__name__,
                "httpStatus": exc.code,
                "endpoint": exc.endpoint,
            },
        }
    except Exception as exc:
        _stop_unresolved_planning(args, exc)

    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")
    print(json.dumps(result, indent=2, sort_keys=True))


def cmd_job_unchanged(args):
    prior = git_show_file(args.prior_sha, args.workflow_path)
    current = Path(args.workflow_path).read_text()
    prior_jobs = _job_blocks(prior)
    current_jobs = _job_blocks(current)
    missing = [name for name in args.job if name not in prior_jobs or name not in current_jobs]
    if missing:
        print("Missing workflow jobs: " + ", ".join(missing), file=sys.stderr)
        raise SystemExit(1)
    changed = [name for name in args.job if prior_jobs[name] != current_jobs[name]]
    if changed:
        print("Changed workflow jobs: " + ", ".join(changed), file=sys.stderr)
        raise SystemExit(1)
    print("Preserved workflow job definitions: " + ", ".join(args.job))


def cmd_verify_release_coverage(args):
    text = Path(args.workflow_path).read_text()
    verify_release_policy_coverage(args.workflow, text)
    print(f"Every named release step is classified: {args.workflow}")


def build_parser():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)

    approved_preflight = sub.add_parser("approved-head-preflight")
    approved_preflight.add_argument("--repository", required=True)
    approved_preflight.add_argument("--current-sha", required=True)
    approved_preflight.add_argument("--event", required=True)
    approved_preflight.add_argument("--output")
    approved_preflight.set_defaults(func=cmd_approved_head_preflight)

    plan = sub.add_parser("plan")
    plan.add_argument("--workflow", required=True)
    plan.add_argument("--current-sha", required=True)
    plan.add_argument("--current-run-id", required=True, type=int)
    plan.add_argument("--run-attempt", required=True, type=int)
    plan.add_argument("--head-branch", required=True)
    plan.add_argument("--event", required=True)
    plan.add_argument("--repository", required=True)
    plan.add_argument("--output", required=True)
    plan.add_argument("--resume-cache")
    plan.set_defaults(func=cmd_plan)

    cache_success = sub.add_parser("cache-success")
    cache_success.add_argument("--workflow", required=True)
    cache_success.add_argument("--head-sha", required=True)
    cache_success.add_argument("--run-id", required=True, type=int)
    cache_success.add_argument("--run-attempt", required=True, type=int)
    cache_success.add_argument("--output", required=True)
    cache_success.set_defaults(func=cmd_cache_success)

    preserved = sub.add_parser("preserved")
    preserved.add_argument("--plan", required=True)
    preserved.add_argument("--gate", required=True)
    preserved.set_defaults(func=cmd_preserved)

    gate_identity = sub.add_parser("gate-identity")
    gate_identity.add_argument("--workflow", required=True)
    gate_identity.add_argument("--revision", required=True)
    gate_identity.add_argument("--gate", required=True)
    gate_identity.add_argument("--github-output")
    gate_identity.set_defaults(func=cmd_gate_identity)

    live_state = sub.add_parser("live-state")
    live_state.add_argument("--revision", required=True)
    live_state.add_argument("--selected-targets", required=True)
    live_state.add_argument("--output", required=True)
    live_state.add_argument("--github-output")
    live_state.set_defaults(func=cmd_live_state)

    verify_live = sub.add_parser("verify-live")
    verify_live.add_argument("--revision", required=True)
    verify_live.add_argument("--selected-targets", required=True)
    verify_live.add_argument("--output", required=True)
    verify_live.add_argument("--timeout-seconds", type=int, default=480)
    verify_live.add_argument("--poll-seconds", type=int, default=5)
    verify_live.set_defaults(func=cmd_verify_live)

    lifecycle_evidence = sub.add_parser("lifecycle-evidence")
    lifecycle_evidence.add_argument("--repository", required=True)
    lifecycle_evidence.add_argument("--output", required=True)
    lifecycle_evidence.set_defaults(func=cmd_lifecycle_evidence)

    package_canary = sub.add_parser("package-canary-plan")
    package_canary.add_argument("--repository", required=True)
    package_canary.add_argument("--current-sha", required=True)
    package_canary.add_argument("--base-sha", required=True)
    package_canary.add_argument("--current-run-id", required=True, type=int)
    package_canary.add_argument("--head-branch", required=True)
    package_canary.add_argument("--output", required=True)
    package_canary.set_defaults(func=cmd_package_canary_plan)

    package_backfill = sub.add_parser("package-backfill-plan")
    package_backfill.add_argument("--repository", required=True)
    package_backfill.add_argument("--revision", required=True)
    package_backfill.add_argument("--current-sha", required=True)
    package_backfill.add_argument("--output", required=True)
    package_backfill.set_defaults(func=cmd_package_backfill_plan)

    validated_package = sub.add_parser("validated-package")
    validated_package.add_argument("--repository", required=True)
    validated_package.add_argument("--revision", required=True)
    validated_package.add_argument("--package-identity", required=True)
    validated_package.add_argument("--output", required=True)
    validated_package.set_defaults(func=cmd_validated_package)

    rollback_evidence = sub.add_parser("rollback-evidence")
    rollback_evidence.add_argument("--repository", required=True)
    rollback_evidence.add_argument("--revision", required=True)
    rollback_evidence.add_argument("--app", required=True)
    rollback_evidence.add_argument("--output", required=True)
    rollback_evidence.set_defaults(func=cmd_rollback_evidence)

    step5_decision = sub.add_parser("step5-decision")
    step5_decision.add_argument("--current-sha", required=True)
    step5_decision.add_argument("--base-sha", required=True)
    step5_decision.add_argument("--current-run-id", required=True, type=int)
    step5_decision.add_argument("--head-branch", required=True)
    step5_decision.add_argument("--repository", required=True)
    step5_decision.add_argument("--resume-cache")
    step5_decision.add_argument("--output", required=True)
    step5_decision.set_defaults(func=cmd_step5_decision)

    step5_baseline = sub.add_parser("step5-baseline")
    step5_baseline.add_argument("--base-sha", required=True)
    step5_baseline.add_argument("--repository", required=True)
    step5_baseline.add_argument("--output", required=True)
    step5_baseline.set_defaults(func=cmd_step5_baseline)

    merge = sub.add_parser('step5-merge')
    for flag in ('prior', 'repair', 'classes', 'output', 'source-run', 'source-artifact', 'source-revision', 'approved-revision'):
        merge.add_argument('--' + flag, required=True)
    merge.set_defaults(func=cmd_step5_merge)

    job_unchanged = sub.add_parser("job-unchanged")
    job_unchanged.add_argument("--workflow-path", required=True)
    job_unchanged.add_argument("--prior-sha", required=True)
    job_unchanged.add_argument("--job", action="append", required=True)
    job_unchanged.set_defaults(func=cmd_job_unchanged)

    record = sub.add_parser("record-evidence")
    record.add_argument("--plan", required=True)
    record.add_argument("--output", required=True)
    record.add_argument("--repository", required=True)
    record.add_argument("--run-id", required=True, type=int)
    record.add_argument("--cache-output")
    record.set_defaults(func=cmd_record_evidence)

    coverage = sub.add_parser("verify-release-coverage")
    coverage.add_argument("--workflow", required=True)
    coverage.add_argument("--workflow-path", required=True)
    coverage.set_defaults(func=cmd_verify_release_coverage)
    return parser


def main():
    args = build_parser().parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
