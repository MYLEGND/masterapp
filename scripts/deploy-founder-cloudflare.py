#!/usr/bin/env python3
"""Deploy/rollback the validated LEGEND Founder Cloudflare baseline inside the canonical release job."""
from __future__ import annotations
import argparse, base64, json, os, re, secrets, subprocess, sys, urllib.request, urllib.error
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CF = ROOT / "Legend-Cloudflare"
CONFIG = CF / "wrangler.founder-baseline.jsonc"
CANARY = CF / "scripts" / "founder-canary.mjs"
CALLBACK_PATH = "/api/founder/legend-ai/cloudflare-tools"
SETTING_PREFIX = "LegendConnect__Foundation__"
ALIASES = {
    "Enabled": ("LegendConnect__Foundation__Enabled", "LegendConnect:Foundation:Enabled"),
    "HostKind": ("LegendConnect__Foundation__HostKind", "LegendConnect:Foundation:HostKind"),
    "Model": ("LegendConnect__Foundation__Model", "LegendConnect:Foundation:Model"),
    "Endpoint": ("LegendConnect__Foundation__Endpoint", "LegendConnect:Foundation:Endpoint"),
    "AccountId": ("LegendConnect__Foundation__Cloudflare__AccountId", "LegendConnect:Foundation:Cloudflare:AccountId"),
    "KeyId": ("LegendConnect__Foundation__Cloudflare__KeyId", "LegendConnect:Foundation:Cloudflare:KeyId"),
    "SigningKey": ("LegendConnect__Foundation__Cloudflare__SigningKey", "LegendConnect:Foundation:Cloudflare:SigningKey"),
    "Environment": ("LegendConnect__Foundation__Cloudflare__Environment", "LegendConnect:Foundation:Cloudflare:Environment"),
    "MaxCost": ("LegendConnect__Foundation__Cloudflare__MaxCostMicrousd", "LegendConnect:Foundation:Cloudflare:MaxCostMicrousd"),
    "Timeout": ("LegendConnect__Foundation__TimeoutSeconds", "LegendConnect:Foundation:TimeoutSeconds"),
    "MaxOutput": ("LegendConnect__Foundation__MaxOutputTokens", "LegendConnect:Foundation:MaxOutputTokens"),
    "Tools": ("LegendConnect__Foundation__Cloudflare__ToolCallbackEnabled", "LegendConnect:Foundation:Cloudflare:ToolCallbackEnabled"),
    "CallbackKeyId": ("LegendConnect__Foundation__Cloudflare__CallbackKeyId", "LegendConnect:Foundation:Cloudflare:CallbackKeyId"),
    "CallbackSigningKey": ("LegendConnect__Foundation__Cloudflare__CallbackSigningKey", "LegendConnect:Foundation:Cloudflare:CallbackSigningKey"),
    "Mutations": ("LegendConnect__Foundation__Cloudflare__MutationsEnabled", "LegendConnect:Foundation:Cloudflare:MutationsEnabled"),
    "Qualification": ("LegendConnect__Foundation__Cloudflare__QualificationEnabled", "LegendConnect:Foundation:Cloudflare:QualificationEnabled"),
}
ALL_SETTING_NAMES = tuple(dict.fromkeys(name for names in ALIASES.values() for name in names))

def run(*args, input_text=None, cwd=None, capture=False):
    return subprocess.run(list(args), input=input_text, text=True, cwd=cwd, check=True,
                          stdout=subprocess.PIPE if capture else None).stdout if capture else None

def az_json(*args):
    return json.loads(subprocess.check_output(["az", *args, "-o", "json"], text=True))

def settings(app, group):
    return az_json("webapp", "config", "appsettings", "list", "-g", group, "-n", app)

def value(rows, *names):
    by = {row.get("name"): row.get("value") for row in rows}
    return next((str(by[name]) for name in names if by.get(name) not in (None, "")), "")

def mask(value_):
    if value_: print("::add-mask::" + value_, flush=True)

