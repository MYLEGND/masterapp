"""One release-request policy for staging, dispatch and branch synchronization."""
import argparse
import json
from pathlib import Path
import subprocess

REQUEST = 'Docs/releases/direct-release-request.json'

# One explicitly approved admission root, NOT a successful/live baseline and
# NOT a generic reset. Existing operation IDs and retained history are unchanged.
# Current owner/provider/rollback/schema proof remains mandatory before intent.
STABLE_BASELINE_ADOPTION = {'schemaVersion': 1, 'generation': 'stable-base-3243221-20261031-v1', 'authorization': 'Sentinel_1a096f6271dc8191808e39cd0e87e11e', 'authorizationUtc': '2026-10-31T01:53:42Z', 'implementationBase': '1ecc94b988188bf664e1e515b363f63dbed915fe', 'originalRun': 41131223973, 'originalAttempt': 4, 'applicationRevision': '3243221ae661b53d60f78cc2dab108a93bad7632', 'targets': {'portal': {'operationId': '274de3b30b337020a30da96e8aec03d5664c1669eaee0849094e4b1c3b8ed240', 'packageDigest': '9252d398f53338d79e68b34cee8cd52cbdc3f094901f3f077b43048b77079ed1', 'packageArtifactId': 13016469859, 'packageArtifactDigest': 'sha256:c047e89e96a68d84555f342dc6806cd5024f7c3b389f1667b6bac6d918c223f0'}, 'client': {'operationId': '63bcbb93939b90faa9010f64309b8f343d44866d5e570a200e3e25af53c84a1a', 'packageDigest': '2e78237df64f9c9e7ba2d9016401ffa0ab47d38b455a681b2cc02c885157744b', 'packageArtifactId': 13016754756, 'packageArtifactDigest': 'sha256:625a555dc6aaec04bad4dbd25f63c8a9a98fd1f0bdc237ab4426c15b78bdc5fd'}, 'protect': {'operationId': 'ca053279fd3a8892d0a8ab69d2bc7c0a2bbb1c904bbe743b44456fb3cf3a4d00', 'packageDigest': '3e8ba8b6073bec9e3a64204c0e0b745e8dcf894c28be05900b158bdc2e28431c', 'packageArtifactId': 13016709993, 'packageArtifactDigest': 'sha256:171f0b608107fb23f33ce5cb572ded74303018e120ca088e93341dad18aeb3d0'}, 'parfait': {'operationId': 'c5706545da6cdfecd541252b8c83f91580c7982053c3283ac4e68e29e76782e6', 'packageDigest': '61c0df9a39064aef218209b2ef1633065ed882b02b01a5715d9ee447dd096af4', 'packageArtifactId': 13016969652, 'packageArtifactDigest': 'sha256:1d119e8af08ef8afaa7ab7d8f7a9f99df0c9ce8dce222d8f0d68cce070ad58d0'}}, 'schemaIdentity': '0e4674714d917c5f2dbfa3cd42f2de954e7b3f310c8322c08ec0f427d3615451', 'databaseIdentity': '3a055450807aeaa2c6465702b983dc99968329389d7c0ae480c3c72cc3a9ff3b', 'schemaSuccessArtifact': 13017640415, 'schemaSuccessDigest': 'sha256:cbe0bd54e2e652908a19d84779ec10f51246cf3e01e3920f8e59d50c70b2a508', 'cloudflareWebsiteRouting': False, 'sqlExecutionAuthorized': False}

def stable_baseline_adoption(repository, original_run, application_revision):
    """Return only the exact Founder-approved four-package authority."""
    import copy
    if (repository.lower() != 'mylegnd/masterapp'
        or original_run != STABLE_BASELINE_ADOPTION['originalRun']
        or application_revision != STABLE_BASELINE_ADOPTION['applicationRevision']):
        return None
    return copy.deepcopy(STABLE_BASELINE_ADOPTION)



