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


# A promotion is a narrowly bound use of the existing validation-only request.
# This is a tool boundary, not an alternative application ownership registry.
TRUSTED_TOOL_PATHS = frozenset({
    'AgentPortal/AgentPortal.csproj',
    'AgentPortal.Tests/AgentPortal.Tests.csproj',
    'scripts/DomainReleaseRefresh/DomainReleaseRefresh.csproj',
    'AgentPortal.Tests/WebsiteContentEditorRoundTripTests.cs',
    'scripts/deploy-approved-app.py',
    '.github/workflows/masterapp-platform-architecture-validation.yml',
    '.github/workflows/parfait-isolated-validation.yml',
    '.github/workflows/parfait-cutover-live-readonly.yml',
    '.github/workflows/legend-fast-track-ui.yml',
    '.github/workflows/legend-release-lifecycle.yml',
    'scripts/validation-resume.py', 'scripts/release_policy.py',
    'scripts/release-lifecycle.py', 'scripts/migration-probe-package.py',
    'scripts/test-validation-resume.py', 'scripts/test-release-policy.py',
    'scripts/test-release-lifecycle.py', 'scripts/test-fast-track-release.py',
    'scripts/test-parfait-readonly-workflow.py', 'scripts/test-migration-probe-package.py',
    'Infrastructure/Infrastructure.csproj',
})


def tool_promotion_requested(sha):
    # Presence, including null/malformed/stale values, must never fall through
    # into normal publication. The exact validator owns positive admission.
    return 'trustedToolPromotion' in read_request(sha)