def valid_b64_key(value_):
    try: raw = base64.b64decode(value_, validate=True)
    except Exception: return False
    return 32 <= len(raw) <= 128

def new_key():
    return base64.b64encode(secrets.token_bytes(32)).decode()

def parse_jsonc(path):
    lines = [line for line in path.read_text().splitlines() if not line.lstrip().startswith("//")]
    return json.loads("\n".join(lines))

def cloudflare(path, method="GET", payload=None, allow_404=False):
    token = required("CLOUDFLARE_API_TOKEN")
    url = "https://api.cloudflare.com/client/v4" + path
    body = None if payload is None else json.dumps(payload, separators=(",", ":")).encode()
    headers = {"Authorization": "Bearer " + token}
    if body is not None: headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            data = json.load(response)
    except urllib.error.HTTPError as error:
        if allow_404 and error.code == 404: return None
        raise RuntimeError(f"cloudflare_http_{error.code}") from error
    if data.get("success") is not True:
        raise RuntimeError("cloudflare_request_failed")
    return data.get("result")

def required(name):
    value_ = os.environ.get(name, "").strip()
    if not value_: raise RuntimeError("missing_" + name.lower())
    return value_

def exact_models():
    text = subprocess.check_output([
        "node", "--input-type=module", "-e",
        "import {FOUNDER_BASELINE_MODEL_IDS} from './Legend-Cloudflare/src/runtime/registry.mjs';"
        "process.stdout.write(JSON.stringify(FOUNDER_BASELINE_MODEL_IDS));"
    ], cwd=ROOT, text=True)
    models = json.loads(text)
    if not isinstance(models, list) or len(models) != 5 or any(not isinstance(item, str) for item in models):
        raise RuntimeError("founder_model_registry_invalid")
    return models

def worker_name():
    name = parse_jsonc(CONFIG).get("name")
    if not isinstance(name, str) or not re.fullmatch(r"[a-z0-9-]{1,63}", name):
        raise RuntimeError("founder_worker_name_invalid")
    return name

def current_worker_version(account, worker):
    result = cloudflare(f"/accounts/{account}/workers/scripts/{worker}/deployments?per_page=1", allow_404=True)
    if result is None: return ""
    deployments = result.get("deployments") or []
    versions = deployments[0].get("versions") if deployments else []
    return str(versions[0].get("version_id") or "") if versions else ""

def restore_worker(account, worker, version):
    if version:
        cloudflare(f"/accounts/{account}/workers/scripts/{worker}/deployments", "POST", {
            "strategy": "percentage",
            "versions": [{"version_id": version, "percentage": 100}],
            "annotations": {"workers/message": "Automatic LEGEND Founder baseline rollback"},
        })
    else:
        cloudflare(f"/accounts/{account}/workers/scripts/{worker}", "DELETE", allow_404=True)

def snapshot(rows):
    names = set(ALL_SETTING_NAMES)
    return [{"name": row["name"], "value": row.get("value", "")} for row in rows if row.get("name") in names]

def restore_settings(group, app, previous):
    # Remove every canonical/legacy alias first, then restore exactly what existed.
    subprocess.run(["az", "webapp", "config", "appsettings", "delete", "-g", group, "-n", app,
                    "--setting-names", *ALL_SETTING_NAMES, "--output", "none"], check=False)
    if previous:
        pairs = [f"{row['name']}={row.get('value','')}" for row in previous]
        run("az", "webapp", "config", "appsettings", "set", "-g", group, "-n", app,
            "--settings", *pairs, "--output", "none")

def write_state(path, data):
    path.write_text(json.dumps(data, sort_keys=True, indent=2) + "\n")
    path.chmod(0o600)

