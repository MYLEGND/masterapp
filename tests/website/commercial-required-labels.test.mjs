import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {JSDOM} from 'jsdom';
const source=readFileSync(new URL('../../Protect-Website/Views/Quote/Commercial.cshtml',import.meta.url),'utf8');
const start=source.indexOf('function enforceRequiredByLabels()');
const end=source.indexOf('// Custom rules',start);
const functionSource=source.slice(start,source.lastIndexOf('// -----------------------------',end));
test('starred coverage group never makes subsequent optional comments required',()=>{
  const dom=new JSDOM(`<form>
    <label for="EffectiveDate">Effective date<span class="required-star">*</span></label><input id="EffectiveDate">
    <label>Coverage Types Needed<span class="required-star">*</span></label>
    <div><label><input type="checkbox" name="InterestedIn" value="property">Property</label></div>
    <label for="Comments">Additional Comments</label><textarea id="Comments" placeholder="Optional"></textarea>
    <label for="BuildingLimit">Building Limit</label><input id="BuildingLimit">
  </form>`,{runScripts:'outside-only'});
  try {
    dom.window.eval(functionSource+';enforceRequiredByLabels();enforceRequiredByLabels();');
    assert.equal(dom.window.document.getElementById('EffectiveDate').required,true);
    assert.equal(dom.window.document.getElementById('Comments').required,false);
    assert.equal(dom.window.document.getElementById('BuildingLimit').required,false);
    assert.equal(dom.window.document.querySelector('[name=InterestedIn]').required,false);
  }finally{dom.window.close();}
});
