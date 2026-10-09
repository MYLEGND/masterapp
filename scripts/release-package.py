#!/usr/bin/env python3
"""Canonical immutable release-package authority.

Validation builds one exact package set for a validated application revision.
Release may only restore and verify those bytes; it never rebuilds a supposedly
equivalent package. Deployment authorization remains in the approved release
workflow and is intentionally separate from package identity.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import zipfile

SCHEMA = "legend-validated-release-package.v1"
ROOT = Path(__file__).resolve().parents[1]
def _release_authority_module():
    path = Path(__file__).with_name("validation-resume.py")
    spec = importlib.util.spec_from_file_location("validation_resume_authority", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_RELEASE_AUTHORITY = _release_authority_module()
APPS = {
    key: (row["project"], row["package"], row["static"])
    for key, row in _RELEASE_AUTHORITY.RELEASE_TARGETS.items()
}
MIGRATION_BUNDLE = _RELEASE_AUTHORITY.MIGRATION_BUNDLE_NAME
# Consume the canonical builder/toolchain inputs. Deployment reconciliation is a
# consumer of the bytes and cannot invalidate their immutable package contract.
CONTRACT_INPUTS = tuple(sorted(_RELEASE_AUTHORITY.PACKAGE_AUTHORITY_PATHS))


def sha256_file(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def normalize_revision(value: str) -> str:
    if not re.fullmatch(r"[0-9a-f]{40}", value or ""):
        raise ValueError("Expected exact lowercase Git commit SHA")
    return value


def validate_revision(value: str) -> str:
    value = normalize_revision(value)
    subprocess.run(["git", "cat-file", "-e", value + "^{commit}"], cwd=ROOT, check=True)
    return value


def contract_hash() -> str:
    digest = hashlib.sha256()
    digest.update(
        json.dumps({"apps": APPS, "staticSourceRoots": {key: row["sourceRoot"] for key, row in _RELEASE_AUTHORITY.RELEASE_TARGETS.items() if row["static"]}, "migrationBundle": MIGRATION_BUNDLE}, sort_keys=True, separators=(",", ":")).encode()
    )
    digest.update(b"\0")
    for relative in CONTRACT_INPUTS:
        path = ROOT / relative
        if not path.exists():
            raise FileNotFoundError(relative)
        digest.update(relative.encode() + b"\0")
        if relative == _RELEASE_AUTHORITY.PACKAGE_BUILD_WORKFLOW:
            digest.update(_RELEASE_AUTHORITY.package_builder_workflow_contract(path.read_text()).encode())
        else:
            digest.update(path.read_bytes())
        digest.update(b"\0")
    return digest.hexdigest()


def package_identity(revision: str) -> str:
    revision = normalize_revision(revision)
    payload = json.dumps(
        {
            "schema": SCHEMA,
            "applicationReleaseSha": revision,
            "packageContractSha256": contract_hash(),
        },
        sort_keys=True,
        separators=(",", ":"),
    ).encode()
    return hashlib.sha256(payload).hexdigest()


def artifact_name(revision: str) -> str:
    return "founder-diagnostics-packages-" + package_identity(revision)


def run(*args, env=None):
    subprocess.run(list(args), cwd=ROOT, env=env, check=True)


def zip_directory(source: Path, destination: Path):
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(source.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(source))


PACKAGE_TOOL_IMAGES = {
    'sdk': 'mcr.microsoft.com/dotnet/sdk@sha256:0eeb52c76e35a5431ca707ad2bc75e38006a05393045d8532ae44c15d9474523',
    'node': 'node@sha256:9af606de2e947cfb3f276633b7c5130c4cd22365aa10f5a647b7675d5cfce0c0',
    'python': 'python@sha256:edd0b3ec946cc68bd20e39480bd03929016d0fb6254cbf870cd3bc55ea25116d',
}



# Only the canonical compilers are available. Node's unrelated yarn aliases point
# outside /usr/local and are deliberately not part of the npm execution contract.
PACKAGE_TOOL_PATHS = {
    'node': ('bin/node', 'bin/npm', 'bin/npx', 'lib/node_modules/npm'),
    'python': ('bin/python3', 'bin/python3.12', 'lib'),
}


def materialize_component_source(revision, stage):
    """Copy exact Git objects into an empty stage; never copy host checkout state.

    The minimal Git metadata contains only this commit identity. It has no remote,
    hooks, credentials, alternates or parent history. Unsupported links stop before
    candidate execution rather than traversing outside the source boundary.
    """
    import tempfile
    import zlib
    from pathlib import PurePosixPath
    revision = validate_revision(revision)
    stage = Path(stage).resolve()
    source = stage / 'source'
    if source.exists():
        raise ValueError('Candidate materialization requires an empty source stage')
    raw = subprocess.check_output(['git', 'ls-tree', '-rzl', revision], cwd=ROOT, timeout=60)
    if len(raw) > 16 * 1024 * 1024:
        raise ValueError('Candidate source inventory exceeds package budget')
    rows, names, total = [], set(), 0
    for item in raw.split(b'\0'):
        if not item:
            continue
        header, name = item.split(b'\t', 1)
        mode, kind, oid, size = header.decode().split()
        name = name.decode('utf-8')
        path = PurePosixPath(name)
        if (mode not in {'100644', '100755'} or kind != 'blob' or
                not re.fullmatch(r'[a-f0-9]{40}', oid) or path.is_absolute() or
                any(part.casefold() in {'', '.', '..', '.git'} for part in name.split('/')) or
                '\\' in name or name.casefold() in names):
            raise ValueError('Candidate source path or object kind unsupported')
        size = int(size)
        total += size
        if size < 0 or size > 512 * 1024 * 1024 or total > 2 * 1024 * 1024 * 1024 or len(rows) >= 100000:
            raise ValueError('Candidate source material exceeds package budget')
        names.add(name.casefold())
        rows.append((oid, size, mode, name))
    source.mkdir()
    # Keep batch transport on disk so repository size does not become RAM usage.
    with tempfile.TemporaryFile() as queries, tempfile.TemporaryFile() as objects:
        queries.write(''.join(row[0] + '\n' for row in rows).encode())
        queries.seek(0)
        subprocess.run(['git', 'cat-file', '--batch'], cwd=ROOT, stdin=queries,
                       stdout=objects, check=True, timeout=90)
        objects.seek(0)
        for oid, size, mode, name in rows:
            if objects.readline() != f'{oid} blob {size}\n'.encode():
                raise ValueError('Candidate source object identity mismatch')
            destination = source / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            digest = hashlib.sha1(f'blob {size}\0'.encode())
            with destination.open('xb') as output:
                remaining = size
                while remaining:
                    block = objects.read(min(remaining, 1024 * 1024))
                    if not block:
                        raise ValueError('Candidate source object truncated')
                    output.write(block)
                    digest.update(block)
                    remaining -= len(block)
            if objects.read(1) != b'\n' or digest.hexdigest() != oid:
                raise ValueError('Candidate source object content mismatch')
            destination.chmod(0o755 if mode == '100755' else 0o644)
        if objects.read(1):
            raise ValueError('Unexpected candidate source material')
    commit = subprocess.check_output(['git', 'cat-file', 'commit', revision], cwd=ROOT, timeout=30)
    encoded = f'commit {len(commit)}\0'.encode() + commit
    if hashlib.sha1(encoded).hexdigest() != revision:
        raise ValueError('Candidate commit identity mismatch')
    metadata = source / '.git'
    (metadata / 'objects' / revision[:2]).mkdir(parents=True)
    (metadata / 'refs').mkdir()
    (metadata / 'objects' / revision[:2] / revision[2:]).write_bytes(zlib.compress(encoded))
    (metadata / 'HEAD').write_text(revision + '\n')
    (metadata / 'shallow').write_text(revision + '\n')
    (metadata / 'config').write_text('[core]\n\trepositoryformatversion = 0\n\tbare = false\n')
    return dict(revision=revision, files=len(rows), bytes=total)


def verified_tool_tree(root, *, deadline):
    """Bounded tool-input identity; do not walk arbitrary host installations."""
    import stat
    import time
    root = Path(root).resolve()
    rows, count, total = [], 0, 0
    pending = [root]
    while pending:
        directory = pending.pop()
        with os.scandir(directory) as entries:
            for entry in entries:
                count += 1
                if count > 100000 or time.monotonic() >= deadline:
                    raise ValueError('Pinned tool inventory budget exceeded')
                path = Path(entry.path)
                metadata = path.lstat()
                row = dict(path=str(path.relative_to(root)), mode=stat.S_IMODE(metadata.st_mode),
                           uid=metadata.st_uid, gid=metadata.st_gid)
                if stat.S_ISLNK(metadata.st_mode):
                    target = os.readlink(path)
                    try:
                        contained = path.resolve(strict=True).is_relative_to(root)
                    except (OSError, RuntimeError):
                        contained = False
                    if not contained:
                        raise ValueError('Pinned tool link escapes verified tree')
                    row.update(kind='link', target=target)
                elif stat.S_ISDIR(metadata.st_mode):
                    row['kind'] = 'directory'
                    pending.append(path)
                elif stat.S_ISREG(metadata.st_mode):
                    digest = hashlib.sha256()
                    with path.open('rb') as source:
                        while block := source.read(1024 * 1024):
                            total += len(block)
                            if total > 2 * 1024 * 1024 * 1024 or time.monotonic() >= deadline:
                                raise ValueError('Pinned tool content budget exceeded')
                            digest.update(block)
                    row.update(kind='file', sha256=digest.hexdigest())
                else:
                    raise ValueError('Pinned tool special file unsupported')
                os.utime(path, (0, 0), follow_symlinks=False)
                rows.append(row)
    root.chmod(0o755)
    os.utime(root, (0, 0))
    root_metadata = root.stat()
    material = dict(schemaVersion=1, normalization='mtime-atime-zero-root-mode755',
        rootOwner=[root_metadata.st_uid, root_metadata.st_gid],
        entries=sorted(rows, key=lambda row: row['path']))
    return dict(identity=hashlib.sha256(json.dumps(material, sort_keys=True, separators=(',', ':')).encode()).hexdigest(),
                entries=count, bytes=total)


def select_tool_inputs(root, paths, *, deadline):
    """Retain only the declared compiler paths inside a fresh owned extraction."""
    import time
    root = Path(root)
    count, size = 0, 0
    def visit(directory):
        nonlocal count, size
        with os.scandir(directory) as entries:
            for entry in entries:
                count += 1
                size += entry.stat(follow_symlinks=False).st_size
                if count > 100000 or size > 2 * 1024 * 1024 * 1024 or time.monotonic() >= deadline:
                    raise ValueError('Pinned tool selection budget exceeded')
                path = Path(entry.path)
                relative = str(path.relative_to(root))
                keep = any(relative == value or relative.startswith(value + '/') or
                           value.startswith(relative + '/') for value in paths)
                if entry.is_dir(follow_symlinks=False):
                    visit(path)
                    if not keep:
                        path.rmdir()
                elif not keep:
                    path.unlink()
    visit(root)
    if any(not (root / value).exists() for value in paths):
        raise ValueError('Pinned compiler input missing from image')


def prepare_isolated_tools(stage, *, timeout=240):
    """Extract declared tools from authenticated image objects, never host PATH.

    Containers are created but never started. This creates no published image,
    registry, candidate execution, or production authority. The output is private
    to this build stage and mounted readonly for every candidate process.
    """
    import time
    stage = Path(stage).resolve()
    tool_root = stage / 'tools'
    tool_root.mkdir(mode=0o700)  # Exclusive creation; ambiguous partial state stops.
    deadline = time.monotonic() + timeout
    def remaining():
        value = deadline - time.monotonic()
        if value <= 0:
            raise ValueError('Pinned tool materialization deadline exceeded')
        return value
    def capture(*args):
        completed = subprocess.run(list(args), check=True, text=True,
            capture_output=True, timeout=remaining())
        if len(completed.stdout) > 1024 * 1024:
            raise ValueError('Pinned tool provider response exceeds budget')
        return completed.stdout
    proofs = {}
    for kind, image in PACKAGE_TOOL_IMAGES.items():
        observed = subprocess.run(['docker', 'image', 'inspect', '--platform', 'linux/amd64', image],
            text=True, capture_output=True, timeout=remaining())
        if observed.returncode:
            subprocess.run(['docker', 'pull', '--platform', 'linux/amd64', image],
                check=True, timeout=remaining())
            records = json.loads(capture('docker', 'image', 'inspect', '--platform', 'linux/amd64', image))
        else:
            if len(observed.stdout) > 1024 * 1024:
                raise ValueError('Pinned tool provider response exceeds budget')
            records = json.loads(observed.stdout)
        if (not isinstance(records, list) or len(records) != 1 or
                records[0].get('Os') != 'linux' or records[0].get('Architecture') != 'amd64' or
                not re.fullmatch(r'sha256:[a-f0-9]{64}', records[0].get('Id', ''))):
            raise ValueError('Pinned tool platform or image identity unproven')
        image_id = records[0]['Id']
        proofs[kind] = dict(image=image, imageId=image_id)
        if kind == 'sdk':
            continue
        identity = capture('docker', 'create', '--platform', 'linux/amd64', image).strip()
        if not re.fullmatch(r'[a-f0-9]{64}', identity):
            raise ValueError('Pinned tool extraction container identity unproven')
        try:
            records = json.loads(capture('docker', 'container', 'inspect', identity))
            if len(records) != 1 or records[0].get('Image') != image_id or records[0].get('State', {}).get('Running'):
                raise ValueError('Pinned tool extraction image mismatch')
            destination = tool_root / kind
            if destination.exists():
                raise ValueError('Pinned tool extraction destination already exists')
            subprocess.run(['docker', 'cp', identity + ':/usr/local', str(destination)],
                check=True, timeout=remaining())
            select_tool_inputs(destination, PACKAGE_TOOL_PATHS[kind], deadline=deadline)
        finally:
            # Cleanup has one explicit 30-second reserve, no retry multiplication.
            subprocess.run(['docker', 'rm', identity], check=True, capture_output=True, timeout=30)
        proofs[kind]['paths'] = list(PACKAGE_TOOL_PATHS[kind])
        proofs[kind]['tree'] = verified_tool_tree(destination, deadline=deadline)
    return dict(schemaVersion=1, platform='linux/amd64', tools=proofs)


def isolated_component_command(component, stage, argv, *, phase):
    """One package owner's isolated execution envelope; never a deployment path.

    The caller materializes the exact candidate and verified tools into its owned
    stage. Restore has dependency-provider access but no credentials. Publication
    has no network and receives immutable source, dependency caches and tools.
    node_modules is disposable output of offline npm ci, not a dependency input.
    This function does not authenticate candidate-controlled metadata or receipts.
    """
    if component not in APPS or phase not in {'bootstrap', 'restore', 'publish', 'observe'}:
        raise ValueError('Unsupported isolated package execution boundary')
    if not argv or any(not isinstance(value, str) or '\0' in value for value in argv):
        raise ValueError('Invalid package execution arguments')
    stage = Path(stage).resolve()
    if any(value in str(stage) for value in (',', '\n', '\r')):
        raise ValueError('Invalid package stage path')
    command = ['docker', 'run', '--rm', '--platform', 'linux/amd64',
               '--user', str(os.getuid()) + ':' + str(os.getgid()), '--read-only',
               '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
               '--pids-limit', '256', '--cpus', '2', '--memory', '6g',
               '--network', 'bridge' if phase == 'restore' else 'none',
               '--tmpfs', '/tmp:rw,nosuid,nodev,size=1g',
               '--tmpfs', '/root:rw,nosuid,nodev,size=64m', '--workdir', '/src']
    mounts = [('source', '/src', False), ('tools/node', '/tools/node', False),
              ('tools/python', '/tools/python', False),
              ('authority', '/authority', False), ('probe', '/probe', phase == 'bootstrap'),
              ('nuget', '/deps/nuget', phase == 'restore'),
              ('artifacts', '/tmp/masterapp', phase in {'restore', 'publish'})]
    if component in {'protect', 'website'}:
        mounts.extend([('npm', '/deps/npm', phase == 'restore'),
                       ('website-workspace', '/src/Legend-Website', phase in {'restore', 'publish'})])
        # The compiler deletes/recreates dist and npm ci recreates node_modules.
        # Their parent is scratch; every committed top-level input stays readonly.
        website = stage / 'source/Legend-Website'
        if not website.is_dir() or website.is_symlink():
            raise ValueError('Static compiler source missing')
        for child in sorted(website.iterdir()):
            if child.name in {'dist', 'node_modules'}:
                raise ValueError('Generated website output cannot be a committed input')
            mounts.append(('source/Legend-Website/' + child.name, '/src/Legend-Website/' + child.name, False))
    for relative, target, writable in mounts:
        path = stage / relative
        if (not (path.is_dir() or path.is_file()) or path.is_symlink() or
                not path.resolve().is_relative_to(stage) or any(value in str(path) for value in (',', '\n', '\r'))):
            raise ValueError('Package input mount missing or outside owned stage')
        command.extend(['--mount', 'type=bind,src=' + str(path) + ',dst=' + target + ('' if writable else ',readonly')])
    environment = dict(HOME='/tmp/home', PATH='/tools/python/bin:/tools/node/bin:/usr/share/dotnet:/usr/bin:/bin',
        PYTHONHOME='/tools/python', LD_LIBRARY_PATH='/tools/python/lib',
        PYTHONDONTWRITEBYTECODE='1', DOTNET_CLI_HOME='/tmp/dotnet',
        DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1', DOTNET_PROCESSOR_COUNT='2',
        DOTNET_CLI_USE_MSBUILD_SERVER='0', MSBUILDDISABLENODEREUSE='1',
        NUGET_PACKAGES='/deps/nuget', npm_config_cache='/deps/npm',
        npm_config_offline='false' if phase == 'restore' else 'true',
        npm_config_audit='false', npm_config_fund='false', npm_config_logs_max='0',
        LANG='C.UTF-8', TZ='UTC')
    for key, value in sorted(environment.items()):
        command.extend(['--env', key + '=' + value])
    command.extend([PACKAGE_TOOL_IMAGES['sdk'], *argv])
    return command


def prepare_website_workspace(component, stage, *, phase):
    """Generated website files never carry from restore into compilation."""
    if component not in APPS or phase not in {'bootstrap', 'restore', 'publish', 'observe'}:
        raise ValueError('Unsupported isolated package execution boundary')
    if component not in {'website', 'protect'} or phase in {'bootstrap', 'observe'}:
        return
    stage = Path(stage).resolve()
    source = stage / 'source/Legend-Website'
    scratch = stage / 'website-workspace'
    if not source.is_dir() or source.is_symlink() or scratch.is_symlink():
        raise ValueError('Unsafe website source or scratch boundary')
    if scratch.exists():
        shutil.rmtree(scratch)
    scratch.mkdir()
    for child in source.iterdir():
        if child.is_symlink() or child.name in {'dist', 'node_modules'}:
            raise ValueError('Unsupported website source input')
        # Docker Desktop requires existing placeholders for nested readonly binds.
        destination = scratch / child.name
        if child.is_dir():
            destination.mkdir()
        else:
            destination.touch()


def run_isolated_component(component, stage, argv, *, phase, timeout):
    """Bound one candidate process and reclaim only its own disposable container.

    There are no production writes in this boundary. A timeout never retries the
    build; logs and generated files remain for diagnosis. Provider mutation retry
    policy continues to belong to the production operation journal.
    """
    import signal
    import threading
    import uuid
    if not isinstance(timeout, (int, float)) or not 0 < timeout <= 1200:
        raise ValueError('Invalid isolated component execution deadline')
    stage = Path(stage).resolve()
    prepare_website_workspace(component, stage, phase=phase)
    invocation = uuid.uuid4().hex
    cidfile = stage / (phase + '-' + invocation + '.container-id')
    log = stage / (phase + '-' + invocation + '.log')
    command = isolated_component_command(component, stage, argv, phase=phase)
    command[2:2] = ['--cidfile', str(cidfile), '--label', 'legend.package.invocation=' + invocation]
    previous = None
    if threading.current_thread() is threading.main_thread():
        previous = signal.getsignal(signal.SIGTERM)
        def interrupted(_signal, _frame):
            raise KeyboardInterrupt('Isolated package worker interrupted')
        signal.signal(signal.SIGTERM, interrupted)
    failed = False
    try:
        with log.open('x') as output:
            subprocess.run(command, stdout=output, stderr=subprocess.STDOUT,
                           check=True, timeout=timeout)
    except BaseException:
        failed = True
        raise
    finally:
        if previous is not None:
            signal.signal(signal.SIGTERM, previous)
        # The candidate cannot access this host-side identity file. Exact IDs
        # avoid cleanup by name, label scan, or stopping an unrelated worker.
        if failed and cidfile.exists():
            identity = cidfile.read_text().strip()
            if re.fullmatch(r'[a-f0-9]{64}', identity):
                try:
                    completed = subprocess.run(['docker', 'rm', '--force', identity],
                        capture_output=True, text=True, timeout=30)
                    cleanup = dict(containerId=identity, attempted=True, removed=completed.returncode == 0)
                except (subprocess.SubprocessError, OSError):
                    cleanup = dict(containerId=identity, attempted=True, removed=False)
                (stage / (phase + '-' + invocation + '.cleanup.json')).write_text(json.dumps(cleanup) + '\n')
    return log


RESTORE_MATERIAL_LIMITS = dict(projects=128, packages=1000, entries=100000,
    fileBytes=512 * 1024 * 1024, totalBytes=2 * 1024 * 1024 * 1024, seconds=120)


def resolved_restore_identity(component, artifacts_root, package_roots, sdk_root, *, content_hash_probe=None):
    """Fingerprint verified restore material; does not resolve, build, or authorize reuse.

    Callers must first restore the desired graph without production credentials,
    then publish with --no-restore and verify this identity again. SDK/runner and
    source identities remain separate required inputs. No raw paths/feed values
    are emitted. Unknown restore formats or missing material fail closed.
    """
    import base64
    import stat
    import time
    from pathlib import PurePosixPath
    if component not in APPS or APPS[component][2]:
        raise ValueError('Resolved .NET application component required')
    roots = tuple(Path(path).resolve() for path in package_roots)
    if not roots:
        raise ValueError('Explicit trusted package cache roots required')
    sdk_root = Path(sdk_root).resolve()
    repository = ROOT.resolve()
    visited, packages, projects, library_records = set(), {}, {}, {}
    signed_archives = {}
    artifacts_root = Path(artifacts_root).resolve()
    deadline = time.monotonic() + RESTORE_MATERIAL_LIMITS['seconds']
    consumed, entries_seen = 0, 0

    def check_budget():
        if time.monotonic() >= deadline:
            raise ValueError('Restore material verification deadline exceeded')

    def digest_stream(stream, algorithm):
        nonlocal consumed
        digest = hashlib.new(algorithm)
        file_bytes = 0
        while True:
            check_budget()
            block = stream.read(1024 * 1024)
            if not block:
                return digest
            file_bytes += len(block)
            consumed += len(block)
            if file_bytes > RESTORE_MATERIAL_LIMITS['fileBytes'] or consumed > RESTORE_MATERIAL_LIMITS['totalBytes']:
                raise ValueError('Restore material byte budget exceeded')
            digest.update(block)

    def regular_file(path):
        check_budget()
        if path.is_symlink() or not stat.S_ISREG(path.stat().st_mode):
            raise ValueError('Restore material must be a regular file')
        if path.stat().st_size > RESTORE_MATERIAL_LIMITS['fileBytes']:
            raise ValueError('Restore material file budget exceeded')
        return path

    def file_hash(path):
        with regular_file(path).open('rb') as stream:
            return digest_stream(stream, 'sha256').hexdigest()


    def bounded_path(base, relative):
        name = PurePosixPath(relative)
        if name.is_absolute() or '..' in name.parts or '\\' in relative:
            raise ValueError('Restore material path escapes owning root')
        value = base.joinpath(*name.parts)
        if not value.resolve().is_relative_to(base):
            raise ValueError('Restore material symlink escapes owning root')
        return value

    def package_material(identity, library):
        nonlocal entries_seen
        check_budget()
        if identity in packages:
            if library_records[identity] != library:
                raise ValueError('Conflicting restored package content')
            return
        if len(packages) >= RESTORE_MATERIAL_LIMITS['packages']:
            raise ValueError('Restore material package budget exceeded')
        expected = library.get('sha512', '')
        try:
            if len(base64.b64decode(expected, validate=True)) != 64:
                raise ValueError('Invalid restored package digest')
        except (ValueError, TypeError) as exc:
            raise ValueError('Invalid restored package digest') from exc
        parts = identity.split('/')
        if len(parts) != 2 or not all(re.fullmatch(r'[A-Za-z0-9_.+-]+', part) for part in parts):
            raise ValueError('Invalid restored package identity')
        relative = library.get('path')
        if relative != identity.lower():
            raise ValueError('Restored package path does not match identity')
        candidates = [bounded_path(root, relative) for root in roots]
        present = [path for path in candidates if path.is_dir()]
        if len(present) != 1:
            raise ValueError('Restored package cache identity missing or ambiguous')
        directory = present[0]
        archive_path = bounded_path(directory, '.'.join(parts).lower() + '.nupkg')
        with regular_file(archive_path).open('rb') as stream:
            actual = base64.b64encode(digest_stream(stream, 'sha512').digest()).decode()
        if actual != expected:
            with zipfile.ZipFile(archive_path) as candidate_archive:
                signed = '.signature.p7s' in candidate_archive.namelist()
            if not signed:
                raise ValueError('Restored package archive digest mismatch')
            if content_hash_probe is None:
                raise ValueError('Signed restore package content verifier unavailable')
            signed_archives[str(archive_path)] = expected
        files = library.get('files')
        if (not isinstance(files, list) or not files or any(not isinstance(name, str) for name in files)
            or len(files) > RESTORE_MATERIAL_LIMITS['entries'] or len(files) != len(set(files))):
            raise ValueError('Restored package file inventory invalid')
        disk_files = set()
        for path in directory.rglob('*'):
            check_budget()
            if path.is_dir():
                if path.is_symlink():
                    raise ValueError('Restore package directory symlink unsupported')
                continue
            regular_file(path)
            disk_files.add(str(path.relative_to(directory)))
            if len(disk_files) > RESTORE_MATERIAL_LIMITS['entries']:
                raise ValueError('Restore material entry budget exceeded')
        generated = {archive_path.name, archive_path.name + '.sha512', '.nupkg.metadata'}
        if disk_files - generated != set(files) - generated:
            raise ValueError('Extracted package inventory differs from restore graph')
        with zipfile.ZipFile(archive_path) as archive:
            entries = {}
            for info in archive.infolist():
                check_budget()
                entries_seen += 1
                if entries_seen > RESTORE_MATERIAL_LIMITS['entries']:
                    raise ValueError('Restore material entry budget exceeded')
                bounded_path(directory, info.filename)
                if info.file_size > RESTORE_MATERIAL_LIMITS['fileBytes']:
                    raise ValueError('Restore material file budget exceeded')
                if info.is_dir():
                    continue
                key = info.filename.casefold()
                if key in entries:
                    raise ValueError('Restored archive file identity ambiguous')
                entries[key] = info.filename
            for name in files:
                path = bounded_path(directory, name)
                if name in {'.nupkg.metadata', archive_path.name + '.sha512'}:
                    continue  # NuGet generated metadata; archive bytes checked above.
                entry = entries.get(name.casefold())
                if entry is None:
                    raise ValueError('Restored package file absent from verified archive')
                with archive.open(entry) as stream:
                    expected_file = digest_stream(stream, 'sha256').hexdigest()
                if file_hash(path) != expected_file:
                    raise ValueError('Extracted package content differs from verified archive')
        generated_metadata = {name: file_hash(directory / name)
            for name in sorted(generated - {archive_path.name}) if (directory / name).exists()}
        packages[identity] = dict(sha512=expected, archiveSha512=actual, files=sorted(files),
                                  generatedMetadata=generated_metadata)
        library_records[identity] = library

    def project_material(project):
        check_budget()
        project = Path(project).resolve()
        if not project.is_relative_to(repository) or project.suffix != '.csproj':
            raise ValueError('Restore project outside candidate repository')
        if project in visited:
            return
        if len(visited) >= RESTORE_MATERIAL_LIMITS['projects']:
            raise ValueError('Restore material project budget exceeded')
        visited.add(project)
        path = bounded_path(artifacts_root, 'obj/' + project.stem + '/project.assets.json')
        regular_file(path)
        if path.stat().st_size > 32 * 1024 * 1024:
            raise ValueError('Oversized restore material')
        file_hash(path)  # Count bounded metadata reads against the shared budget.
        data = json.loads(path.read_text())
        # SDK 10.0.300+ uses NuGet assets v4 with framework aliases. Keep
        # every target/framework key; never collapse distinct alias graphs.
        if (type(data.get('version')) is not int or data['version'] not in {3, 4} or
                not re.fullmatch(r'[A-Za-z0-9.+-]{1,128}', data.get('project', {}).get('version', ''))):
            raise ValueError('Unsupported restore material schema')
        if set(Path(value).resolve() for value in data.get('packageFolders', {})) != set(roots):
            raise ValueError('Restore package search roots differ from verified caches')
        restore = data['project']['restore']
        if Path(restore['projectPath']).resolve() != project:
            raise ValueError('Restore material belongs to different project')
        frameworks = data['project']['frameworks']
        # Record content of the actual runtime graph, never its machine path.
        for framework in frameworks.values():
            graph = framework.get('runtimeIdentifierGraphPath')
            if graph:
                graph_path = Path(graph).resolve()
                if not graph_path.is_relative_to(sdk_root):
                    raise ValueError('Runtime graph outside measured SDK')
                framework['runtimeIdentifierGraphPath'] = {
                    'sdkRelativePath': str(graph_path.relative_to(sdk_root)),
                    'sha256': file_hash(graph_path)}
        references = set()
        for framework in restore['frameworks'].values():
            for reference, metadata in framework.get('projectReferences', {}).items():
                if Path(reference).resolve() != Path(metadata['projectPath']).resolve():
                    raise ValueError('Conflicting restored project reference')
                child = Path(reference).resolve()
                project_material(child)
                references.add(str(child.relative_to(repository)))
        libraries = {}
        for identity, library in data['libraries'].items():
            if library.get('type') == 'package':
                package_material(identity, library)
                libraries[identity] = {'type': 'package', 'sha512': library['sha512']}
            elif library.get('type') == 'project':
                child = (project.parent / library['msbuildProject']).resolve()
                project_material(child)
                libraries[identity] = {'type': 'project', 'project': str(child.relative_to(repository))}
            else:
                raise ValueError('Unsupported restored dependency kind')
        for target in data['targets'].values():
            for identity, assets in target.items():
                if identity not in data['libraries']:
                    raise ValueError('Resolved target dependency absent from library inventory')
                if libraries[identity]['type'] != 'package':
                    continue
                allowed = set(data['libraries'][identity]['files'])
                for kind in ('compile', 'runtime', 'native', 'resource', 'contentFiles', 'build',
                             'buildMultiTargeting', 'buildTransitive', 'analyzers', 'runtimeTargets'):
                    for name in assets.get(kind, {}):
                        bounded_path(repository, name)
                        # NuGet's PackageEmptyFileName denotes a synthetic empty
                        # asset group, not a compiler input requiring extraction.
                        if name not in allowed and PurePosixPath(name).name != '_._':
                            raise ValueError('Resolved compiler input absent from verified package inventory')
        generated = {}
        for suffix in ('.nuget.g.props', '.nuget.g.targets'):
            generated_path = bounded_path(artifacts_root, 'obj/' + project.stem + '/' + project.name + suffix)
            file_hash(generated_path)
            text = generated_path.read_text()
            substitutions = [(str(repository), '$REPOSITORY'), (str(artifacts_root), '$ARTIFACTS'),
                             (str(sdk_root), '$SDK')] + [(str(root), '$PACKAGES' + str(index)) for index, root in enumerate(roots)]
            for prefix, replacement in sorted(substitutions, key=lambda item: len(item[0]), reverse=True):
                text = text.replace(prefix, replacement)
            generated[suffix] = hashlib.sha256(text.encode()).hexdigest()
        projects[str(project.relative_to(repository))] = dict(
            restoreSchemaVersion=data['version'], projectVersion=data['project']['version'],
            frameworks=frameworks, targets=data['targets'], libraries=libraries,
            projectReferences=sorted(references), generatedImports=generated)

    project_material(repository / APPS[component][0])
    expected_cache = {identity.lower() for identity in packages}
    for root in roots:
        for package in root.iterdir():
            check_budget()
            if (not package.is_dir() or package.is_symlink() or
                    not any(value.startswith(package.name + '/') for value in expected_cache)):
                raise ValueError('Unmeasured input in component package cache')
            for version in package.iterdir():
                check_budget()
                if not version.is_dir() or version.is_symlink() or package.name + '/' + version.name not in expected_cache:
                    raise ValueError('Unmeasured input in component package cache')
    if signed_archives:
        archives = sorted(signed_archives)
        completed = subprocess.run(['dotnet', str(content_hash_probe)], input=json.dumps(archives),
                                   text=True, capture_output=True, timeout=max(0.001, deadline - time.monotonic()), check=False)
        if completed.returncode or len(completed.stdout) > 256 * 1024:
            raise ValueError('Signed restore package integrity unproven')
        result = json.loads(completed.stdout)
        expected_rows = [dict(index=index, contentHash=signed_archives[path]) for index, path in enumerate(archives)]
        if result != dict(schemaVersion=1, packages=expected_rows):
            raise ValueError('Signed restore package content mismatch')
    check_budget()
    material = dict(schemaVersion=1, component=component, projects=projects, packages=packages)
    identity = hashlib.sha256(json.dumps(material, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    return dict(schemaVersion=1, identity=identity, projectCount=len(projects), packageCount=len(packages),
                projectPaths=sorted(projects))


def narrow_restore_cache(stage):
    """Reduce visible restore cache; the full verifier still admits every input.

    NuGet downloads superseded versions while resolving. They are diagnostic
    evidence, not compiler dependencies. Never mount that excluded inventory.
    """
    import stat
    import time
    stage = Path(stage).resolve()
    cache, obj = stage / 'nuget', stage / 'artifacts/obj'
    deadline, count, size = time.monotonic() + 120, 0, 0
    def bounded_tree(root):
        nonlocal count, size
        pending = [root]
        while pending:
            path = pending.pop()
            info = path.lstat()
            count += 1
            if count > 100000 or time.monotonic() > deadline:
                raise ValueError('Restore selection inventory budget exceeded')
            if stat.S_ISDIR(info.st_mode):
                pending.extend(path.iterdir())
            elif stat.S_ISREG(info.st_mode):
                size += info.st_size
                if info.st_size > 512 * 1024 * 1024 or size > 2 * 1024 ** 3:
                    raise ValueError('Restore selection byte budget exceeded')
            else:
                raise ValueError('Unsafe restore selection entry')
    bounded_tree(cache)
    bounded_tree(obj)
    assets = sorted(obj.glob('*/project.assets.json'))
    if not 0 < len(assets) <= 128:
        raise ValueError('Restore selection project inventory invalid')
    selected = set()
    for path in assets:
        if path.stat().st_size > 32 * 1024 * 1024:
            raise ValueError('Restore selection metadata oversized')
        data = json.loads(path.read_text())
        if type(data.get('version')) is not int or data['version'] not in {3, 4}:
            raise ValueError('Restore selection schema unsupported')
        for identity, library in data['libraries'].items():
            if library.get('type') != 'package':
                continue
            relative = library.get('path')
            if (not isinstance(relative, str) or relative != identity.lower() or
                    not re.fullmatch(r'[a-z0-9_+-][a-z0-9_.+-]*/[a-z0-9_+-][a-z0-9_.+-]*', relative)):
                raise ValueError('Unsafe restore selection package path')
            if not (cache / relative).is_dir():
                raise ValueError('Restore selection package missing')
            selected.add(relative)
            if len(selected) > 1000:
                raise ValueError('Restore selection package budget exceeded')
    excluded = sorted(str(path.relative_to(cache)) for path in cache.glob('*/*')
                      if str(path.relative_to(cache)) not in selected)
    observation = stage / 'restore-cache-observation'
    if observation.exists():
        raise ValueError('Restore selection already recorded; reconcile existing stage')
    cache.rename(observation)
    cache.mkdir()
    for relative in sorted(selected):
        destination = cache / relative
        destination.parent.mkdir(exist_ok=True)
        (observation / relative).rename(destination)
    proof = dict(schemaVersion=1, selected=sorted(selected), excluded=excluded,
                 state='selected-unverified', requiredNext='resolved_restore_identity')
    (stage / 'restore-cache-selection.json').write_text(json.dumps(proof, sort_keys=True, indent=2) + '\n')
    return proof


def freeze_component_restore(stage, material):
    """Discard unmeasured restore side effects before any compilation.

    Only the verified NuGet assets and generated imports enter fresh obj folders.
    Successful dependency downloads remain readonly; prior generated binaries or
    arbitrary restore-time files cannot suppress compilation or become inputs.
    """
    from pathlib import PurePosixPath
    import stat
    stage = Path(stage).resolve()
    original = stage / 'artifacts'
    clean = stage / 'publish-inputs'
    projects = material.get('projectPaths')
    if (not isinstance(projects, list) or not 0 < len(projects) <= 128 or
            any(not isinstance(value, str) for value in projects) or
            len(projects) != len(set(projects))):
        raise ValueError('Verified restore project inventory missing')
    clean.mkdir()
    names, files = set(), {}
    for relative in projects:
        project = PurePosixPath(relative)
        if project.is_absolute() or '..' in project.parts or '\\' in relative or project.suffix != '.csproj':
            raise ValueError('Invalid verified restore project path')
        if project.stem in names:
            raise ValueError('Ambiguous restore output directory')
        names.add(project.stem)
        for name in ('project.assets.json', project.name + '.nuget.g.props', project.name + '.nuget.g.targets'):
            path = original / 'obj' / project.stem / name
            if (not path.resolve().is_relative_to(original) or path.is_symlink() or
                    not stat.S_ISREG(path.stat().st_mode) or path.stat().st_size > 32 * 1024 * 1024):
                raise ValueError('Unsafe verified restore material')
            destination = clean / 'obj' / project.stem / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, destination)
            files[str(destination.relative_to(clean))] = sha256_file(destination)
    # Both paths are exclusive to this build. Keep the original for diagnosis;
    # never mutate its contents or clear another build's restore state.
    original.rename(stage / 'restore-observation')
    clean.rename(original)
    return hashlib.sha256(json.dumps(files, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def static_restore_identity():
    """Observe npm's completed, integrity-checked install without resolving again."""
    import base64
    import stat
    import time
    from pathlib import PurePosixPath
    source = ROOT / 'Legend-Website'
    lock = json.loads((source / 'package-lock.json').read_text())
    if lock.get('lockfileVersion') != 3 or not isinstance(lock.get('packages'), dict):
        raise ValueError('Unsupported static dependency lock format')
    for name, package in lock['packages'].items():
        if name == '':
            continue
        path = PurePosixPath(name)
        if (not name.startswith('node_modules/') or '..' in path.parts or '\\' in name or
                package.get('link') or not isinstance(package.get('integrity'), str)):
            raise ValueError('Static dependency content identity unproven')
        algorithm, separator, encoded = package['integrity'].partition('-')
        try:
            valid = algorithm == 'sha512' and separator and len(base64.b64decode(encoded, validate=True)) == 64
        except (ValueError, TypeError):
            valid = False
        if not valid:
            raise ValueError('Static dependency integrity algorithm unsupported')
    root = source / 'node_modules'
    if not root.is_dir() or root.is_symlink():
        raise ValueError('Static dependency installation missing')
    rows, pending, count, total = {}, [root], 0, 0
    deadline = time.monotonic() + 120
    while pending:
        with os.scandir(pending.pop()) as entries:
            for entry in entries:
                count += 1
                if count > 100000 or time.monotonic() >= deadline:
                    raise ValueError('Static dependency inventory budget exceeded')
                path = Path(entry.path)
                metadata = path.lstat()
                row = dict(mode=stat.S_IMODE(metadata.st_mode))
                if stat.S_ISDIR(metadata.st_mode):
                    row['kind'] = 'directory'
                    pending.append(path)
                elif stat.S_ISLNK(metadata.st_mode):
                    if not path.resolve(strict=True).is_relative_to(root):
                        raise ValueError('Static dependency link escapes installation')
                    row.update(kind='link', target=os.readlink(path))
                elif stat.S_ISREG(metadata.st_mode):
                    digest = hashlib.sha256()
                    with path.open('rb') as file:
                        while block := file.read(1024 * 1024):
                            total += len(block)
                            if total > 512 * 1024 * 1024 or time.monotonic() >= deadline:
                                raise ValueError('Static dependency content budget exceeded')
                            digest.update(block)
                    row.update(kind='file', sha256=digest.hexdigest())
                else:
                    raise ValueError('Static dependency special file unsupported')
                rows[str(path.relative_to(root))] = row
    material = dict(lockSha256=sha256_file(source / 'package-lock.json'), files=rows)
    return dict(schemaVersion=1, identity=hashlib.sha256(json.dumps(material,
        sort_keys=True, separators=(',', ':')).encode()).hexdigest(), packageCount=len(lock['packages']) - 1)