def deploy(state_path, receipt_path):
    group = required("RELEASE_RESOURCE_GROUP")
    app = required("DATABASE_AUTHORITY")
    account = required("CLOUDFLARE_ACCOUNT_ID")
    tenant = required("AZURE_TENANT_ID")
    required("CLOUDFLARE_API_TOKEN")
    if not re.fullmatch(r"[A-Za-z0-9_-]{16,64}", account):
        raise RuntimeError("cloudflare_account_invalid")
    rows = settings(app, group)
    founder = value(rows, "FOUNDER_OID", "FounderOid", "Founder__Oid", "Founder:Oid")
    if not founder: raise RuntimeError("founder_identity_unavailable")
    service_key_id = value(rows, *ALIASES["KeyId"]) or "legend-founder-worker-v1"
    service_key = value(rows, *ALIASES["SigningKey"]) or new_key()
    callback_key_id = value(rows, *ALIASES["CallbackKeyId"]) or "legend-founder-callback-v1"
    callback_key = value(rows, *ALIASES["CallbackSigningKey"]) or new_key()
    if not all(re.fullmatch(r"[A-Za-z0-9_.:@-]{1,128}", item) for item in (service_key_id, callback_key_id)):
        raise RuntimeError("founder_key_id_invalid")
    if not valid_b64_key(service_key) or not valid_b64_key(callback_key):
        raise RuntimeError("founder_signing_key_invalid")
    for secret in (founder, service_key, callback_key, tenant, account): mask(secret)

    models = exact_models()
    policy = {
        "version": "legend-founder-baseline.v3", "accountId": account, "tenantId": tenant,
        "founderUserId": founder, "serviceKeyId": service_key_id, "requiredRole": "Founder",
        "environment": "production", "modelIds": models, "lifetimeCostMicrousd": 3_000_000,
    }
    service_keys = {service_key_id: service_key}
    worker = worker_name()
    previous_version = current_worker_version(account, worker)
    state = {
        "schemaVersion": 1, "resourceGroup": group, "app": app, "accountId": account,
        "worker": worker, "previousWorkerVersion": previous_version, "previousSettings": snapshot(rows),
        "portalModified": False, "workerModified": False,
    }
    write_state(state_path, state)

    release_config = CF / "wrangler.founder-baseline.release.jsonc"
    config = parse_jsonc(CONFIG)
    config["account_id"] = account
    config.setdefault("vars", {})
    config["vars"].update({
        "LEGEND_ACCOUNT_ID": account,
        "LEGEND_AZURE_TOOL_CALLBACK_URL": f"https://{app}.azurewebsites.net{CALLBACK_PATH}",
        "LEGEND_TOOL_CALLBACK_KEY_ID": callback_key_id,
        "LEGEND_TOOL_MAX_COST_MICROUSD": "50000",
    })
    release_config.write_text(json.dumps(config, indent=2) + "\n")
    try:
        run("npm", "ci", "--ignore-scripts", "--no-audit", "--no-fund", cwd=CF)
        run("npx", "wrangler", "deploy", "--config", release_config.name, cwd=CF)
        state["workerModified"] = True; write_state(state_path, state)
        for name, secret_value in (
            ("LEGEND_SERVICE_KEYS_JSON", json.dumps(service_keys, separators=(",", ":"))),
            ("LEGEND_FOUNDER_BASELINE_POLICY_JSON", json.dumps(policy, separators=(",", ":"))),
            ("LEGEND_TOOL_CALLBACK_SECRET", callback_key),
        ):
            run("npx", "wrangler", "secret", "put", name, "--config", release_config.name,
                input_text=secret_value, cwd=CF)

        subdomain = cloudflare(f"/accounts/{account}/workers/subdomain").get("subdomain")
        if not isinstance(subdomain, str) or not re.fullmatch(r"[A-Za-z0-9.-]{1,253}", subdomain):
            raise RuntimeError("workers_subdomain_unavailable")
        endpoint = f"https://{worker}.{subdomain}.workers.dev/v1/legend/respond"
        canary_output = Path("/tmp/legend-founder-cloudflare-canary.json")
        canary_env = os.environ.copy()
        canary_env.update({
            "LEGEND_CANARY_ENDPOINT": endpoint, "LEGEND_ACCOUNT_ID": account, "LEGEND_TENANT_ID": tenant,
            "LEGEND_FOUNDER_USER_ID": founder, "LEGEND_SERVICE_KEY_ID": service_key_id,
            "LEGEND_SERVICE_SIGNING_KEY": service_key, "LEGEND_CANARY_OUTPUT": str(canary_output),
        })
        subprocess.run(["node", str(CANARY)], cwd=ROOT, env=canary_env, check=True)

        canonical = {
            ALIASES["Enabled"][0]: "true", ALIASES["HostKind"][0]: "Cloudflare",
            ALIASES["Model"][0]: "cloudflare:registry", ALIASES["Endpoint"][0]: endpoint,
            ALIASES["AccountId"][0]: account, ALIASES["KeyId"][0]: service_key_id,
            ALIASES["SigningKey"][0]: service_key, ALIASES["Environment"][0]: "production",
            ALIASES["MaxCost"][0]: "3000000", ALIASES["Timeout"][0]: "90",
            ALIASES["MaxOutput"][0]: "2048", ALIASES["Tools"][0]: "true",
            ALIASES["CallbackKeyId"][0]: callback_key_id, ALIASES["CallbackSigningKey"][0]: callback_key,
            ALIASES["Mutations"][0]: "false", ALIASES["Qualification"][0]: "false",
        }
        run("az", "webapp", "config", "appsettings", "set", "-g", group, "-n", app,
            "--settings", *[f"{key}={value_}" for key, value_ in canonical.items()], "--output", "none")
        stale = [name for names in ALIASES.values() for name in names[1:]]
        if stale:
            subprocess.run(["az", "webapp", "config", "appsettings", "delete", "-g", group, "-n", app,
                            "--setting-names", *stale, "--output", "none"], check=True)
        state["portalModified"] = True; state["endpoint"] = endpoint; write_state(state_path, state)

        observed = {row.get("name"): str(row.get("value", "")) for row in settings(app, group)}
        for key, expected in canonical.items():
            if observed.get(key) != expected: raise RuntimeError("founder_portal_activation_readback_failed")
        receipt = json.loads(canary_output.read_text())
        receipt.update({
            "worker": worker, "endpointHost": urllib.request.urlparse(endpoint).hostname if False else endpoint.split("/")[2],
            "portal": app, "mutationsEnabled": False, "modelCount": len(models),
            "policyPersistence": "persistent", "budgetPeriod": "lifetime",
            "releaseSha": os.environ.get("APPLICATION_RELEASE_SHA", ""),
        })
        receipt_path.write_text(json.dumps(receipt, sort_keys=True, indent=2) + "\n")
        print(json.dumps({k: receipt[k] for k in ("provider","foundationHosting","foundationModel","billing","openAiApiUsed","modelCount")}, sort_keys=True))
    except Exception:
        try:
            if state.get("portalModified"): restore_settings(group, app, state["previousSettings"])
        finally:
            if state.get("workerModified"): restore_worker(account, worker, previous_version)
        raise
    finally:
        release_config.unlink(missing_ok=True)

def rollback(state_path):
    if not state_path.exists(): raise RuntimeError("founder_cloudflare_state_missing")
    state = json.loads(state_path.read_text())
    group, app, account, worker = (state[k] for k in ("resourceGroup","app","accountId","worker"))
    if state.get("portalModified"): restore_settings(group, app, state.get("previousSettings") or [])
    if state.get("workerModified"): restore_worker(account, worker, state.get("previousWorkerVersion") or "")
    print("Restored the pre-release LEGEND Founder Cloudflare state.")

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("deploy","rollback"))
    parser.add_argument("--state", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, default=Path("/tmp/legend-founder-cloudflare-release.json"))
    args = parser.parse_args()
    if args.command == "deploy": deploy(args.state, args.receipt)
    else: rollback(args.state)

if __name__ == "__main__":
    main()
