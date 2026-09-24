// Dedicated publication process entrypoint. The .NET publisher invokes this
// file directly so runtime execution never depends on comparing Windows,
// junction, URL, or filesystem path spellings.
import {compileBusiness} from './render-business.mjs';

let input='';
for await (const chunk of process.stdin) {
  input+=chunk;
  if(input.length>8_000_000) throw new Error('Website document too large.');
}
process.stdout.write(JSON.stringify(await compileBusiness(JSON.parse(input))));