def isolated_component_stage(revision, component, phase):
    """Private compiler steps executed only within the fixed package envelope."""
    global ROOT
    if Path(__file__).parent != Path('/authority') or Path.cwd() != Path('/src') or component not in APPS:
        raise ValueError('Isolated component stage requires its canonical mount boundary')
    ROOT = Path('/src')
    revision = validate_revision(revision)
    Path('/tmp/home').mkdir(exist_ok=True)
    static = APPS[component][2]
    if phase == 'bootstrap':
        if static:
            return dict(state='not-required', reason='static_component_has_no_nuget_adapter')
        Path('/tmp/empty-feed').mkdir()
        project = '/authority/PackageRestoreProbe/PackageRestoreProbe.csproj'
        properties = ['-p:BaseIntermediateOutputPath=/probe/obj/', '-p:MSBuildProjectExtensionsPath=/probe/obj/']
        run('dotnet', 'restore', project, '--source', '/tmp/empty-feed', '--nologo', *properties)
        run('dotnet', 'build', project, '--no-restore', '-c', 'Release', '--nologo',
            '-o', '/probe/bin', '-m:1', '-nr:false', '-p:UseSharedCompilation=false', *properties)
        return dict(state='executed-success')
    if phase == 'restore':
        if not static:
            run('dotnet', 'restore', APPS[component][0], '--nologo')
        if component in {'protect', 'website'}:
            extra = ['--omit=dev'] if component == 'protect' else []
            run('npm', 'ci', '--prefix', 'Legend-Website', '--ignore-scripts', '--no-audit', '--no-fund', *extra)
        return dict(state='executed-success')
    if phase == 'material':
        result = {}
        if not static:
            result['dotnet'] = resolved_restore_identity(component, Path('/tmp/masterapp'),
                [Path('/deps/nuget')], Path('/usr/share/dotnet'),
                content_hash_probe=Path('/probe/bin/PackageRestoreProbe.dll'))
        if component in {'protect', 'website'}:
            result['npm'] = static_restore_identity()
        return result
    if phase == 'publish':
        output = Path('/tmp/masterapp/component')
        output.mkdir(parents=True, exist_ok=True)
        if static:
            build_static(component, revision, output)
        else:
            build_dotnet(component, revision, output, no_restore=True)
        return dict(state='executed-success')
    raise ValueError('Unsupported isolated component stage')


