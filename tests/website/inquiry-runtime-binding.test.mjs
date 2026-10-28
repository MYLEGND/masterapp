import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {JSDOM} from 'jsdom';

const source=readFileSync(new URL('../../SHARED/WebsitePlatform/public-inquiry-form.mjs',import.meta.url),'utf8');
for(const [name,binding,expected] of [
  ['packaged dependency','/legend-public-inquiry.js?v=child-hash','https://www.mylegnd.com/legend-public-inquiry.js?v=child-hash'],
  ['external dependency rejected','https://other.test/legend-public-inquiry.js?v=bad','https://www.mylegnd.com/legend-public-inquiry.js'],
  ['other path rejected','/unrelated.js?v=bad','https://www.mylegnd.com/legend-public-inquiry.js'],
]) test(`canonical inquiry module imports ${name} once`,async()=>{
  const dom=new JSDOM('<form data-website-inquiry></form><script type="module" src="/public-inquiry-form.mjs?v=parent-hash"></script>',
    {url:'https://www.mylegnd.com/contact',runScripts:'outside-only'});
  try {
    dom.window.document.querySelector('script').setAttribute('data-inquiry-runtime',binding);
    const imports=[];
    dom.window.testImport=async url=>{imports.push(url);};
    // Keep the shipped module body; replace only ESM boundaries to observe its
    // actual dynamic import target without loading code from a real network.
    dom.window.eval(source.replace(/^export /gm,'')
      .replaceAll('import.meta.url',JSON.stringify('https://www.mylegnd.com/public-inquiry-form.mjs?v=parent-hash'))
      .replace('import(runtimeUrl.href)','window.testImport(runtimeUrl.href)'));
    dom.window.document.dispatchEvent(new dom.window.Event('DOMContentLoaded'));
    await new Promise(resolve=>setTimeout(resolve,0));
    dom.window.dispatchEvent(new dom.window.Event('legend:website-content-rendered'));
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.deepEqual(imports,[expected]);
  } finally {dom.window.close();}
});
