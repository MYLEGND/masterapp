#!/usr/bin/env python3
"""Preserve exact Worker publication while downstream route proof is repaired."""
import argparse
import hashlib
import importlib.util
import json
import os
import subprocess
import time
import urllib.request
from pathlib import Path


def load(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def prepare(journal, version):
    if journal.success is not None:
        if journal.success.get('observation') != {'providerVersion': version}:
            raise RuntimeError('Router current version differs from retained publication; no write authorized')
        return 'preserve'
    if journal.intent is not None:
        raise RuntimeError('Router publication outcome ambiguous; preserve state without deployment replay')
    journal.before_mutation({'providerVersion': version or 'absent'})
    return 'publish'


def reconcile_tagged_router(journal, observe, publish, *, sleep=time.sleep):
    """Reconcile a provider version tagged with the durable operation identity."""
    version, tag = observe()
    if journal.success is not None:
        if journal.success.get('observation') != {'providerVersion': version}:
            raise RuntimeError('Router drift from preserved success; no deployment replay')
        return 'preserved'
    if journal.intent is None:
        journal.before_mutation({'providerVersion': version or 'absent'})
        try:
            publish(journal.identity)
        except (OSError, subprocess.SubprocessError):
            # Delivery may have succeeded. Only provider readback follows.
            pass
    for attempt in range(12):
        version, tag = observe()
        if version and tag == journal.identity:
            journal.record_success({'providerVersion': version})
            return 'verified'
        if attempt < 11:
            sleep(5)
    raise RuntimeError('Router outcome unresolved; retained intent forbids another upload')


def require_disabled_cutover(bindings):
    for binding in bindings:
        if binding.get('name') in {'LEGEND_COMMERCE_CUTOVER_ENABLED', 'LEGEND_COMMERCE_PREVIEW_ENABLED'} and (binding.get('type') != 'plain_text' or str(binding.get('text', '')).lower() not in {'', 'false', '0'}):
            raise RuntimeError('Router commerce cutover flags must remain off')


def company_render_ready(authority):
    # Verify the actual anonymous compiler response before moving company HTML
    # traffic to it; an assembly revision alone cannot prove Node/render readiness.
    origin = 'https://' + authority.RELEASE_TARGETS['protect']['azureHost']
    request = urllib.request.Request(origin + '/api/website-content/public/legend/page?path=/',
        headers={'Accept': 'text/html', 'Cache-Control': 'no-cache'})
    with urllib.request.urlopen(request, timeout=45) as response:
        body = response.read(4 * 1024 * 1024)
        if (response.status != 200 or 'text/html' not in response.headers.get('Content-Type', '').lower()
            or not response.headers.get('X-Legend-Publication-Id')
            or b'legend-cms-published-document' not in body or b'<main' not in body.lower()
            or b'data-legend-canonical-pending=' in body
            or b'Loading the latest published website' in body
            or b'<h1' not in body.lower()):
            raise RuntimeError('Canonical published first paint unproven; routing remains unchanged')
    print('FAST_TRACK_COMPANY_RENDERER:VERIFIED_BEFORE_ROUTING')


def fast_track():
    authority = load('validation-resume')
    authority.fast_track_source_admission()
    provider = load('deploy-founder-cloudflare')
    account = os.environ['CLOUDFLARE_ACCOUNT_ID']
    config = provider.parse_jsonc(Path('Legend-Cloudflare/wrangler.website-routing.jsonc'))
    worker = config['name']
    zones = provider.cloudflare('/zones?name=mylegnd.com&per_page=50')
    if not isinstance(zones, list) or len(zones) != 1 or zones[0].get('account', {}).get('id') != account:
        raise RuntimeError('Router existing zone ownership unproven')
    zone = zones[0]['id']
    routes = provider.cloudflare('/zones/' + zone + '/workers/routes')
    expected = {r['pattern'] for r in config['routes'] if r.get('zone_name') == 'mylegnd.com'}
    if (len(expected) != len(config['routes']) or not isinstance(routes, list)
        or {r.get('pattern') for r in routes if r.get('script') == worker} != expected):
        raise RuntimeError('Router existing route inventory differs; no route mutation authorized')
    settings = provider.cloudflare(f'/accounts/{account}/workers/scripts/{worker}/settings')
    bindings = settings.get('bindings', [])
    if not any(b.get('name') == 'LEGEND_WEBSITE_BRIDGE_SECRET' and b.get('type') == 'secret_text' for b in bindings):
        raise RuntimeError('Existing router bridge secret missing')
    require_disabled_cutover(bindings)
    material = hashlib.sha256(json.dumps(dict(account=account, worker=worker, zone=zone), sort_keys=True).encode()).hexdigest()
    company_render_ready(authority)
    journal = load('release-operation-evidence').ChildJournal('routing-cloudflare', material)
    def observe():
        version = provider.current_worker_version(account, worker)
        if not version:
            return '', ''
        detail = provider.cloudflare(f'/accounts/{account}/workers/scripts/{worker}/versions/{version}')
        if detail.get('id') != version:
            raise RuntimeError('Router version identity mismatch')
        return version, detail.get('annotations', {}).get('workers/tag')
    def publish(identity):
        subprocess.run(['node_modules/.bin/wrangler', 'deploy', '--config', 'wrangler.website-routing.jsonc',
                        '--keep-vars', '--tag', identity], cwd='Legend-Cloudflare', check=True, timeout=600)
    result = reconcile_tagged_router(journal, observe, publish)
    print('FAST_TRACK_ROUTER:' + result)
    # The previous Worker upload is retained. DNS-only web hostnames cannot
    # run Worker routes until the exact apex and www address records are proxied.
    # Reuse the single Cloudflare control-plane authority; never touch MX/TXT.
    dns_authority = load('cloudflare-routing-authority')
    print('FAST_TRACK_DNS_PROXY_RECONCILIATION:apex_and_www_only', flush=True)
    dns_authority.reconcile_public_web_proxy(os.environ['CLOUDFLARE_API_TOKEN'], zone)
    print('FAST_TRACK_DNS_PROXY_RECONCILIATION:readback_complete', flush=True)
    os.environ['ROUTING_PASS_THROUGH_HOSTS'] = json.dumps(['mylegnd.com', 'www.mylegnd.com', 'protect.mylegnd.com', 'portal.mylegnd.com', 'client.mylegnd.com'])
    load('release-auxiliary').first_party_router_proof()
    # Read back both public hostnames, not only the code deployed to Cloudflare.
    for hostname in ('mylegnd.com', 'www.mylegnd.com'):
        request = urllib.request.Request('https://' + hostname + '/',
            headers={'Accept': 'text/html', 'Cache-Control': 'no-cache',
                     'User-Agent': 'LEGEND-publication-route-readback/1.0'})
        verified = False
        for attempt in range(18):
            try:
                with urllib.request.urlopen(request, timeout=20) as response:
                    body = response.read(4 * 1024 * 1024)
                    verified = (response.status == 200 and
                        bool(response.headers.get('X-Legend-Publication-Id')) and
                        b'legend-cms-published-document' in body and
                        b'data-legend-canonical-pending=' not in body and
                        b'Loading the latest published website' not in body and
                        b'<h1' in body.lower())
            except (OSError, ValueError):
                verified = False
            if verified:
                break
            if attempt < 17:
                time.sleep(10)
        if not verified:
            raise RuntimeError('LIVE_PUBLICATION_FIRST_PAINT_UNPROVEN:' + hostname)
        print('LIVE_PUBLICATION_FIRST_PAINT_VERIFIED:' + hostname)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=('prepare', 'complete', 'fast-track'))
    args = parser.parse_args()
    if args.command == 'fast-track':
        fast_track()
        raise SystemExit(0)
    load('validation-resume').assert_protected_release_execution()
    account, worker = os.environ['CLOUDFLARE_ACCOUNT_ID'], os.environ['ROUTING_WORKER']
    material = hashlib.sha256(json.dumps(dict(account=account, worker=worker,
        zone=os.environ['CLOUDFLARE_ZONE_ID']), sort_keys=True).encode()).hexdigest()
    journal = load('release-operation-evidence').ChildJournal('routing-cloudflare', material)
    version = load('deploy-founder-cloudflare').current_worker_version(account, worker)
    if args.command == 'prepare':
        print(prepare(journal, version))
    else:
        if not version:
            raise SystemExit('Router exact publication version unavailable')
        journal.record_success({'providerVersion': version})
        print('Exact router publication retained; downstream live route proof remains required.')