# The permanent forward contract references existing immutable completion receipts.
# References are inputs, not authority: every consumer authenticates their producer,
# bytes, currentness and live state. Unknown resources never inherit an app baseline.
COMPLETED_RESOURCE_POLICY = 'completed-resource-v1'
COMPLETED_RESOURCE_AUTHORIZATION = 'Sentinel_84879ea6a8548191abb3fae9d62d1af2'
COMPLETED_RESOURCE_ROOT = '41240193444:1:13058045416'
COMPLETED_RESOURCE_ROOT_TOOL = 'fd4e2e49b98456604ef2602b8409069b73bab492'
COMPLETED_RESOURCE_ROOT_REFS = {'portal': {'sourceRevision': '3243221ae661b53d60f78cc2dab108a93bad7632', 'runId': 41235118337, 'runAttempt': 1, 'artifactId': 13055430973, 'artifactDigest': 'sha256:1be025a9f047228c7ed1599d0406c785d56d78797ef8bf1e4a85b3a3dff081e9', 'artifactName': 'legend-release-operation-success-274de3b30b337020a30da96e8aec03d5664c1669eaee0849094e4b1c3b8ed240'}, 'client': {'sourceRevision': '3243221ae661b53d60f78cc2dab108a93bad7632', 'runId': 41240193444, 'runAttempt': 1, 'artifactId': 13056979567, 'artifactDigest': 'sha256:ccaaaadc7e310049712011ce70c22bf02847993243ff292213ca53e764060d08', 'artifactName': 'legend-release-operation-success-63bcbb93939b90faa9010f64309b8f343d44866d5e570a200e3e25af53c84a1a'}, 'protect': {'sourceRevision': '3243221ae661b53d60f78cc2dab108a93bad7632', 'runId': 41240193444, 'runAttempt': 1, 'artifactId': 13057606696, 'artifactDigest': 'sha256:f9856400f0db7b724940d3f7fdb567cbb1c5055a21df0471d486411a17f92dba', 'artifactName': 'legend-release-operation-success-ca053279fd3a8892d0a8ab69d2bc7c0a2bbb1c904bbe743b44456fb3cf3a4d00'}, 'parfait': {'sourceRevision': '3243221ae661b53d60f78cc2dab108a93bad7632', 'runId': 41240193444, 'runAttempt': 1, 'artifactId': 13057074013, 'artifactDigest': 'sha256:48a1f956189c13ef29356ce1bf44e009933d6a9e02c8d4246d0f1090c2818153', 'artifactName': 'legend-release-operation-success-c5706545da6cdfecd541252b8c83f91580c7982053c3283ac4e68e29e76782e6'}, 'schema': {'sourceRevision': '3243221ae661b53d60f78cc2dab108a93bad7632', 'runId': 41240193444, 'runAttempt': 1, 'artifactId': 13056544969, 'artifactDigest': 'sha256:f3cd53e34e15c9f3d3615833a6ef70fc6bf488f61f97d3c71c907e48e419c176', 'artifactName': 'legend-retained-schema-proof-2bc7a1d8bda8fb423826f94edf71a1f5f508259acf56b18a4c7b5dd381cbe4e9'}, 'website': None, 'routing': None, 'founder': None}


def completed_resource_baselines(request):
    """Validate the versioned existing-request reference vector, never accept it."""
    import copy
    import re
    value = request.get('perResourceBaselines')
    if value is None:
        if 'perResourceBaselines' in request:
            raise ValueError('RESOURCE_BASELINE_CONTRACT_INVALID')
        return None
    if (not isinstance(value, dict) or set(value) != {'schemaVersion', 'policy', 'root', 'resources'}
        or type(value['schemaVersion']) is not int or value['schemaVersion'] != 1
        or value['policy'] != COMPLETED_RESOURCE_POLICY or value['root'] != COMPLETED_RESOURCE_ROOT
        or not isinstance(value['resources'], dict)
        or set(value['resources']) != set(COMPLETED_RESOURCE_ROOT_REFS)):
        raise ValueError('RESOURCE_BASELINE_CONTRACT_INVALID')
    fields = {'sourceRevision','runId','runAttempt','artifactId','artifactDigest','artifactName'}
    for resource, ref in value['resources'].items():
        if ref is None:
            if COMPLETED_RESOURCE_ROOT_REFS[resource] is not None:
                raise ValueError('RESOURCE_BASELINE_VERIFIED_RESOURCE_DROPPED')
            continue
        if (not isinstance(ref, dict) or set(ref) != fields
            or any(type(ref[k]) is not int or ref[k] < 1 for k in ('runId','runAttempt','artifactId'))
            or not re.fullmatch('[a-f0-9]{40}', str(ref['sourceRevision']))
            or not re.fullmatch('sha256:[a-f0-9]{64}', str(ref['artifactDigest']))
            or not re.fullmatch('[a-zA-Z0-9_.-]{1,256}', str(ref['artifactName']))):
            raise ValueError('RESOURCE_BASELINE_REFERENCE_INVALID')
        prefix = ('legend-release-operation-success-' if resource in {'portal','client','protect','parfait','website'}
                  else 'legend-release-child-success-')
        if (not ref['artifactName'].startswith(prefix)
            and not (resource in {'portal','client','protect','parfait','website'}
                     and ref['artifactName'].startswith('legend-release-target-acceptance-'))
            and ref != COMPLETED_RESOURCE_ROOT_REFS[resource]):
            raise ValueError('RESOURCE_BASELINE_NOT_COMPLETION')
    return copy.deepcopy(value)