def build_dotnet(app: str, revision: str, output: Path, *, no_restore=False):
    project, archive_name, _ = APPS[app]
    publish = output / (app + "-publish")
    shutil.rmtree(publish, ignore_errors=True)
    run(
        "dotnet", "publish", project, "-c", "Release", "--nologo",
        *(["--no-restore"] if no_restore else []),
        "-o", str(publish), "-p:SourceRevisionId=" + revision,
        "-m:1", "-nr:false", "-p:UseSharedCompilation=false",
    )
    wwwroot = publish / "wwwroot"
    wwwroot.mkdir(parents=True, exist_ok=True)
    (wwwroot / "_deployment-provenance.json").write_text(
        json.dumps({"releaseSha": revision, "release": SCHEMA}, separators=(",", ":")) + "\n"
    )
    zip_directory(publish, output / archive_name)
    # Only immutable deployable bytes cross the component boundary. Build trees
    # stay runner-local instead of bloating the retained validation artifact.
    shutil.rmtree(publish, ignore_errors=True)


def build_static(app: str, revision: str, output: Path):
    target = _RELEASE_AUTHORITY.RELEASE_TARGETS[app]
    source_root = target["sourceRoot"]
    run("npm", "ci", "--prefix", source_root, "--ignore-scripts", "--no-audit", "--no-fund")
    run("npm", "--prefix", source_root, "run", "build")
    dist = ROOT / source_root / "dist"
    (dist / "_deployment-provenance.txt").write_text(revision + "\n")
    shutil.copy2(ROOT / source_root / "public" / "web.config", dist / "web.config")
    zip_directory(dist, output / target["package"])


