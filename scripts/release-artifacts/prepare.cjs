'use strict';
const fs = require('node:fs');
// JavaScript actions receive these job-scoped values from the runner. Shell
// steps do not. Keep them exclusively in the runner's private environment file.
const names = ['ACTIONS_RUNTIME_TOKEN', 'ACTIONS_RESULTS_URL', 'ACTIONS_RUNTIME_URL'];
for (const name of names) {
  const value = process.env[name];
  if (!value || /[\r\n]/.test(value)) throw new Error('Artifact runtime is unavailable');
}
process.stdout.write(`::add-mask::${process.env.ACTIONS_RUNTIME_TOKEN}\n`);
fs.appendFileSync(process.env.GITHUB_ENV, names.map(name => `${name}=${process.env[name]}\n`).join(''));
