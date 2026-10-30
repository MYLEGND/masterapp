(() => {
  'use strict';
  // Submission evidence only: no network, identifiers, IP/device collection or analytics.
  const supported = new Set(['/Quote/Life','/Quote/Term-Life','/Quote/Whole-Life','/Quote/Final-Expense','/Quote/Mortgage-Protection','/Quote/IUL','/Quote/Auto','/Quote/Home','/Quote/Commercial','/Quote/Disability','/Quote/Dental-Vision-Hearing','/RiskAssessment']);
  const eligible = form => supported.has(new URL(form.action, location.href).pathname.replace(/\/$/,'')) || form.dataset.insuranceHistory === 'true';
  for (const form of document.forms) {
    if (!eligible(form)) continue;
    const presented = new WeakSet();
    const controls = () => Array.from(form.elements).filter(x => x.name && ['INPUT','SELECT','TEXTAREA'].includes(x.tagName) && !['hidden','submit','button','reset','password','file'].includes(x.type));
    const observe = () => controls().forEach(x => { if (x.getClientRects().length && getComputedStyle(x).visibility !== 'hidden') presented.add(x); });
    observe();
    let queued = false;
    new MutationObserver(() => { if (!queued) { queued = true; requestAnimationFrame(() => { queued = false; observe(); }); } }).observe(form,{subtree:true,childList:true,attributes:true,attributeFilter:['hidden','style','class','disabled']});
    form.addEventListener('focusin', event => { if (event.target.name) presented.add(event.target); });
    const text = element => (element?.textContent || '').replace(/\s+/g,' ').trim().slice(0,4000);
    const snapshot = () => {
      observe();
      const fields = controls().map((field, order) => {
        const label = field.id ? form.querySelector('label[for="'+CSS.escape(field.id)+'"]') : null;
        const group = field.closest('[role="radiogroup"], fieldset');
        const labelled = element => (element?.getAttribute('aria-labelledby') || '').split(/\s+/).filter(Boolean).map(id=>text(document.getElementById(id))).join(' ');
        const optionLabel = field.getAttribute('data-label') || text(label || field.closest('label')) || null;
        const question = labelled(group) || text(group?.querySelector('legend')) || labelled(field) || field.getAttribute('aria-label') || optionLabel;

        const isChoice = field.type === 'checkbox' || field.type === 'radio';
        const answer = field.tagName === 'SELECT' && field.multiple ? Array.from(field.selectedOptions,x=>x.value) : field.type === 'radio' ? (field.checked ? field.value : null) : field.type === 'checkbox' ? field.checked : field.value;
        const wasPresented = presented.has(field);
        const state = !wasPresented ? 'Not presented (browser-reported)' : isChoice ? field.checked ? 'Checked' : 'Unchecked' : answer === '' || Array.isArray(answer) && !answer.length ? 'Presented but unanswered' : 'Answered';
        return {name:field.name,order,question,fieldType:field.type,group:labelled(group)||text(group?.querySelector('legend'))||null,optionLabel,selected:isChoice?field.checked:null,
          state,presented:wasPresented,disabledAtSubmission:field.disabled,answer,
          choiceValue:isChoice?field.value:null,choices:field.tagName==='SELECT'?Array.from(field.options,o=>({value:o.value,label:text(o),selected:o.selected})):null};
      });
      return {schemaVersion:1,source:'Submitted browser presentation; not independently attested',fields};
    };
    const put = data => {
      const json = JSON.stringify(snapshot());
      // Fail visibly through the ordinary submission if a legitimate form exceeds the bound.
      if (json.length > 250000) throw new Error('The form evidence is too large. Please contact your agent before submitting.');
      if (data) data.set('HistoryPresentationJson',json);
      else {let input=form.querySelector('input[name="HistoryPresentationJson"]');if(!input){input=document.createElement('input');input.type='hidden';input.name='HistoryPresentationJson';form.append(input);}input.value=json;}
    };
    form.addEventListener('submit',event=>{try{put();}catch(error){event.preventDefault();alert(error.message);}},true);
    form.addEventListener('formdata',event=>put(event.formData));
  }
})();