def build_migration_bundle(output: Path):
    env = os.environ | {
        "ConnectionStrings__MasterAppDb": "Server=127.0.0.1;Database=masterapp_ef_design_time;Integrated Security=true;TrustServerCertificate=true;Encrypt=false",
        "EF_FORCE_SQLSERVER": "true",
        "DOTNET_ENVIRONMENT": "Development",
        "ASPNETCORE_ENVIRONMENT": "Development",
    }
    # This component runs in an isolated package lane. It must materialize the
    # project graph it consumes instead of inheriting project.assets.json from a
    # previously serialized app publish.
    startup = "scripts/MigrationReleaseProbe/MigrationReleaseProbe.csproj"
    run("dotnet", "restore", startup, "--nologo", env=env)
    run("dotnet", "tool", "restore", env=env)
    destination = output / MIGRATION_BUNDLE
    run(
        "dotnet", "tool", "run", "dotnet-ef", "migrations", "bundle",
        "--force",
        "--project", "Infrastructure/Infrastructure.csproj",
        "--startup-project", startup,
        "--context", "MasterAppDbContext",
        "--configuration", "Release",
        "--output", str(destination),
        env=env,
    )
    destination.chmod(0o755)


COMPONENT_SCHEMA = "legend-validated-release-package-component.v1"
CONTENT_COMPONENT_SCHEMA = "legend-validated-release-package-component.v2"


