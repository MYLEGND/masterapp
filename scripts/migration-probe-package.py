#!/usr/bin/env python3
"""Validate the read-only migration probe independently of immutable app ZIPs."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess


def authority():
    spec = importlib.util.spec_from_file_location('validation_resume', Path(__file__).with_name('validation-resume.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


AUTHORITY = authority()


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def manifest(directory, tool_revision, application_revision):
    data = json.loads((directory / 'manifest.json').read_text())
    expected = AUTHORITY.migration_probe_identity(tool_revision, application_revision)
    for key, value in expected.items():
        if data.get(key) != value:
            raise ValueError('Migration probe dependency identity mismatch: ' + key)
    files = data.get('files')
    if not isinstance(files, dict) or 'MigrationReleaseProbe.dll' not in files:
        raise ValueError('Migration probe inventory incomplete')
    actual = {str(path.relative_to(directory)): digest(path) for path in directory.rglob('*')
              if path.is_file() and path != directory / 'manifest.json'}
    if actual != files:
        raise ValueError('Migration probe immutable digest mismatch')
    return data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['build', 'plan', 'resolve', 'verify'])
    parser.add_argument('--tool-revision', required=True)
    parser.add_argument('--application-revision', required=True)
    parser.add_argument('--directory', required=True, type=Path)
    parser.add_argument('--output')
    args = parser.parse_args()
    identity = AUTHORITY.migration_probe_identity(args.tool_revision, args.application_revision)
    if args.command == 'build':
        head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
        if head != args.tool_revision or args.tool_revision != args.application_revision:
            raise ValueError('Probe must build once from exact checked-out validated source')
        args.directory.mkdir(parents=True, exist_ok=False)
        subprocess.run(['dotnet', 'publish', 'scripts/MigrationReleaseProbe/MigrationReleaseProbe.csproj',
                        '-c', 'Release', '--nologo', '-o', str(args.directory), '-m:1', '-nr:false',
                        '-p:UseSharedCompilation=false'], check=True)
        identity['files'] = {str(path.relative_to(args.directory)): digest(path)
                             for path in args.directory.rglob('*') if path.is_file()}
        (args.directory / 'manifest.json').write_text(json.dumps(identity, sort_keys=True) + '\n')
        manifest(args.directory, args.tool_revision, args.application_revision)
        result = {'artifact': identity['artifact'], 'identity': identity['identity']}
    elif args.command in {'resolve', 'plan'}:
        result = AUTHORITY.migration_probe_evidence(os.environ['GITHUB_REPOSITORY'], identity)
        result['needed'] = str(not result.get('reusable')).lower()
        if args.command == 'resolve' and not result.get('reusable'):
            raise ValueError('Validated migration probe missing; release cannot rebuild it')
    else:
        manifest(args.directory, args.tool_revision, args.application_revision)
        return
    if args.output:
        with open(args.output, 'a') as stream:
            for key in ('artifact', 'identity', 'runId', 'needed'):
                if key in result:
                    stream.write(f'{key}={result[key]}\n')
    print(json.dumps(result, sort_keys=True))


if __name__ == '__main__':
    main()
