#!/usr/bin/env python3
"""Local SQL implementation for scripts/db.sh; never obtains production credentials."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parent.parent
STATE = Path.home() / '.local/share/legend/masterapp-local-test'
NAME = 'legend-masterapp-local-test'
DATABASE = 'MasterAppLocalTest'
PORT = 14339
IMAGE = 'mcr.microsoft.com/mssql/server:2022-latest'
def projects():
    import ast
    tree = ast.parse((ROOT / 'scripts/validation-resume.py').read_text())
    values = [ast.literal_eval(node.value) for node in tree.body if isinstance(node, ast.Assign)
              and any(isinstance(target, ast.Name) and target.id == 'RELEASE_TARGETS' for target in node.targets)]
    require(len(values) == 1, 'Canonical application inventory is ambiguous.')
    return {name: row['project'] for name, row in values[0].items() if not row['static']}


URLS = {'portal': 'http://127.0.0.1:6206', 'client': 'http://127.0.0.1:5222',
        'protect': 'http://127.0.0.1:1112', 'parfait': 'http://127.0.0.1:2122'}


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def canonical():
    spec = importlib.util.spec_from_file_location('checkout_sync', ROOT / 'scripts/sync-published-checkout.py')
    owner = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(owner)
    owner.assert_canonical_checkout(ROOT, required=True)


def command(args, *, env=None):
    result = subprocess.run(args, cwd=ROOT, env=env, capture_output=True, text=True)
    if result.returncode:
        detail = result.stdout + '\n' + result.stderr
        for key, value in (env or {}).items():
            if value and any(word in key.lower() for word in ('password', 'secret', 'token', 'masterappdb', 'db_connection')):
                detail = detail.replace(value, '<redacted>')
        STATE.mkdir(parents=True, exist_ok=True, mode=0o700)
        log = STATE / 'last-error.log'
        with log.open('w') as output:
            os.chmod(log, 0o600)
            output.write(detail)
        raise RuntimeError('Local ' + Path(args[0]).name + ' failed (exit ' + str(result.returncode) +
                           '); see ' + str(log) + '. No reset or fallback was attempted.')
    return result.stdout


def config():
    path = STATE / 'database.json'
    require(path.is_file(), 'Local SQL has not been initialized. Use the explicit scripts/db.sh local init command.')
    require(path.stat().st_mode & 0o077 == 0, 'Local database configuration must have mode 600.')
    data = json.loads(path.read_text())
    require(data['container'] == NAME and data['database'] == DATABASE and data['port'] == PORT,
            'Local database identity changed; reconcile instead of selecting a replacement.')
    return data


def inspect(data):
    rows = json.loads(command(['docker', 'inspect', NAME]))
    require(len(rows) == 1 and rows[0]['Id'] == data['containerId'], 'Local SQL container identity changed.')
    row = rows[0]
    require(row['HostConfig']['PortBindings'] == {'1433/tcp': [{'HostIp': '127.0.0.1', 'HostPort': str(PORT)}]},
            'Local SQL must bind only the expected loopback port.')
    require(any(m.get('Name') == NAME and m['Destination'] == '/var/opt/mssql' for m in row['Mounts']),
            'Persistent local SQL volume is missing; refusing an empty replacement.')
    return row


def connection(data):
    return f"Server=127.0.0.1,{PORT};Initial Catalog={DATABASE};User ID=sa;Password={data['password']};Encrypt=True;TrustServerCertificate=True"


def sql(data, query, database=DATABASE):
    require(database in (DATABASE, 'master'), 'Unexpected local database.')
    env = dict(os.environ, SQLCMDPASSWORD=data['password'])
    return command(['sqlcmd', '-S', f'127.0.0.1,{PORT}', '-d', database, '-U', 'sa', '-C', '-l', '5',
                    '-t', '30', '-b', '-h', '-1', '-W', '-Q', query], env=env).strip()


def start(data):
    if not inspect(data)['State']['Running']:
        command(['docker', 'start', NAME])
    for attempt in range(12):
        try:
            sql(data, 'SET NOCOUNT ON; SELECT 1', 'master')
            return
        except RuntimeError:
            if attempt == 11:
                raise
            time.sleep(2)


def verify(data):
    require(inspect(data)['State']['Running'], 'Local SQL is stopped. Run scripts/db.sh local start.')
    identity = sql(data, "SET NOCOUNT ON; SELECT CONVERT(nvarchar(100),value) FROM sys.extended_properties WHERE class=0 AND name='LegendLocalTestIdentity'")
    require(identity == data['identity'], 'Persistent test database identity is missing or changed. Refusing replacement/reinitialization.')
    return {'database': DATABASE, 'server': f'127.0.0.1,{PORT}', 'container': NAME,
            'identity': identity, 'persistentVolume': NAME, 'status': 'ready', 'customerDataImported': False}


def initialize():
    require(not (STATE / 'database.json').exists(), 'Persistent local SQL is already configured; init never resets it.')
    bootstrap = STATE / 'bootstrap'
    bootstrap.mkdir(parents=True, exist_ok=True, mode=0o700)
    schema = bootstrap / 'model-schema.sql'
    placeholder = {'password': 'SchemaGenerationOnly_NotARealCredential'}
    env = local_environment(placeholder)
    env.update(EF_FORCE_SQLSERVER='true', MASTERAPP_DEV_USE_SQLSERVER='true')
    common = ['--project', str(ROOT / 'Infrastructure/Infrastructure.csproj'),
              '--startup-project', str(ROOT / 'AgentPortal/AgentPortal.csproj'), '--context', 'MasterAppDbContext']
    command(['dotnet', 'tool', 'run', 'dotnet-ef', 'dbcontext', 'script', *common, '--output', str(schema)], env=env)
    raw = command(['dotnet', 'tool', 'run', 'dotnet-ef', 'migrations', 'list', *common, '--no-build', '--no-connect', '--json'], env=env)
    rows = json.loads(raw[raw.index('['):])
    import re
    version = command(['dotnet', 'tool', 'run', 'dotnet-ef', '--version'], env=env).strip().split()[-1]
    require(re.fullmatch(r'[0-9.]+', version) is not None, 'EF version could not be verified.')
    rows = [{'MigrationId': row['id'], 'ProductVersion': version} for row in rows]
    require(rows and all(re.fullmatch(r'[A-Za-z0-9_]+', row['MigrationId']) for row in rows), 'Invalid source migration metadata.')
    history = bootstrap / 'model-history.json'
    history.write_text(json.dumps(rows, indent=2))
    require(not command(['docker', 'ps', '-aq', '--filter', 'name=^/' + NAME + '$']).strip(), 'Container already exists; reconcile it instead of recreating.')
    volumes = command(['docker', 'volume', 'ls', '--format', '{{.Name}}']).splitlines()
    require(NAME not in volumes, 'Persistent volume already exists; never replace or initialize over it.')
    STATE.mkdir(parents=True, exist_ok=True, mode=0o700)
    data = dict(container=NAME, database=DATABASE, port=PORT, identity=secrets.token_hex(24),
                password='LocalOnly!' + secrets.token_hex(24), schemaSha256=hashlib.sha256(schema.read_bytes()).hexdigest(),
                initialization='CURRENT_EF_MODEL_NOT_PRODUCTION_MIGRATION_REHEARSAL',
                sourceRevision=command(['git', '-C', str(ROOT), 'rev-parse', 'HEAD']).strip(),
                historySha256=hashlib.sha256(history.read_bytes()).hexdigest(), initialized=False)
    path = STATE / 'database.json'
    with path.open('x') as output:
        os.chmod(path, 0o600)
        json.dump(data, output, indent=2)
    command(['docker', 'volume', 'create', '--label', 'com.mylegnd.purpose=persistent-local-test', NAME])
    env = dict(os.environ, MSSQL_SA_PASSWORD=data['password'])
    data['containerId'] = command(['docker', 'create', '--name', NAME, '--platform', 'linux/amd64',
        '--label', 'com.mylegnd.purpose=persistent-local-test', '-e', 'ACCEPT_EULA=Y', '-e', 'MSSQL_PID=Developer',
        '-e', 'MSSQL_SA_PASSWORD', '-p', f'127.0.0.1:{PORT}:1433', '-v', NAME + ':/var/opt/mssql', IMAGE], env=env).strip()
    path.write_text(json.dumps(data, indent=2))
    start(data)
    # Explicit initialization only. This command is never called by sync or run.
    sql(data, 'CREATE DATABASE [' + DATABASE + ']', 'master')
    env = dict(os.environ, SQLCMDPASSWORD=data['password'])
    command(['sqlcmd', '-S', f'127.0.0.1,{PORT}', '-d', DATABASE, '-U', 'sa', '-C', '-l', '5',
             '-t', '120', '-b', '-i', str(schema)], env=env)
    sql(data, 'CREATE TABLE dbo.__EFMigrationsHistory (MigrationId nvarchar(150) NOT NULL PRIMARY KEY, ProductVersion nvarchar(32) NOT NULL)')
    values = ',\n'.join("(N'" + r['MigrationId'] + "',N'" + r['ProductVersion'] + "')" for r in rows)
    sql(data, "SET XACT_ABORT ON; BEGIN TRANSACTION; IF EXISTS(SELECT 1 FROM dbo.__EFMigrationsHistory) THROW 51000,'History unexpectedly populated',1; INSERT INTO dbo.__EFMigrationsHistory(MigrationId,ProductVersion) VALUES " + values + "; EXEC sys.sp_addextendedproperty @name=N'LegendLocalTestIdentity',@value=N'" + data['identity'] + "'; COMMIT;")
    data['initialized'] = True
    path.write_text(json.dumps(data, indent=2))
    print(json.dumps(verify(data)))


def local_environment(data):
    # Do not inherit provider/payment/messaging credentials from the operator shell.
    allowed = ('PATH', 'HOME', 'USER', 'LOGNAME', 'TMPDIR', 'TMP', 'TEMP', 'LANG', 'LC_ALL', 'DOTNET_ROOT', 'DOTNET_PROCESSOR_COUNT')
    env = {k: os.environ[k] for k in allowed if k in os.environ}
    cs = connection(data)
    env.update(ASPNETCORE_ENVIRONMENT='Development', DOTNET_ENVIRONMENT='Development',
               LocalTesting__Enabled='true', Founder__Upn='founder@local-test.invalid', SQLCONNSTR_MasterAppDb=cs, ConnectionStrings__MasterAppDb=cs,
               MasterAppDb=cs, MASTERAPP_LOCAL_DB_CONNECTION=cs)
    return env


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('status', 'init', 'start', 'stop', 'run', 'migrate'))
    parser.add_argument('app', nargs='?', choices=tuple(projects()))
    args = parser.parse_args()
    canonical()
    if args.action == 'init':
        initialize()
        return
    data = config()
    if args.action == 'stop':
        inspect(data)
        command(['docker', 'stop', NAME])
        print('Local SQL stopped; persistent database retained.')
        return
    if args.action in ('start', 'run', 'migrate'):
        start(data)
    proof = verify(data)
    if args.action in ('status', 'start'):
        print(json.dumps(proof))
        return
    if args.action == 'migrate':
        result = subprocess.run(['bash', str(ROOT / 'scripts/db.sh'), 'apply'], cwd=ROOT, env=local_environment(data))
    else:
        require(args.app in projects(), 'run requires an app name.')
        env = local_environment(data)
        env['ASPNETCORE_URLS'] = URLS[args.app]
        print(json.dumps(dict(app=args.app, url=URLS[args.app], database=proof)), flush=True)
        result = subprocess.run(['dotnet', 'run', '--project', str(ROOT / projects()[args.app]), '--no-launch-profile'],
                                cwd=ROOT, env=env)
    raise SystemExit(result.returncode)


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, OSError, ValueError) as error:
        print('LOCAL TEST BLOCKED: ' + str(error), file=sys.stderr)
        raise SystemExit(1)