def trusted_tool_promotion(before, revision, admission):
    """Bind a nonpublishing request to an authenticated exact merged source."""
    import re
    request = read_request(revision)
    value = request.get('trustedToolPromotion')
    def sha(value):
        return isinstance(value, str) and re.fullmatch('[a-f0-9]{40}', value) is not None
    if (not isinstance(value, dict) or set(value) != {'schemaVersion', 'baseRevision', 'sourceRevision', 'files'}
        or type(value['schemaVersion']) is not int or value['schemaVersion'] != 1
        or not all(sha(v) for v in (before, revision, value.get('baseRevision'), value.get('sourceRevision')))
        or value['baseRevision'] != before or request.get('releaseMode') != 'validate-only'
        or request.get('targets') != [] or request.get('cloudflareWebsiteRouting') is not False
        or request.get('preserveLiveTargets') is not False
        or request.get('requestedBy') != 'Founder' or not isinstance(request.get('requestId'), str) or not request['requestId']
        or not isinstance(request.get('authorization'), str) or not request['authorization']
        or admission.get('executionAuthority') != revision or not sha(admission.get('candidate'))
        or type(admission.get('sourcePr')) is not int or admission['sourcePr'] < 1
        or set(admission.get('validation', {})) != {'approved-release-security-validation.yml',
            'masterapp-platform-architecture-validation.yml', 'parfait-isolated-validation.yml'}):
        raise ValueError('TRUSTED_TOOL_PROMOTION_AUTHORIZATION_UNPROVEN')
    previous = read_request(before)
    if request['requestId'] == previous.get('requestId'):
        raise ValueError('TRUSTED_TOOL_PROMOTION_REQUEST_REPLAYED')
    def git(*args):
        result = subprocess.run(['git', *args], capture_output=True, text=True, check=False)
        if result.returncode:
            raise ValueError('TRUSTED_TOOL_PROMOTION_IMMUTABLE_SOURCE_UNAVAILABLE')
        return result.stdout
    parents = git('rev-list', '--parents', '-n', '1', revision).split()
    if len(parents) != 3 or parents[0] != revision or parents[1] != before:
        raise ValueError('TRUSTED_TOOL_PROMOTION_BASE_CHANGED')
    changed = git('diff', '--no-renames', '--name-status', '-z', before, revision, '--').split('\0')
    if changed[-1:] != [''] or len(changed[:-1]) % 2:
        raise ValueError('TRUSTED_TOOL_PROMOTION_DIFF_UNPROVEN')
    rows = list(zip(changed[:-1:2], changed[1:-1:2]))
    files = value.get('files')
    if (not isinstance(files, dict) or not files or not set(files) <= TRUSTED_TOOL_PATHS
        or any(status != 'M' for status, path in rows)
        or len({path for status, path in rows}) != len(rows)
        or {path for status, path in rows} != set(files) | {REQUEST}):
        raise ValueError('TRUSTED_TOOL_PROMOTION_SCOPE_UNPROVEN')
    def entry(ref, path):
        result = git('ls-tree', '-z', ref, '--', path).split('\0')
        if len(result) != 2 or result[1] or '\t' not in result[0]:
            raise ValueError('TRUSTED_TOOL_PROMOTION_TREE_UNPROVEN')
        metadata, name = result[0].split('\t', 1)
        mode, kind, blob = metadata.split()
        if name != path or kind != 'blob' or mode not in {'100644', '100755'} or not sha(blob):
            raise ValueError('TRUSTED_TOOL_PROMOTION_TREE_UNPROVEN')
        return dict(mode=mode, blob=blob)
    for path, expected in files.items():
        if (not isinstance(expected, dict) or set(expected) != {'mode', 'blob'}
            or expected != entry(revision, path) or expected != entry(value['sourceRevision'], path)
            or expected['mode'] != entry(before, path)['mode']):
            raise ValueError('TRUSTED_TOOL_PROMOTION_BLOB_UNPROVEN')
    # Even this shared application project has a single permitted tool-build
    # prerequisite. No runtime/source/project graph change is hidden as tooling.
    dependency = 'Infrastructure/Infrastructure.csproj'
    if dependency in files:
        prior, current = git('show', before + ':' + dependency), git('show', revision + ':' + dependency)
        addition = '    <PackageReference Include="System.Security.Cryptography.Pkcs" Version="10.0.13" />\n'
        if current.count(addition) != 1 or current.replace(addition, '') != prior:
            raise ValueError('TRUSTED_TOOL_PROMOTION_DEPENDENCY_SCOPE_UNPROVEN')
    # Only the Founder-approved security pins may change in these project files.
    security_patches = {
        'AgentPortal/AgentPortal.csproj': ('MessagePack', '3.1.7', '3.1.9'),
        'AgentPortal.Tests/AgentPortal.Tests.csproj': ('System.Security.Cryptography.Xml', '10.0.10', '10.0.13'),
        'scripts/DomainReleaseRefresh/DomainReleaseRefresh.csproj': ('System.Security.Cryptography.Xml', '10.0.10', '10.0.13'),
    }
    for dependency, (package, old, new) in security_patches.items():
        if dependency not in files:
            continue
        prior, current = git('show', before + ':' + dependency), git('show', revision + ':' + dependency)
        old_ref = f'<PackageReference Include="{package}" Version="{old}" />'
        new_ref = f'<PackageReference Include="{package}" Version="{new}" />'
        if (prior.count(old_ref) != 1 or current.count(new_ref) != 1
                or prior.replace(old_ref, new_ref) != current):
            raise ValueError('TRUSTED_TOOL_PROMOTION_SECURITY_PACKAGE_SCOPE_UNPROVEN')
    # Reuse only the independently accepted default-favicon fixture correction.
    fixture = 'AgentPortal.Tests/WebsiteContentEditorRoundTripTests.cs'
    if fixture in files and files[fixture]['blob'] != 'a380649c2168e7e67b391be1147ecc40b55c0024':
        raise ValueError('TRUSTED_TOOL_PROMOTION_FIXTURE_SCOPE_UNPROVEN')
    request_entry = entry(revision, REQUEST)
    if request_entry['mode'] != entry(before, REQUEST)['mode']:
        raise ValueError('TRUSTED_TOOL_PROMOTION_REQUEST_MODE_CHANGED')
    return dict(schemaVersion=1, phase='trusted-tool-promotion', productionEligible=False,
        authorizesMutation=False, baseRevision=before, executionAuthority=revision,
        sourceRevision=value['sourceRevision'], requestId=request['requestId'],
        requestEntry=request_entry, files=files, source=admission)


def release_after_tool_promotion(before, revision, admission):
    """Removing a persistent hold requires a fresh existing release request."""
    prior, request = read_request(before), read_request(revision)
    if (request.get('releaseMode') != 'approved-only' or 'trustedToolPromotion' in request
        or request.get('requestedBy') != 'Founder' or not request.get('authorization')
        or not request.get('requestId') or request['requestId'] == prior.get('requestId')
        or admission.get('executionAuthority') != revision
        or not isinstance(request.get('targets'), list) or not request['targets']
        or type(request.get('cloudflareWebsiteRouting')) is not bool):
        raise ValueError('TRUSTED_TOOL_PROMOTION_RELEASE_TRANSITION_UNPROVEN')
    return request

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