def verify_component(revision, component, directory, *, execution_identity=None):
    """Read both receipt generations without changing retained bytes/provenance.

    Artifact producer authentication belongs to validation-resume. This boundary
    independently verifies bytes and the complete candidate dependency closure.
    V1 remains exact-revision only; it cannot masquerade as cross-revision proof.
    """
    revision = validate_revision(revision)
    path = directory / component_file(component)
    receipt = json.loads((directory / f'{component}.component.json').read_text())
    digest = sha256_file(path)
    if receipt.get('schema') == COMPONENT_SCHEMA:
        expected = dict(schema=COMPONENT_SCHEMA, applicationReleaseSha=revision,
            packageContractSha256=contract_hash(), packageIdentity=package_identity(revision),
            component=component, file=path.name, sha256=digest)
        if receipt != expected:
            raise ValueError('Validated legacy component receipt mismatch: ' + component)
        producer = revision
    elif receipt.get('schema') == CONTENT_COMPONENT_SCHEMA:
        if not re.fullmatch('[a-f0-9]{64}', execution_identity or ''):
            raise ValueError('Component execution environment unproven')
        producer = normalize_revision(receipt.get('producerRevision'))
        desired = _RELEASE_AUTHORITY.package_component_manifest(revision, component)
        original = _RELEASE_AUTHORITY.package_component_manifest(producer, component)
        if producer != revision and (not desired['reusable'] or not original['reusable']):
            raise ValueError('Component dependency closure unproven: ' + component)
        if desired['contentIdentity'] != original['contentIdentity']:
            raise ValueError('Component dependencies changed: ' + component)
        expected = dict(schema=CONTENT_COMPONENT_SCHEMA, producerRevision=producer,
            contentIdentity=desired['contentIdentity'], executionIdentity=execution_identity, component=component,
            file=path.name, sha256=digest)
        if receipt != expected:
            raise ValueError('Validated component content receipt mismatch: ' + component)
    else:
        raise ValueError('Unsupported component receipt schema')
    if component in APPS and embedded_revision(path, APPS[component][2]) != producer:
        raise ValueError('Component embedded producer mismatch: ' + component)
    return receipt