def initial_completed_resource_baselines():
    import copy
    return dict(schemaVersion=1, policy=COMPLETED_RESOURCE_POLICY, root=COMPLETED_RESOURCE_ROOT,
                resources=copy.deepcopy(COMPLETED_RESOURCE_ROOT_REFS))



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
    'tests/layout/app-navigation-contract.test.mjs',
    'tests/analytics/crm-history-browser.cjs',
    'tests/analytics/crm-history.test.cjs',

    'AgentPortal/AgentPortal.csproj',
    'AgentPortal.Tests/AgentPortal.Tests.csproj',
    'scripts/DomainReleaseRefresh/DomainReleaseRefresh.csproj',
    'AgentPortal.Tests/WebsiteContentEditorRoundTripTests.cs',
    'tests/layout/modal-content-region.test.mjs',
    'scripts/deploy-approved-app.py',
    'scripts/release-operation-evidence.py',
    '.github/workflows/deployment-diagnostics.yml',
    'scripts/test-deploy-approved-app.py',
    'scripts/test-release-operation-evidence.py',
    'scripts/release-artifacts/transport.cjs',
    'scripts/release-artifacts/test-transport.cjs',
    '.github/workflows/masterapp-platform-architecture-validation.yml',
    '.github/workflows/parfait-isolated-validation.yml',
    '.github/workflows/parfait-cutover-live-readonly.yml',
    '.github/workflows/legend-fast-track-ui.yml',
    '.github/workflows/legend-release-lifecycle.yml',
    'scripts/validation-resume.py', 'scripts/release_policy.py',
    'scripts/release-lifecycle.py', 'scripts/migration-probe-package.py',
    'scripts/test-validation-resume.py', 'scripts/test-release-policy.py',
    'scripts/test-release-lifecycle.py', 'scripts/test-fast-track-release.py',
    'scripts/test-release-children.py',
    'scripts/test-fast-track-ready-no-sql.py',
    'scripts/test-parfait-readonly-workflow.py', 'scripts/test-migration-probe-package.py',
    'scripts/release-migration.py', 'scripts/test-release-migration-probe-retry.py',
    'scripts/MigrationReleaseProbe/Program.cs',
    'scripts/MigrationReleaseProbe/CandidateContract.cs',
    'scripts/MigrationReleaseProbe/CandidateContract.Structures.cs',
    'scripts/MigrationReleaseProbe/Rehearsal/CandidateContractTests.cs',
    'scripts/MigrationReleaseProbe/Rehearsal/RehearsalFixture.cs',
    'Infrastructure/Infrastructure.csproj',
})


# The browser observer splits exact reviewed transition and rehearsal owners into
# two new source files. Only these additions may cross the nonpublishing boundary;
# candidate application/migration files and arbitrary neighboring tools remain excluded.
TRUSTED_ADDED_TOOL_PATHS = frozenset({
    'scripts/MigrationReleaseProbe/CandidateContract.Browser.cs',
    'scripts/MigrationReleaseProbe/CandidateContract.Crm.cs',
    'scripts/MigrationReleaseProbe/Rehearsal/RehearsalFixture.Browser.cs',
    'scripts/MigrationReleaseProbe/Rehearsal/RehearsalFixture.Crm.cs',
})


def tool_promotion_requested(sha):
    # Presence, including null/malformed/stale values, must never fall through
    # into normal publication. The exact validator owns positive admission.
    return 'trustedToolPromotion' in read_request(sha)


