// Real local Linux process tests. These are not Cloudflare qualification evidence.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
const launcher = '/usr/local/bin/legend-offline';
const probe = '/usr/local/bin/legend-probe';
const support = spawnSync(probe, ['seccomp-support'], { encoding: 'utf8', timeout: 4000 });
assert.equal(support.status, 0, `Native Linux seccomp is required; no tests skipped or boundary relaxed. ${support.stderr}`);
const run = (command, args = [], seconds = 5) => spawnSync(launcher,
  ['--wall-seconds', String(seconds), '--', command, ...args],
  { env: { ...process.env, LEGEND_TEST_SECRET: 'must-not-survive' }, encoding: 'utf8', timeout: (seconds + 4) * 1000, maxBuffer: 2 * 1024 * 1024 });
const checked = run(probe);
assert.equal(checked.status, 0, checked.stderr);
assert.equal(checked.stdout.trim().split('\n').length, 2);
const inherited = spawnSync('/bin/sh', ['-c', 'exec 9</etc/os-release; exec "$1" --wall-seconds 5 -- "$2"', 'probe', launcher, probe], { encoding: 'utf8', timeout: 9000 });
assert.equal(inherited.status, 0, inherited.stderr);
const hung = run(probe, ['hang'], 1);
assert.equal(hung.status, 124, hung.stderr);
await new Promise((resolve, reject) => {
  const child = spawn(launcher, ['--wall-seconds', '30', '--', probe, 'hang']);
  let ready = false; let stderr = ''; let stdout = '';
  const timeout = () => { child.kill('SIGKILL'); reject(new Error('supervisor cancellation exceeded bound')); };
  let timer = setTimeout(timeout, 8000);
  child.stdout.on('data', bytes => {
    stdout += bytes;
    if (!ready && stdout.includes('probe_hanging')) {
      ready = true; clearTimeout(timer); timer = setTimeout(timeout, 3000); child.kill('SIGTERM');
    }
  });
  child.stderr.on('data', bytes => { stderr += bytes; });
  child.on('error', error => { clearTimeout(timer); reject(error); });
  child.on('exit', code => {
    clearTimeout(timer);
    try { assert(ready, stderr); assert.equal(code, 124, stderr); resolve(); } catch (error) { reject(error); }
  });
});
const abi = run(probe, ['x32']);
assert.equal(abi.status, 159, 'alternate syscall ABI was not killed');
const flood = run(probe, ['flood']);
assert.equal(flood.status, 126, flood.stderr); assert(flood.stdout.length <= 1024 * 1024);
const orphan = run(probe, ['orphan']);
assert.equal(orphan.status, 0, orphan.stderr);
assert.equal(existsSync(`/proc/${Number(orphan.stdout.trim())}`), false, 'background descendant survived');
const node = run('/opt/node/bin/node', ['-e', 'console.log(6*7)']);
assert.equal(node.status, 0, node.stderr); assert.equal(node.stdout.trim(), '42');
const dotnet = run('/usr/share/dotnet/dotnet', ['--info']);
assert.equal(dotnet.status, 0, dotnet.stderr); assert.match(dotnet.stdout, /10\.0\.401/);
const badDeadline = run(probe, [], 601);
assert.equal(badDeadline.status, 125);
// Compile-time simulation of an unavailable cleanup acknowledgment; never shipped.
const cleanup = spawnSync('/usr/local/bin/legend-offline-cleanup-fault', ['--wall-seconds', '5', '--', '/bin/true'], { encoding: 'utf8', timeout: 9000 });
assert.equal(cleanup.status, 127, cleanup.stderr); assert.match(cleanup.stderr, /cleanup_unverified/);
console.log('PASS: Linux seccomp, inherited filter, nonroot, env/FD clearing, limits, supervisor, timeout, orphan cleanup, Node and dotnet --info');
