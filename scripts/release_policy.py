"""One release-request policy for staging, dispatch and branch synchronization."""
import argparse
import json
from pathlib import Path
import subprocess

REQUEST = 'Docs/releases/direct-release-request.json'


def read_request(sha=None):
    if sha is None:
        value = Path(REQUEST).read_text()
    else:
        result = subprocess.run(['git', 'show', sha + ':' + REQUEST],
                                text=True, capture_output=True)
        if result.returncode:
            raise RuntimeError('Cannot establish release authorization for revision')
        value = result.stdout
    request = json.loads(value)
    if not isinstance(request, dict) or request.get('releaseMode') not in {'approved-only', 'validate-only'}:
        raise ValueError('Missing or invalid release mode; publication is not authorized')
    return request


def staging_only(sha=None):
    # A staging hold survives subsequent commits until explicitly released.
    return read_request(sha)['releaseMode'] == 'validate-only'


def production_disabled(sha='HEAD'):
    """True only for the exact approved-only release commit.

    This lets an approved release deploy its selected applications without
    opening or advancing the rigorous production path. A later commit must
    carry its own authorization and is evaluated independently.
    """
    request = read_request(sha)
    if request['releaseMode'] == 'validate-only':
        return True
    changed = subprocess.run(
        ['git', 'diff-tree', '--no-commit-id', '--name-only', '-r', sha, '--', REQUEST],
        text=True, capture_output=True, check=True).stdout.splitlines()
    return request['releaseMode'] == 'approved-only' and REQUEST in changed


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sha', default='HEAD')
    parser.add_argument('--require-release', action='store_true')
    parser.add_argument('--production-disabled', action='store_true')
    args = parser.parse_args()
    if args.production_disabled:
        print('true' if production_disabled(args.sha) else 'false')
        raise SystemExit(0)
    held = staging_only(args.sha)
    if args.require_release and held:
        raise SystemExit('Staged validation only: deployment and production synchronization are not authorized')
    print('true' if held else 'false')