# Retire after the diagnostics feature inherits the promoted observer. These
# views are NOT tooling: only this exact, single-baseline nonpublishing repair
# may carry their already reviewed null-safe predicates through promotion.
DIAGNOSTICS_BOOTSTRAP_BASE = '27f90cb6595ac9be0be148d72791b05f0f5e74ca'
DIAGNOSTICS_BOOTSTRAP_REQUEST = {
    'requestedBy': 'Founder',
    'authorization': 'Founder requested completion of the bounded diagnostics bootstrap and combined diagnostics/client activity release. Bootstrap is nonpublishing and retains independent review, exact CI and credential-free SQL rehearsal; it authorizes no application, database or provider changes.',
    'purpose': 'One-use exact-base observer promotion with two reviewed null-safe Razor predicates; retire after verified feature integration.',
    'requestId': 'diagnostics-observer-exact-bootstrap-20261030-v1',
    'releaseMode': 'validate-only', 'targets': [],
    'cloudflareWebsiteRouting': False, 'preserveLiveTargets': False,
}
DIAGNOSTICS_BOOTSTRAP_RAZOR = {
    'SHARED/Views/Shared/ReceptionistContentReview.cshtml': (
        '7f91701a46a070bf53fddca061f69f2c036c8c02', '3bf031b67662e8fec40ba8163db7cf653124925d'),
    'SHARED/Views/Shared/ReceptionistManagement.cshtml': (
        '269c4f288509f8a1f44df67c856dbe0fdbf47617', '9597d074ffaca0cba4f8bd48a65dfb3fde2eb468'),
}
# Filled from the sealed reviewed control plus the explicit readiness/test delta.
DIAGNOSTICS_BOOTSTRAP_TOOLS = {'scripts/MigrationReleaseProbe/CandidateContract.cs': 'a8f43169cfe101cfd58d8d85b7af7977997c5800', 'scripts/MigrationReleaseProbe/Program.cs': '38952ac8cb9e17c6577e5b3c455f49c3169d8938', 'scripts/MigrationReleaseProbe/Rehearsal/CandidateContractTests.cs': 'a082b665386cced31830e659ddca1b2497730c9e', 'scripts/MigrationReleaseProbe/Rehearsal/RehearsalFixture.cs': '4055fa8172b3c84fb88c1c6ec90c9c1f1676d476', 'scripts/migration-probe-package.py': '02045754431c9beef886467c9d1797995a28f712', 'scripts/test-fast-track-release.py': 'd452e539c3c4b2c09004d339d69df99e61221793', 'scripts/test-migration-probe-package.py': 'fdc290cc62d711ee566abc6fd0e72933adb9251b', 'scripts/test-release-policy.py': 'd5afeaa2e3a9e49a98dad5f2905fedf66f28fec5', 'scripts/test-validation-resume.py': 'd81892b0e1ef01e99b0957c4095218b35a7e44ac', 'scripts/validation-resume.py': '98efc67bc15574b8068b7d4b7c59a279c005765c'}


