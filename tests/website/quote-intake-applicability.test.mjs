import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {execFileSync} from 'node:child_process';
import {JSDOM} from 'jsdom';
const root = new URL('../../', import.meta.url);
const read = path => process.env.QUOTE_SOURCE_REF
  ? execFileSync('git',['show',`${process.env.QUOTE_SOURCE_REF}:${path}`],{cwd:root,encoding:'utf8'})
  : readFileSync(new URL(path,root),'utf8');
const auto=read('Protect-Website/Views/Quote/Auto.cshtml');
const home=read('Protect-Website/Views/Quote/Home.cshtml');
const template=(source,id)=>source.match(new RegExp(`<template id="${id}">([\\s\\S]*?)<\\/template>`))[1];
const options=(source,key)=>new JSDOM(source.match(new RegExp(`<select (?:asp-for|name)="${key}"[\\s\\S]*?<\\/select>`))[0]).window.document.querySelector('select');

test('all driver lists preserve supported applicant statuses and marital answers',()=>{
  for(const key of ['Drivers\\[0\\]\\.DLStatus','Drivers\\[__i__\\]\\.DLStatus']) {
    const select=options(auto,key);select.value='Not Licensed';assert.equal(select.value,'Not Licensed');
  }
  const select=options(auto,'Drivers\\[__i__\\]\\.MaritalStatus');
  for(const status of ['Single','Married','Domestic Partner','Widowed','Separated','Divorced']) {
    select.value=status;assert.equal(select.value,status);
  }
});

test('standalone home intake accepts truthful no-history answers while keeping an answer required',()=>{
  const select=options(home,'AutoYearsWithPriorCarrierOrAgent');
  for(const answer of ['0 years','Not applicable','1 year','5 Years or more']) {
    select.value=answer;assert.equal(select.value,answer);assert.equal(select.checkValidity(),true);
  }
  select.value='';assert.equal(select.checkValidity(),false);
});

test('license requirements follow exact status without losing entered values or step state',()=>{
  const partial=read('Protect-Website/Views/Shared/_QuoteLicenseApplicability.cshtml');
  const dom=new JSDOM(`<form><select name="DLStatus"><option>Valid</option><option>Not Licensed</option><option>Expired</option></select><label for="license">License<span class="text-danger">*</span></label><input id="license" name="DriversLicenseNumber" data-license-detail required value="retained"><select name="Drivers[1].DLStatus"><option>Valid</option><option>Not Licensed</option></select><input name="Drivers[1].AgeLicensed" data-license-detail required disabled></form>`,{runScripts:'outside-only'});
  const w=dom.window;try{
    w.eval(partial.match(/<script>([\s\S]*)<\/script>/)[1]);
    const form=w.document.querySelector('form'), field=form.elements.namedItem('DriversLicenseNumber'), status=form.elements.namedItem('DLStatus');
    const change=value=>{status.value=value;status.dispatchEvent(new w.Event('change',{bubbles:true}));};
    change('Not Licensed');assert.equal(field.required,false);assert.equal(field.dataset.originalRequired,'0');assert.equal(field.value,'retained');assert.equal(field.labels[0].querySelector('span').hidden,true);
    change('Expired');assert.equal(field.required,true);assert.equal(field.value,'retained');assert.equal(field.labels[0].querySelector('span').hidden,false);
    field.value='';assert.equal(field.checkValidity(),false);change('Not Licensed');assert.equal(field.checkValidity(),true);
    change('');assert.equal(field.required,true,'missing status cannot relax validation');
    const driver=form.elements.namedItem('Drivers[1].AgeLicensed');assert.equal(driver.disabled,true);assert.equal(driver.required,true);
    form.elements.namedItem('Drivers[1].DLStatus').value='Not Licensed';w.LegendQuoteLicenseRequirements.refresh(form);assert.equal(driver.required,false);assert.equal(driver.disabled,true);
  }finally{w.close();}
});

test('dynamic labels remain unique and associated through removal and reindexing',()=>{
  const dom=new JSDOM('<form><div id="driversContainer"></div><div id="vehiclesContainer"></div><div id="accidentsContainer"></div><div id="violationsContainer"></div><div id="compLossesContainer"></div></form>',{runScripts:'outside-only'});
  const w=dom.window;try{
    const a=auto.indexOf('    function associateDynamicLabels('), b=auto.indexOf('    function addDriver()',a);
    assert.ok(a>=0);w.eval(auto.slice(a,b));
    const groups=[['driversContainer','driverTemplate','driver-card','Drivers','data-driver'],['vehiclesContainer','vehicleTemplate','vehicle-card','Vehicles','data-vehicle'],['accidentsContainer','accidentTemplate',null,'Accidents'],['violationsContainer','violationTemplate',null,'Violations'],['compLossesContainer','compLossTemplate',null,'CompLosses']];
    for(const [id,tpl,cls,prefix,attr] of groups){
      const container=w.document.getElementById(id), html=template(auto,tpl);
      for(let i=0;i<3;i++)container.insertAdjacentHTML('beforeend',html.replaceAll('__i__',String(i)));
      const reindex=()=>cls?w.reindexCards(id,cls,prefix,attr):w.reindexMini(id,prefix);
      reindex();container.children[1].remove();reindex();container.insertAdjacentHTML('beforeend',html.replaceAll('__i__','2'));reindex();
      for(const field of container.querySelectorAll('input,select,textarea')){
        if(field.type==='hidden')continue;
        assert.ok(field.labels.length>0,`${field.name} missing associated label`);
        assert.equal(w.document.querySelectorAll(`[id="${field.id}"]`).length,1,`${field.id} duplicated`);
      }
    }
  }finally{w.close();}
});

test('Life modal cycles backward and forward, including an authored hidden close button',()=>{
  const index=read('Protect-Website/Views/Quote/Index.cshtml');
  const modal=read('Protect-Website/Views/Shared/_LifeModal.cshtml').replace(/^@model string\s*/,'').replaceAll('@Model','');
  const script=index.match(/<script>([\s\S]*?)<\/script>/)[1];
  for(const hidden of [false,true]){
    const dom=new JSDOM(`<a href="/login">Background</a><div class="life-tile" tabindex="0"></div>${modal}`,{runScripts:'outside-only'}), w=dom.window;
    try{
      const close=w.document.querySelector('.life-modal-close');close.hidden=hidden;
      w.eval(script);w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
      const tile=w.document.querySelector('.life-tile'), dialog=w.document.querySelector('[role="dialog"]');
      tile.click();
      const links=[...dialog.querySelectorAll('a')],first=hidden?links[0]:close,last=links.at(-1);
      const tab=(from,shiftKey)=>{from.focus();const e=new w.KeyboardEvent('keydown',{key:'Tab',shiftKey,bubbles:true,cancelable:true});from.dispatchEvent(e);return e;};
      assert.equal(tab(dialog,true).defaultPrevented,true);assert.equal(w.document.activeElement,last);
      assert.equal(tab(first,true).defaultPrevented,true);assert.equal(w.document.activeElement,last);
      assert.equal(tab(last,false).defaultPrevented,true);assert.equal(w.document.activeElement,first);
      assert.equal(tab(links[2],true).defaultPrevented,false,'interior traversal remains native');
      last.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));assert.equal(w.document.activeElement,tile);
      tile.click();assert.equal(tab(first,true).defaultPrevented,true);assert.equal(w.document.activeElement,last);
    }finally{w.close();}
  }
});