def restore_prepared_migration(revision, output, readiness_identity):
    """Recover a completed bundle child even if its rehearsal/parent failed."""
    import tempfile
    repository = os.environ['GITHUB_REPOSITORY']
    token = os.environ.get('GITHUB_TOKEN') or os.environ.get('GH_TOKEN')
    authority = _RELEASE_AUTHORITY
    workflow = authority.PACKAGE_VALIDATION_WORKFLOW
    data = authority.api_get(repository, 'actions/workflows/' + workflow + '/runs?event=pull_request&per_page=100', token)
    trusted = lambda run: authority._trusted_lineage_run(repository, run, '.github/workflows/' + workflow,
                                                       revision, require_completed=False)
    for run, artifact in authority.readiness_artifact_candidates(repository, data.get('workflow_runs', []),
            'legend-rehearsal-bundle-', readiness_identity, token, trusted):
        attempt = int(artifact['name'].rsplit('-a', 1)[1])
        if not authority.readiness_child_succeeded(repository, run, 'migration-rehearsal-prepare', attempt, token):
            continue
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            authority._download_run_artifact(repository, run['id'], artifact['name'], root, artifact_id=artifact['id'])
            bundle = root / 'migration' / MIGRATION_BUNDLE
            receipt = json.loads((root / 'migration/migration.component.json').read_text())
            expected = dict(schema=COMPONENT_SCHEMA, applicationReleaseSha=revision,
                packageContractSha256=contract_hash(), packageIdentity=package_identity(revision),
                component='migration', file=MIGRATION_BUNDLE, sha256=sha256_file(bundle))
            if receipt != expected:
                continue
            output.mkdir(parents=True, exist_ok=True)
            shutil.copy2(bundle, output / MIGRATION_BUNDLE)
            (output / MIGRATION_BUNDLE).chmod(0o755)
            shutil.copy2(root / 'migration/migration.component.json', output / 'migration.component.json')
            proof = dict(state='reused-success', sourceReceipt={'runId': run['id'], 'artifact': artifact['name'], 'artifactId': artifact['id']},
                         compatibilityProof=expected)
            (output / 'migration.reuse.json').write_text(json.dumps(proof, sort_keys=True) + '\n')
            return proof
    return None