def diagnostics_bootstrap_scope(before, revision):
    """Return an immutable zero-effect proof for this case, otherwise fail closed.

    No provider reads or writes. This is NOT source acceptance or release
    admission. The normal authenticated promotion validator still runs later.
    """
    import hashlib
    import re
    if before != DIAGNOSTICS_BOOTSTRAP_BASE:
        return None
    def git(*args):
        result = subprocess.run(['git', *args], capture_output=True, text=True, check=False)
        if result.returncode:
            raise ValueError('DIAGNOSTICS_BOOTSTRAP_SOURCE_UNAVAILABLE')
        return result.stdout
    request = read_request(revision)
    if request.get('requestId') != DIAGNOSTICS_BOOTSTRAP_REQUEST['requestId']:
        return None
    def reject():
        raise ValueError('DIAGNOSTICS_BOOTSTRAP_SCOPE_UNPROVEN')
    if (set(request) != set(DIAGNOSTICS_BOOTSTRAP_REQUEST) | {'trustedToolPromotion'}
            or any(type(request.get(k)) is not type(v) or request[k] != v
                   for k, v in DIAGNOSTICS_BOOTSTRAP_REQUEST.items())):
        reject()
    marker = request['trustedToolPromotion']
    expected_paths = set(DIAGNOSTICS_BOOTSTRAP_TOOLS) | set(DIAGNOSTICS_BOOTSTRAP_RAZOR) | {'scripts/release_policy.py'}
    if (not isinstance(marker, dict)
            or set(marker) != {'schemaVersion', 'baseRevision', 'sourceRevision', 'files'}
            or type(marker.get('schemaVersion')) is not int or marker['schemaVersion'] != 1
            or marker.get('baseRevision') != before
            or not re.fullmatch('[a-f0-9]{40}', marker.get('sourceRevision', ''))
            or not isinstance(marker.get('files'), dict)
            or set(marker['files']) != expected_paths or len(expected_paths) != 13):
        reject()
    source = marker['sourceRevision']
    # Source is a single immutable child of the one approved baseline, before
    # the request is bound. No hidden ancestry or changed application tree.
    if git('rev-list', '--parents', '-n', '1', source).split() != [source, before]:
        reject()
    def rows(ref):
        data = git('diff', '--no-renames', '--name-status', '-z', before, ref, '--').split('\0')
        if data[-1:] != [''] or len(data[:-1]) % 2:
            reject()
        pairs = list(zip(data[:-1:2], data[1:-1:2]))
        if any(status != 'M' for status, path in pairs) or len({p for _, p in pairs}) != len(pairs):
            reject()
        return {p for _, p in pairs}
    if rows(source) != expected_paths or rows(revision) != expected_paths | {REQUEST}:
        reject()
    def entry(ref, path):
        data = git('ls-tree', '-z', ref, '--', path)
        match = re.fullmatch(r'100644 blob ([a-f0-9]{40})\t' + re.escape(path) + '\0', data)
        if not match:
            reject()
        return {'mode': '100644', 'blob': match.group(1)}
    for path in expected_paths:
        original, current = entry(before, path), entry(revision, path)
        if marker['files'][path] != current or entry(source, path) != current:
            reject()
        if path in DIAGNOSTICS_BOOTSTRAP_RAZOR:
            old, new = DIAGNOSTICS_BOOTSTRAP_RAZOR[path]
            if original['blob'] != old or current['blob'] != new:
                reject()
        elif path in DIAGNOSTICS_BOOTSTRAP_TOOLS and current['blob'] != DIAGNOSTICS_BOOTSTRAP_TOOLS[path]:
            reject()
    if entry(before, REQUEST)['mode'] != entry(revision, REQUEST)['mode']:
        reject()
    proof = dict(candidate=revision, approved=before, sourceRevision=source,
                 sourceTree=git('rev-parse', source + '^{tree}').strip(),
                 paths=sorted(expected_paths | {REQUEST}),
                 productionEligible=False, authorizesMutation=False)
    return dict(proof, identity=hashlib.sha256(json.dumps(proof, sort_keys=True).encode()).hexdigest())


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
    bootstrap = diagnostics_bootstrap_scope(before, revision)
    permitted = TRUSTED_TOOL_PATHS | TRUSTED_ADDED_TOOL_PATHS
    if bootstrap is not None:
        permitted = permitted | set(DIAGNOSTICS_BOOTSTRAP_RAZOR)
    if (not isinstance(files, dict) or not files or not set(files) <= permitted
        or any(status != 'M' and not (status == 'A' and path in TRUSTED_ADDED_TOOL_PATHS) for status, path in rows)
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
            or expected != entry(revision, path) or expected != entry(value['sourceRevision'], path)):
            raise ValueError('TRUSTED_TOOL_PROMOTION_BLOB_UNPROVEN')
        if dict((path, status) for status, path in rows)[path] == 'A':
            # Positive absence at the exact first parent, not a failed tree read.
            # No executable, symlink, rename, overwrite or mode change is an addition.
            if expected['mode'] != '100644' or git('ls-tree', '-z', before, '--', path) != '':
                raise ValueError('TRUSTED_TOOL_PROMOTION_ADDITION_UNPROVEN')
        elif expected['mode'] != entry(before, path)['mode']:
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
    # Pin the accepted mobile harness correction; never admit arbitrary test changes.
    mobile_fixture = 'tests/layout/modal-content-region.test.mjs'
    if mobile_fixture in files and files[mobile_fixture]['blob'] != 'c21b130bda415433686f01ece3111afe52aa217a':
        raise ValueError('TRUSTED_TOOL_PROMOTION_MOBILE_FIXTURE_SCOPE_UNPROVEN')
    # Exact reviewed validation corrections; arbitrary test changes remain denied.
    validation_fixtures = {'tests/layout/app-navigation-contract.test.mjs': '57c103161f271a5a00410180a99d4454032a5cef', 'tests/analytics/crm-history-browser.cjs': '4cfbb35f1286552d9c6c8e76a82c68ac0389f46c', 'tests/analytics/crm-history.test.cjs': '95d2f8ec9fb84d61b959581d5eeeb5664920f462'}
    for path, blob in validation_fixtures.items():
        if path in files and files[path]['blob'] != blob:
            raise ValueError('TRUSTED_TOOL_PROMOTION_VALIDATION_FIXTURE_SCOPE_UNPROVEN')
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
