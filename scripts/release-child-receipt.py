#!/usr/bin/env python3
"""Retain child success after its owner has freshly reconciled live state."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('child', choices=('shared-config', 'editor-config', 'live-proof'))
    parser.add_argument('--target')
    parser.add_argument('--partition', choices=('authorization', 'marketing', 'editor'))
    parser.add_argument('--prepare', action='store_true')
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location('journal', Path(__file__).with_name('release-operation-evidence.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    targets = json.loads(os.environ['SELECTED_TARGETS'])
    # Public resource identities only. Never persist or hash configuration values.
    material = dict(resourceGroup=os.environ['RELEASE_RESOURCE_GROUP'], targets=sorted(targets))
    if args.target:
        if args.child == 'live-proof' or args.target not in targets or not args.partition:
            raise SystemExit('Invalid canonical configuration target')
        material = dict(resourceGroup=os.environ['RELEASE_RESOURCE_GROUP'], target=args.target,
                        partition=args.partition, authority=os.environ['DATABASE_AUTHORITY'])
    if args.child == 'live-proof':
        material['packageIdentity'] = os.environ['PACKAGE_IDENTITY']
    identity = hashlib.sha256(json.dumps(material, sort_keys=True).encode()).hexdigest()
    journal_type = module.ConfigurationJournal if args.target else module.ChildJournal
    journal = journal_type(args.child, identity)
    if args.prepare:
        if not args.target:
            raise SystemExit('Configuration write intent requires a selected target')
        journal.before_mutation({})
    else:
        journal.record_success({})
