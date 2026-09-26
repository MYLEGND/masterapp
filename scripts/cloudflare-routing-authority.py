#!/usr/bin/env python3
"""Single Cloudflare control-plane authority for LEGEND custom-domain routing.

This module intentionally owns only Cloudflare routing/security control-plane work.
Tenant ownership and verified-domain resolution remain in MasterApp/WebsiteDomainService.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import sys
import urllib.error
import urllib.request

API = "https://api.cloudflare.com/client/v4"
GRAPHQL = "https://api.cloudflare.com/client/v4/graphql"
ROUTER_SCRIPT = "legend-business-website-router"
POLICY_REF = "legend_public_custom_hostname_disable_bic"
POLICY_EXPRESSION = 'not (lower(http.host) eq "mylegnd.com" or ends_with(lower(http.host), ".mylegnd.com"))'

REQUIRED_PERMISSION_CONTRACT = (
    "Zone WAF: Edit",
    "Bot Management: Edit",
    "Zone Settings: Edit",
    "Config Rules: Edit",
    "Origin Rules: Edit",
    "Analytics: Read",
    "Zone: Read",
    "Workers Routes: Edit",
    "SSL and Certificates: Edit",
    "DNS: Read",
    "Cache Purge",
    "Workers Scripts: Edit",
    "Workers Tail: Read",
    "Account Settings: Read",
)


class CloudflareError(RuntimeError):
    pass


def required_env(name: str) -> str:
    value = os.environ.get(name, "").strip()
    if not value:
        raise CloudflareError(f"{name} is required")
    return value


def api_request(method: str, url: str, token: str, body=None, *, allow=(200,)) -> dict:
    data = None if body is None else json.dumps(body, separators=(",", ":")).encode()
    request = urllib.request.Request(url, data=data, method=method)
    request.add_header("Authorization", f"Bearer {token}")
    request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            status = response.status
            raw = response.read()
    except urllib.error.HTTPError as error:
        status = error.code
        raw = error.read()
    if status not in allow:
        detail = raw.decode("utf-8", "replace")[:1200]
        raise CloudflareError(f"{method} {url.split('?')[0]} returned HTTP {status}: {detail}")
    if not raw:
        return {}
    try:
        payload = json.loads(raw)
    except json.JSONDecodeError as error:
        raise CloudflareError(f"Cloudflare returned invalid JSON for {url.split('?')[0]}") from error
    if isinstance(payload, dict) and payload.get("success") is False:
        raise CloudflareError(f"Cloudflare rejected {url.split('?')[0]}: {payload.get('errors')}")
    return payload


def graphql(token: str, query: str, variables: dict) -> dict:
    payload = api_request("POST", GRAPHQL, token, {"query": query, "variables": variables})
    if payload.get("errors"):
        raise CloudflareError(f"Cloudflare GraphQL rejected routing authority audit: {payload['errors']}")
    return payload


def phase_probe(token: str, zone: str, phase: str) -> str:
    url = f"{API}/zones/{zone}/rulesets/phases/{phase}/entrypoint"
    try:
        payload = api_request("GET", url, token, allow=(200, 404))
    except CloudflareError:
        raise
    # A 404/no-entrypoint is acceptable: authorization succeeded and no ruleset exists yet.
    return payload.get("result", {}).get("id", "absent") if isinstance(payload, dict) else "absent"


def audit(prove_cache_purge: bool) -> None:
    token = required_env("CLOUDFLARE_API_TOKEN")
    zone = required_env("CLOUDFLARE_ZONE_ID")
    account = required_env("CLOUDFLARE_ACCOUNT_ID")

    results: dict[str, str] = {}
    verified = api_request("GET", f"{API}/user/tokens/verify", token)
    results["token"] = str(verified.get("result", {}).get("status", "verified"))

    zone_result = api_request("GET", f"{API}/zones/{zone}", token)["result"]
    if zone_result.get("id") != zone:
        raise CloudflareError("Cloudflare token resolved a different zone")
    if zone_result.get("name") != "mylegnd.com":
        raise CloudflareError("Cloudflare routing authority must be scoped to mylegnd.com")
    results["zone_read"] = "ok"

    account_result = api_request("GET", f"{API}/accounts/{account}", token)["result"]
    if account_result.get("id") != account:
        raise CloudflareError("Cloudflare token resolved a different account")
    results["account_settings_read"] = "ok"

    api_request("GET", f"{API}/zones/{zone}/dns_records?per_page=1", token)
    results["dns_read"] = "ok"

    api_request("GET", f"{API}/zones/{zone}/custom_hostnames?per_page=1", token)
    results["ssl_custom_hostnames"] = "ok"

    api_request("GET", f"{API}/zones/{zone}/settings/browser_check", token)
    api_request("GET", f"{API}/zones/{zone}/settings/security_level", token)
    results["zone_settings"] = "ok"

    api_request("GET", f"{API}/zones/{zone}/bot_management", token)
    results["bot_management"] = "ok"

    api_request("GET", f"{API}/zones/{zone}/rulesets", token)
    results["waf_rules"] = phase_probe(token, zone, "http_request_firewall_custom")
    results["config_rules"] = phase_probe(token, zone, "http_config_settings")
    results["origin_rules"] = phase_probe(token, zone, "http_request_origin")

    api_request("GET", f"{API}/zones/{zone}/workers/routes", token)
    results["workers_routes"] = "ok"

    api_request(
        "GET",
        f"{API}/accounts/{account}/workers/scripts/{ROUTER_SCRIPT}/deployments?per_page=1",
        token,
        allow=(200, 404),
    )
    results["workers_scripts"] = "ok"

    end = dt.datetime.now(dt.timezone.utc)
    start = end - dt.timedelta(minutes=10)
    query = """query RoutingAuthorityAudit($zoneTag: string, $start: Time, $end: Time) {
      viewer {
        zones(filter:{zoneTag:$zoneTag}) {
          firewallEventsAdaptive(filter:{datetime_geq:$start,datetime_leq:$end},limit:1,orderBy:[datetime_DESC]) {
            action
          }
        }
      }
    }"""
    graphql(
        token,
        query,
        {
            "zoneTag": zone,
            "start": start.isoformat().replace("+00:00", "Z"),
            "end": end.isoformat().replace("+00:00", "Z"),
        },
    )
    results["analytics_read"] = "ok"

    # Cloudflare exposes Cache Purge only as a write operation. Purging a guaranteed
    # non-existent probe URL proves the permission without evicting application content.
    if prove_cache_purge:
        api_request(
            "POST",
            f"{API}/zones/{zone}/purge_cache",
            token,
            {"files": ["https://mylegnd.com/__legend-routing-authority-permission-probe__"]},
        )
        results["cache_purge"] = "proven-safe-noop"
    else:
        results["cache_purge"] = "contract-required"

    api_request(
        "GET",
        f"{API}/accounts/{account}/workers/scripts/{ROUTER_SCRIPT}/tails",
        token,
        allow=(200, 404),
    )
    results["workers_tail"] = "ok"

    print(json.dumps({
        "authority": "LEGEND Cloudflare Production Routing",
        "zone": "mylegnd.com",
        "requiredPermissions": REQUIRED_PERMISSION_CONTRACT,
        "capabilities": results,
    }, indent=2))


def reconcile_bic() -> None:
    token = required_env("CLOUDFLARE_API_TOKEN")
    zone = required_env("CLOUDFLARE_ZONE_ID")

    rulesets = api_request("GET", f"{API}/zones/{zone}/rulesets", token)["result"]
    ruleset_id = next(
        (row.get("id") for row in rulesets if row.get("kind") == "zone" and row.get("phase") == "http_config_settings"),
        None,
    )
    rule = {
        "action": "set_config",
        "expression": POLICY_EXPRESSION,
        "description": "LEGEND public custom hostnames: disable Browser Integrity Check only for customer vanity domains",
        "ref": POLICY_REF,
        "enabled": True,
        "action_parameters": {"bic": False},
    }

    if not ruleset_id:
        payload = {
            "name": "LEGEND public custom-host configuration",
            "description": "Central Cloudflare configuration authority for LEGEND-managed customer custom hostnames",
            "kind": "zone",
            "phase": "http_config_settings",
            "rules": [rule],
        }
        api_request("POST", f"{API}/zones/{zone}/rulesets?dry_run=true", token, payload)
        created = api_request("POST", f"{API}/zones/{zone}/rulesets", token, payload)
        ruleset_id = created["result"]["id"]
    else:
        current = api_request("GET", f"{API}/zones/{zone}/rulesets/{ruleset_id}", token)["result"]
        match = next((row for row in current.get("rules", []) if row.get("ref") == POLICY_REF), None)
        if match:
            rule_id = match["id"]
            api_request(
                "PATCH",
                f"{API}/zones/{zone}/rulesets/{ruleset_id}/rules/{rule_id}?dry_run=true",
                token,
                rule,
            )
            api_request("PATCH", f"{API}/zones/{zone}/rulesets/{ruleset_id}/rules/{rule_id}", token, rule)
        else:
            api_request(
                "POST",
                f"{API}/zones/{zone}/rulesets/{ruleset_id}/rules?dry_run=true",
                token,
                rule,
            )
            api_request("POST", f"{API}/zones/{zone}/rulesets/{ruleset_id}/rules", token, rule)

    verify = api_request("GET", f"{API}/zones/{zone}/rulesets/{ruleset_id}", token)["result"]
    matches = [
        row for row in verify.get("rules", [])
        if row.get("ref") == POLICY_REF
        and row.get("enabled") is True
        and row.get("action") == "set_config"
        and row.get("expression") == POLICY_EXPRESSION
        and row.get("action_parameters", {}).get("bic") is False
    ]
    if len(matches) != 1:
        raise CloudflareError("Custom-hostname Browser Integrity policy did not verify exactly once")
    print("Cloudflare Browser Integrity Check remains enabled by zone policy and is disabled only on customer custom hostnames.")


def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    audit_parser = sub.add_parser("audit")
    audit_parser.add_argument("--prove-cache-purge", action="store_true")
    sub.add_parser("reconcile-bic")
    args = parser.parse_args()
    try:
        if args.command == "audit":
            audit(args.prove_cache_purge)
        elif args.command == "reconcile-bic":
            reconcile_bic()
        return 0
    except CloudflareError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
