// Dedicated publication process entrypoint. The .NET publisher invokes this
// file directly so runtime execution never depends on comparing Windows,
// junction, URL, or filesystem path spellings.
const started=performance.now();
let phase='module_load';
try {
  const {compileWebsite}=await import('./render-business.mjs');
  phase='input_read';
  let input='';
  for await (const chunk of process.stdin) {
    input+=chunk;
    if(input.length>8_000_000)
      throw Object.assign(new Error('Website document too large.'),{code:'WEBSITE_INPUT_LIMIT'});
  }
  phase='input_parse';
  const payload=JSON.parse(input);
  phase='render';
  const result=await compileWebsite(payload);
  phase='output';
  process.stdout.write(JSON.stringify(result));
} catch(error) {
  const code=error?.code;
  const category=code==='ERR_SCRIPT_EXECUTION_TIMEOUT' ? 'render_timeout'
    : code==='ERR_MODULE_NOT_FOUND' || code==='MODULE_NOT_FOUND' ? 'module_missing'
    : code==='ENOENT' ? 'file_missing'
    : code==='EACCES' || code==='EPERM' ? 'file_access_denied'
    : code==='WEBSITE_INPUT_LIMIT' ? 'input_limit'
    : phase==='input_parse' ? 'invalid_input'
    : 'compiler_error';
  process.stderr.write(JSON.stringify({
    schema:'legend-website-compiler-diagnostic/v1',phase,category,
    elapsedMs:Math.round(performance.now()-started)
  })+'\n');
  process.exitCode=1;
}