def component_file(component: str) -> str:
    if component in APPS:
        return APPS[component][1]
    if component == "migration":
        return MIGRATION_BUNDLE
    raise ValueError("Unknown release package component: " + component)


def promoted_migration_artifact(revision, attempt):
    revision = normalize_revision(revision)
    if type(attempt) is not int or attempt < 1:
        raise ValueError('Promoted component requires exact producing attempt')
    return f'validated-rehearsed-migration-{revision}-a{attempt}'


def promote_rehearsed_migration(revision, output, evidence):
    """The credentialed planner restores bytes; the build lane receives no token."""
    revision = validate_revision(revision)
    output.mkdir(parents=True, exist_ok=True)
    source = evidence.get('receipt', {}).get('rehearsalSource')
    if source:
        import tempfile
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            _RELEASE_AUTHORITY._download_run_artifact(os.environ['GITHUB_REPOSITORY'], source['runId'], source['artifact'], root,
                                                     artifact_id=source['artifactId'])
            receipt = json.loads((root / 'migration/migration.component.json').read_text())
            bundle = root / 'migration' / MIGRATION_BUNDLE
            expected = dict(schema=COMPONENT_SCHEMA, applicationReleaseSha=revision,
                packageContractSha256=contract_hash(), packageIdentity=package_identity(revision),
                component='migration', file=MIGRATION_BUNDLE, sha256=sha256_file(bundle))
            if receipt != expected or receipt['sha256'] != evidence['receipt']['rehearsal']['bundleDigest']:
                raise ValueError('Validated rehearsal bundle is not the exact package component')
            shutil.copy2(bundle, output / MIGRATION_BUNDLE)
            (output / MIGRATION_BUNDLE).chmod(0o755)
            shutil.copy2(root / 'migration/migration.component.json', output / 'migration.component.json')
            (output / 'migration.reuse.json').write_text(json.dumps(dict(state='reused-success',
                sourceReceipt=source, compatibilityProof=expected), sort_keys=True) + '\n')
            print('LEGEND_PACKAGE:REUSED:migration:source=' + str(source['runId']))
            return receipt
    return None


def build_isolated_component(revision, component, output):
    """Canonical component build with declared inputs and no production access."""
    import tempfile
    import time
    revision = validate_revision(revision)
    if component not in APPS:
        raise ValueError('Application component required for isolated build')
    output = Path(output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix='legend-package-' + component + '-', dir=output.parent))
    started = time.monotonic()
    deadline = started + 1140  # Preserve 30-second cleanup within the existing 20-minute job.
    timings = {}
    def remaining():
        value = deadline - time.monotonic()
        if value <= 0:
            raise ValueError('Component build deadline exhausted; preserve completed siblings')
        return value
    def operation(name, action):
        before = time.monotonic()
        result = action()
        timings[name] = round(time.monotonic() - before, 3)
        return result
    def step(name, phase=None):
        return run_isolated_component(component, stage,
            ['python3', '/authority/release-package.py', 'isolated-component-stage',
             '--revision', revision, '--component', component, '--phase', name],
            phase=phase or name, timeout=min(remaining(), 150 if name == 'material' else 1140))
    def material():
        log = step('material', 'observe')
        if log.stat().st_size > 1024 * 1024:
            raise ValueError('Component input observation exceeds budget')
        return json.loads(log.read_text())
    try:
        source = operation('source', lambda: materialize_component_source(revision, stage))
        tools = operation('tools', lambda: prepare_isolated_tools(stage, timeout=min(240, remaining())))
        authority = stage / 'authority'
        authority.mkdir()
        authority_inputs = {}
        for relative in ('release-package.py', 'validation-resume.py',
                         'PackageRestoreProbe/PackageRestoreProbe.csproj', 'PackageRestoreProbe/Program.cs'):
            source_file = Path(__file__).parent / relative
            destination = authority / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source_file, destination)
            authority_inputs[relative] = sha256_file(destination)
        for directory in ('nuget', 'npm', 'probe', 'artifacts', 'website-workspace'):
            (stage / directory).mkdir()
        if not APPS[component][2]:
            operation('bootstrap', lambda: step('bootstrap'))
        operation('restore', lambda: step('restore'))
        if not APPS[component][2]:
            operation('selectRestoreCache', lambda: narrow_restore_cache(stage))
        before = operation('inputVerification', material)
        if not APPS[component][2]:
            operation('freezeRestore', lambda: freeze_component_restore(stage, before['dotnet']))
        operation('publish', lambda: step('publish'))
        after = operation('postBuildInputVerification', material)
        if before != after:
            raise ValueError('Component build changed verified dependency material')
        archive = stage / 'artifacts/component' / component_file(component)
        if embedded_revision(archive, APPS[component][2]) != revision:
            raise ValueError('Isolated component runtime provenance mismatch')
        digest = sha256_file(archive)
        shutil.copy2(archive, output / archive.name)
        proof = dict(schema='legend-package-component-execution.v1', state='executed-success',
            component=component, candidateRevision=revision, compatibilityScope='exact-candidate',
            authorityInputs=authority_inputs, source=source, tools=tools, dependencyMaterial=before,
            file=archive.name, sha256=digest, timings=timings,
            elapsedSeconds=round(time.monotonic() - started, 3),
            reason='compatible_component_execution_receipt_unavailable')
        (output / (component + '.execution.json')).write_text(json.dumps(proof, sort_keys=True, indent=2) + '\n')
        return proof
    except BaseException as exc:
        blocked = dict(schema='legend-package-component-execution.v1', state='blocked',
            component=component, candidateRevision=revision, failureType=type(exc).__name__,
            completedStages=list(timings), timings=timings, resumeBoundary='package-component:' + component)
        (stage / 'blocked.json').write_text(json.dumps(blocked, sort_keys=True, indent=2) + '\n')
        print('LEGEND_PACKAGE:BLOCKED:' + component + ':evidence=' + str(stage / 'blocked.json'))
        raise


