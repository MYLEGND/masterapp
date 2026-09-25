#!/usr/bin/env python3
"""Read-only build graph for diagnostic attribution and conservative release impact.

Uses tracked build metadata, never executes project files. Missing dependencies or
unowned changes broaden validation instead of silently skipping applications.
This planner never grants deployment authority or supplies cloud credentials.
"""
import argparse
import json
import posixpath
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath


def git(root, *args):
    return subprocess.check_output(['git', '-C', str(root), *args], text=True)


def discover(root, files):
    projects = {}
    for name in sorted(files):
        path = PurePosixPath(name)
        if any(p in {'node_modules', 'bin', 'obj', 'Pods', 'vendor', '.build'} or p.startswith('publish-') for p in path.parts):
            continue
        kind = ('dotnet' if path.suffix == '.csproj' else
                'android' if path.name in {'settings.gradle', 'settings.gradle.kts'} else
                'ios' if path.name == 'project.pbxproj' and path.parent.suffix == '.xcodeproj' else
                'javascript' if path.name == 'package.json' else None)
        if kind is None:
            continue
        directory = str(path.parent.parent if kind == 'ios' else path.parent)
        record = {'id': name, 'root': directory, 'kind': kind, 'dependencies': [],
                  'externalInputs': [], 'uncertainDependencies': False}
        content = (root / name).read_text(encoding='utf-8')
        if kind == 'dotnet':
            tree = ET.fromstring(content)
            for element in tree.iter():
                tag = element.tag.split('}')[-1]
                include = element.attrib.get('Include', '').replace('\\', '/')
                if '$(' in include or '*' in include:
                    record['uncertainDependencies'] = True
                elif tag == 'ProjectReference' and include:
                    record['dependencies'].append(posixpath.normpath(posixpath.join(directory, include)))
                elif tag in {'Content', 'EmbeddedResource', 'Compile', 'None'} and include.startswith('../'):
                    record['externalInputs'].append(posixpath.normpath(posixpath.join(directory, include)).split('*')[0])
        elif kind == 'javascript':
            data = json.loads(content)
            record['packageName'] = data.get('name')
            record['packageDependencies'] = sorted(set(data.get('dependencies', {})) | set(data.get('devDependencies', {})))
        else:
            # Native build graphs may reference shared resources outside their root.
            # Do not claim a textual scan evaluates Gradle or Xcode semantics.
            record['uncertainDependencies'] = True
        projects[name] = record
    packages = {p['packageName']: key for key, p in projects.items() if p.get('packageName')}
    for project in projects.values():
        project['dependencies'] += [packages[p] for p in project.pop('packageDependencies', []) if p in packages]
        project['uncertainDependencies'] |= any(d not in projects for d in project['dependencies'])
    return projects


def impact(projects, changed):
    selected = set()
    reasons = []
    for name in changed:
        owners = {key for key, p in projects.items() if name == key or
                  (p['root'] != '.' and name.startswith(p['root'] + '/')) or
                  any(name.startswith(prefix) for prefix in p['externalInputs'])}
        if not owners:
            reasons.append('unowned-change:' + name)
        selected.update(owners)
    if reasons:
        selected.update(projects)
    # Unknown build dependencies cannot authorize a skipped native target.
    if changed:
        selected.update(k for k, p in projects.items() if p['uncertainDependencies'])
    while True:
        expanded = selected | {k for k, p in projects.items() if set(p['dependencies']) & selected}
        if expanded == selected:
            break
        selected = expanded
    return sorted(selected), reasons


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', default='.')
    parser.add_argument('--base', required=True)
    parser.add_argument('--head', default='HEAD')
    parser.add_argument('--output')
    args = parser.parse_args()
    root = Path(args.root).resolve()
    base = git(root, 'rev-parse', '--verify', args.base + '^{commit}').strip()
    head = git(root, 'rev-parse', '--verify', args.head + '^{commit}').strip()
    if head != git(root, 'rev-parse', 'HEAD').strip():
        parser.error('Checkout the exact head before planning impact.')
    files = git(root, 'ls-files', '-z').split('\0')
    projects = discover(root, [f for f in files if f and (root / f).is_file()])
    changed = [p for p in git(root, 'diff', '--name-only', '--no-renames', '-z', base, head).split('\0') if p]
    affected, reasons = impact(projects, changed)
    result = {'schemaVersion': 1, 'baseSha': base, 'headSha': head,
              'projects': list(projects.values()), 'changedFiles': changed,
              'affectedProjects': affected, 'conservativeReasons': reasons,
              'deploymentAuthorized': False,
              'deploymentBinding': 'existing-release-authority-required'}
    text = json.dumps(result, indent=2) + '\n'
    if args.output:
        Path(args.output).write_text(text)
    else:
        print(text, end='')


if __name__ == '__main__':
    main()
