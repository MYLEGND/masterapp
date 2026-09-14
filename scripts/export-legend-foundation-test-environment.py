#!/usr/bin/env python3
"""Export existing Azure foundation settings for isolated CI checks; never mutate Azure."""
import argparse, json, os, shlex, stat, subprocess
from pathlib import Path
from urllib.parse import urlsplit
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('output', type=Path)
parser.add_argument('--format', choices=('shell', 'json'), default='shell')
args = parser.parse_args()
result = subprocess.run([
    'az', 'webapp', 'config', 'appsettings', 'list',
    '--resource-group', os.environ['AZURE_RESOURCE_GROUP'],
    '--name', os.environ['AZURE_WEBAPP_NAME'], '--output', 'json'
], capture_output=True, text=True, timeout=60)
if result.returncode:
    raise SystemExit('Cannot read the existing foundation settings with the release identity.')
items = json.loads(result.stdout)
if os.environ.get('GITHUB_OUTPUT'):
    with Path(os.environ['GITHUB_OUTPUT']).open('a') as receipt:
        receipt.write('foundation_configuration_inspected=true\n')
settings = {}
for item in items:
    name = item['name'].replace('__', ':').lower()
    if not name.startswith('legendconnect:foundation:'):
        continue
    name = name.removeprefix('legendconnect:foundation:')
    value = item['value'] or ''
    if name in settings and settings[name] != value:
        raise SystemExit('Conflicting foundation configuration aliases: ' + name)
    if any(ord(c) < 32 for c in value):
        raise SystemExit('Invalid control character in foundation configuration: ' + name)
    settings[name] = value
required = ['endpoint', 'hostkind', 'model', 'modelrevision', 'apikey', 'engineversion', 'toolcallparser']
required.append('machostid' if settings.get('hostkind') == 'FounderMac' else 'azureresourceid')
for name in required:
    if not settings.get(name) or settings[name].startswith('@Microsoft.KeyVault('):
        raise SystemExit('Controlled-model acceptance configuration is missing or unresolved: ' + name)
endpoint = urlsplit(settings['endpoint'])
if endpoint.scheme != 'https' or not endpoint.hostname or endpoint.username or endpoint.password or endpoint.query or endpoint.fragment:
    raise SystemExit('CI requires the configured authenticated HTTPS model connector.')
# The application transport remains the host/model receipt authority.
# Enabled stays unchanged on Azure; only the synthetic test fixture enables inference.
mapping = {
    'ENDPOINT': 'endpoint', 'HOST_KIND': 'hostkind', 'MAC_HOST_ID': 'machostid',
    'AZURE_RESOURCE_ID': 'azureresourceid', 'MODEL': 'model', 'REVISION': 'modelrevision',
    'KEY': 'apikey', 'ENGINE_VERSION': 'engineversion', 'TOOL_CALL_PARSER': 'toolcallparser',
    'STREAM_RESPONSES': 'streamresponses', 'ENABLE_THINKING': 'enablethinking',
    'REASONING_EFFORT': 'reasoningeffort', 'TEMPERATURE': 'temperature', 'TOP_P': 'topp',
    'TOP_K': 'topk', 'PRESENCE_PENALTY': 'presencepenalty', 'SEED': 'seed',
    'CONTEXT_TOKENS': 'maxcontexttokens', 'OUTPUT_TOKENS': 'maxoutputtokens',
    'TIMEOUT_SECONDS': 'timeoutseconds'
}
print('::add-mask::' + settings['apikey'].replace('%', '%25'))
environment = {'LEGEND_CONTROLLED_FOUNDATION_' + target: settings[source]
               for target, source in mapping.items() if settings.get(source)}
# Enforce private permissions even when the caller supplied an existing file.
# Reject links and non-files before truncation or writing any credential bytes.
descriptor = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_NOFOLLOW | os.O_NONBLOCK, 0o600)
with os.fdopen(descriptor, 'w', encoding='utf-8') as output:
    if not stat.S_ISREG(os.fstat(output.fileno()).st_mode):
        raise SystemExit('Foundation configuration output must be a regular file.')
    os.fchmod(output.fileno(), 0o600)
    output.truncate(0)
    if args.format == 'json':
        json.dump(environment, output, ensure_ascii=False)
        output.write('\n')
    else:
        for name, value in environment.items():
            output.write('export ' + name + '=' + shlex.quote(value) + '\n')
# Only a boolean receipt is exposed; the credential remains process-scoped.
if os.environ.get('GITHUB_OUTPUT'):
    with Path(os.environ['GITHUB_OUTPUT']).open('a') as receipt:
        receipt.write('foundation_configuration_loaded=true\n')