def build_component(revision: str, component: str, output: Path):
    revision = validate_revision(revision)
    output.mkdir(parents=True, exist_ok=True)
    print('LEGEND_PACKAGE:EXECUTE:' + component + ':compatible_component_receipt_unavailable')
    destination = output / component_file(component)
    if destination.exists():
        destination.unlink()

    if component == "migration":
        build_migration_bundle(output)
    else:
        build_isolated_component(revision, component, output)

    receipt = {
        "schema": COMPONENT_SCHEMA,
        "applicationReleaseSha": revision,
        "packageContractSha256": contract_hash(),
        "packageIdentity": package_identity(revision),
        "component": component,
        "file": destination.name,
        "sha256": sha256_file(destination),
    }
    (output / f"{component}.component.json").write_text(
        json.dumps(receipt, sort_keys=True, indent=2) + "\n"
    )
    return receipt


def assemble_components(revision: str, output: Path):
    revision = validate_revision(revision)
    expected_contract = contract_hash()
    expected_identity = package_identity(revision)
    components = list(APPS) + ["migration"]
    files = []

    for component in components:
        path = output / component_file(component)
        receipt_path = output / f"{component}.component.json"
        if not path.is_file() or not receipt_path.is_file():
            raise ValueError("Validated package component missing: " + component)
        receipt = json.loads(receipt_path.read_text())
        expected = {
            "schema": COMPONENT_SCHEMA,
            "applicationReleaseSha": revision,
            "packageContractSha256": expected_contract,
            "packageIdentity": expected_identity,
            "component": component,
            "file": path.name,
            "sha256": sha256_file(path),
        }
        if receipt != expected:
            raise ValueError("Validated package component receipt mismatch: " + component)
        files.append(path)

    (output / "SHA256SUMS").write_text(
        "\n".join(f"{sha256_file(path)}  {path.name}" for path in files) + "\n"
    )
    tree = subprocess.check_output(
        ["git", "rev-parse", revision + "^{tree}"], cwd=ROOT, text=True
    ).strip()
    manifest = {
        "schema": SCHEMA,
        "applicationReleaseSha": revision,
        "applicationTreeSha": tree,
        "packageContractSha256": expected_contract,
        "packageIdentity": expected_identity,
        "files": {path.name: sha256_file(path) for path in files},
    }
    (output / "manifest.json").write_text(json.dumps(manifest, sort_keys=True, indent=2) + "\n")
    verify_all(revision, output)
    return manifest


def build_all(revision: str, output: Path):
    revision = validate_revision(revision)
    shutil.rmtree(output, ignore_errors=True)
    output.mkdir(parents=True, exist_ok=True)
    for component in list(APPS) + ["migration"]:
        build_component(revision, component, output)
    return assemble_components(revision, output)


def embedded_revision(package: Path, static: bool):
    with zipfile.ZipFile(package) as archive:
        if archive.testzip() is not None:
            raise ValueError("Corrupt immutable ZIP: " + package.name)
        entry = "_deployment-provenance.txt" if static else "wwwroot/_deployment-provenance.json"
        raw = archive.read(entry).decode().strip()
        return raw if static else json.loads(raw)["releaseSha"]


def verify_all(revision: str, directory: Path):
    revision = validate_revision(revision)
    manifest_path = directory / "manifest.json"
    sums_path = directory / "SHA256SUMS"
    if not manifest_path.exists() or not sums_path.exists():
        raise ValueError("Validated package manifest/checksums missing")
    manifest = json.loads(manifest_path.read_text())
    expected_tree = subprocess.check_output(
        ["git", "rev-parse", revision + "^{tree}"], cwd=ROOT, text=True
    ).strip()
    declared_contract = manifest.get("packageContractSha256", "")
    if not re.fullmatch(r"[0-9a-f]{64}", declared_contract):
        raise ValueError("Validated package contract identity malformed")
    current_head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    if declared_contract != contract_hash() and not _RELEASE_AUTHORITY.package_inputs_compatible(revision, current_head):
        raise ValueError("Validated package builder inputs changed")
    # Retain the producing contract/revision and bytes. Authenticating the producer
    # belongs to validated-package evidence lookup; never stamp current metadata
    # over the historical package's immutable manifest.
    declared_identity = hashlib.sha256(json.dumps({
        "schema": SCHEMA,
        "applicationReleaseSha": revision,
        "packageContractSha256": declared_contract,
    }, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    expected = {
        "schema": SCHEMA,
        "applicationReleaseSha": revision,
        "applicationTreeSha": expected_tree,
        "packageIdentity": declared_identity,
    }
    for key, value in expected.items():
        if manifest.get(key) != value:
            raise ValueError(f"Validated package {key} mismatch")

    rows = {}
    for line in sums_path.read_text().splitlines():
        parts = line.split()
        if len(parts) != 2:
            raise ValueError("Malformed SHA256SUMS")
        rows[Path(parts[1]).name] = parts[0]

    expected_files = [APPS[app][1] for app in APPS] + [MIGRATION_BUNDLE]
    if set(rows) != set(expected_files):
        raise ValueError("Validated package inventory mismatch")
    manifest_files = manifest.get("files")
    if not isinstance(manifest_files, dict) or set(manifest_files) != set(expected_files):
        raise ValueError("Validated package manifest inventory mismatch")

    for app, (_, archive_name, static) in APPS.items():
        path = directory / archive_name
        digest = sha256_file(path)
        if rows.get(archive_name) != digest or manifest_files.get(archive_name) != digest:
            raise ValueError("Validated package digest mismatch: " + archive_name)
        if embedded_revision(path, static) != revision:
            raise ValueError("Validated package provenance mismatch: " + app)

    bundle = directory / MIGRATION_BUNDLE
    bundle_digest = sha256_file(bundle)
    if rows.get(MIGRATION_BUNDLE) != bundle_digest or manifest_files.get(MIGRATION_BUNDLE) != bundle_digest:
        raise ValueError("Migration bundle digest mismatch")
    if not os.access(bundle, os.X_OK):
        bundle.chmod(0o755)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    identity = sub.add_parser("identity")
    identity.add_argument("--revision", required=True)
    identity.add_argument("--output")

    build = sub.add_parser("build")
    build.add_argument("--revision", required=True)
    build.add_argument("--directory", required=True)
    build.add_argument("--output")

    component = sub.add_parser("build-component")
    component.add_argument("--revision", required=True)
    component.add_argument("--component", required=True, choices=[*APPS.keys(), "migration"])
    component.add_argument("--directory", required=True)
    component.add_argument("--output")

    isolated = sub.add_parser("isolated-component-stage")
    isolated.add_argument("--revision", required=True)
    isolated.add_argument("--component", required=True, choices=list(APPS))
    isolated.add_argument("--phase", required=True, choices=['bootstrap', 'restore', 'material', 'publish'])

    assemble = sub.add_parser("assemble")
    assemble.add_argument("--revision", required=True)
    assemble.add_argument("--directory", required=True)
    assemble.add_argument("--output")

    verify = sub.add_parser("verify")
    verify.add_argument("--revision", required=True)
    verify.add_argument("--directory", required=True)

    args = parser.parse_args()
    if args.command == 'isolated-component-stage':
        print(json.dumps(isolated_component_stage(args.revision, args.component, args.phase), sort_keys=True))
        return
    if args.command == "identity":
        data = {
            "artifact": artifact_name(args.revision),
            "identity": package_identity(args.revision),
            "contract": contract_hash(),
        }
    elif args.command == "build":
        manifest = build_all(args.revision, Path(args.directory))
        data = {
            "artifact": artifact_name(args.revision),
            "identity": manifest["packageIdentity"],
            "contract": manifest["packageContractSha256"],
        }
    elif args.command == "build-component":
        receipt = build_component(args.revision, args.component, Path(args.directory))
        data = {
            "component": receipt["component"],
            "file": receipt["file"],
            "identity": receipt["packageIdentity"],
            "contract": receipt["packageContractSha256"],
        }
    elif args.command == "assemble":
        manifest = assemble_components(args.revision, Path(args.directory))
        data = {
            "artifact": artifact_name(args.revision),
            "identity": manifest["packageIdentity"],
            "contract": manifest["packageContractSha256"],
        }
    else:
        verify_all(args.revision, Path(args.directory))
        print("Validated immutable release package verified.")
        return

    if args.output:
        with open(args.output, "a") as out:
            for key, value in data.items():
                out.write(f"{key}={value}\n")
    print(json.dumps(data, sort_keys=True))


if __name__ == "__main__":
    main()
