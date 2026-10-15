#!/usr/bin/env python3
"""Preserve exact Worker publication while downstream route proof is repaired."""
import argparse
import hashlib
import importlib.util
import json
import os
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


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=('prepare', 'complete'))
    args = parser.parse_args()
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
