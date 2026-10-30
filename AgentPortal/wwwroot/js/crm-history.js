(() => {
  'use strict';
  const root = document.getElementById('crm-history'); if (!root) return;
  const filters = document.getElementById('history-filters'), form = document.getElementById('history-interaction');
  const status = document.getElementById('history-status'), results = document.getElementById('history-results');
  const dialog = document.getElementById('history-detail'), detail = document.getElementById('history-detail-content');
  const scope = new URLSearchParams({kind:root.dataset.kind,id:root.dataset.relationshipId});
  const token = filters.querySelector('[name="__RequestVerificationToken"]').value;
  const zone = root.dataset.timezone || 'America/Phoenix';
  let snapshotUtc = null;
  let page = 0, generation = 0, requestId = crypto.randomUUID(), savedPayload = null;
  const draftKey='crm-history-draft:'+root.dataset.draftScope+':'+scope.toString();
  function rememberDraft(){try{sessionStorage.setItem(draftKey,JSON.stringify({requestId,savedPayload,fields:Object.fromEntries(new FormData(form))}));}catch{status.textContent='Draft is open in this page; browser recovery storage is unavailable.';}}
  try {const draft=JSON.parse(sessionStorage.getItem(draftKey)||'null');if(draft){requestId=draft.requestId;savedPayload=draft.savedPayload;for(const [key,value] of Object.entries(draft.fields||{})){const input=form.elements.namedItem(key);if(input)input.value=value;}status.textContent=savedPayload?'Recovered uncertain save. Retry unchanged to confirm its durable record.':'Recovered unfiled draft.';}}catch{}
  form.addEventListener('input',rememberDraft);
  window.addEventListener('beforeunload',event=>{if(field(form,'account').trim() || savedPayload){rememberDraft();event.preventDefault();event.returnValue='';}});
  const field = (f,n) => f.elements.namedItem(n).value;
  const toDate = value => new Date(/(?:Z|[+-]\d{2}:\d{2})$/.test(value) ? value : value+'Z');
  const date = value => value ? new Intl.DateTimeFormat(undefined,{dateStyle:'medium',timeStyle:'short',timeZone:zone}).format(toDate(value)) : 'Time unavailable';
  const endpoint = (path,params = {}) => '/CrmHistory/'+path+'?'+new URLSearchParams([...scope,...Object.entries(params)]);
  async function json(url, options) { const r = await fetch(url,{credentials:'same-origin',...options}); if(!r.ok){const error=new Error(r.status===403 || r.status===404 ? 'This record is unavailable under your current access.' : 'Request failed. Your existing history has not been changed.');error.status=r.status;throw error;} return r.json(); }
  function renderValue(value, parent, label) {
    if(label) {const h=document.createElement('h3');h.className='h6 mt-3';h.textContent=label;parent.append(h);}
    if(value && typeof value==='object') {for(const [key,item] of Object.entries(value)) renderValue(item,parent,key);}
    else {const p=document.createElement('p');p.style.whiteSpace='pre-wrap';p.textContent=value===null ? 'Evidence unavailable' : value==='' ? 'Empty submitted value' : String(value);parent.append(p);}
  }
  function renderSubmission(snapshot) {
    const evidence=snapshot?.SubmittedFormEvidence;
    if(!evidence){renderValue(snapshot,detail,'Original captured fields — historical question wording may be unavailable');return;}
    renderValue(evidence.formType,detail,'Submitted form');
    renderValue(evidence.completeness,detail,'Evidence completeness');
    renderValue(evidence.answers,detail,'Validated submitted answers — authoritative captured values');
    renderValue('The presentation below is browser-reported context. It may differ from the validated answers above and does not independently establish what was shown or selected.',detail,'Presentation evidence limitation');
    for(const field of evidence.presentationEvidence?.fields || []){
      renderValue(field.question || field.name+' — question wording unavailable',detail,'Question');
      renderValue(field.answer,detail,'Browser-reported response — not authoritative');
      if(field.optionLabel)renderValue(field.optionLabel,detail,'Browser-reported option label');
      if(field.choiceValue!==null)renderValue(field.choiceValue,detail,'Browser-reported option value');
      renderValue(field.state,detail,'Presentation / answer state (browser-reported)');
      if(field.choices?.length)for(const option of field.choices)renderValue(option.label+' — '+(option.selected?'Selected':'Not selected'),detail,'Available choice');
    }
    for(const authorization of evidence.authorizations || []){
      renderValue(authorization.text,detail,'Authorization disclosure');
      renderValue(authorization.status,detail,'Submitted checkbox state');
      renderValue(authorization.source,detail,'Disclosure evidence');
      renderValue(authorization.signatureEvidence,detail,'Signature evidence');
    }
    const raw=document.createElement('details'),rawTitle=document.createElement('summary');rawTitle.textContent='Original captured fields and provenance';raw.append(rawTitle);renderValue(snapshot,raw);detail.append(raw);
  }
  async function open(entry) {
    detail.replaceChildren(); dialog.showModal();
    try {const data=await json(endpoint('Entry/'+entry.id));
      const {contentJson, ...provenance}=data.entry;const provenanceBox=document.createElement('details'),provenanceTitle=document.createElement('summary');provenanceTitle.textContent='Record provenance and original IDs';provenanceBox.append(provenanceTitle);renderValue(provenance,provenanceBox);detail.append(provenanceBox);const permalink=document.createElement('a');permalink.textContent='Link to this record';permalink.href='/CrmHistory?'+new URLSearchParams([...scope,['entry',entry.id]]);detail.append(permalink);
      try {renderValue(JSON.parse(data.entry.contentJson),detail,'Filed content');}catch {renderValue(data.entry.contentJson,detail,'Filed content');}
      if(data.entry.source==='ProtectedDocument'){const download=document.createElement('a');download.textContent='Download verified original evidence';download.href=endpoint('Evidence/'+data.entry.id);download.className='btn btn-outline-primary';detail.append(download);}
      if(data.association)renderValue(data.association,detail,'Association');
      if(data.submission){const association=document.getElementById('history-association');association.elements.namedItem('submissionId').value=data.entry.submissionId;association.dataset.expectedVersion=data.submission.associationVersion;}
      else delete document.getElementById('history-association').dataset.expectedVersion;
      if(data.submission){const {snapshotJson,...meta}=data.submission;renderValue(meta,detail,'Submission');try{renderSubmission(JSON.parse(snapshotJson));}catch{renderValue(snapshotJson,detail,'Original captured fields');}}
    } catch(e){detail.textContent=e.message;}
  }
  async function load() {
    const current=++generation;status.textContent='Loading history…';
    try {
      const query={page,range:field(filters,'range'),fromDate:field(filters,'from'),throughDate:field(filters,'through'),type:field(filters,'type'),product:field(filters,'product'),owner:field(filters,'owner'),caseReference:field(filters,'caseReference'),search:field(filters,'search'),obligations:field(filters,'obligations'),status:field(filters,'status'),evidence:field(filters,'evidence'),recordedTime:field(filters,'recordedTime'),...(snapshotUtc?{snapshotUtc}:{})};
      const data=await json(endpoint('Entries',query)); if(current!==generation)return;
      snapshotUtc=data.snapshotUtc;document.getElementById('history-preservation-status').textContent=`${data.openObligations} open follow-ups / complaints · ${data.overdueObligations} overdue`+(data.activeHolds?.length?' · Active preservation instructions: '+data.activeHolds.join(', '):'');
      results.replaceChildren(); let priorGroup='';
      for(const entry of data.entries){
        const group=field(filters,'group'); const day=field(filters,'recordedTime')==='true' ? new Intl.DateTimeFormat('en-CA',{timeZone:zone,year:'numeric',month:'2-digit',day:'2-digit'}).format(toDate(entry.recordedUtc)) : entry.occurredDate || (entry.occurredUtc ? new Intl.DateTimeFormat('en-CA',{timeZone:zone,year:'numeric',month:'2-digit',day:'2-digit'}).format(toDate(entry.occurredUtc)) : 'Unknown date');
        let key=group==='month' ? day.slice(0,7) : day;
        if(group==='week' && /^\d{4}-\d{2}-\d{2}$/.test(day)){const d=new Date(day+'T12:00:00Z');d.setUTCDate(d.getUTCDate()-(d.getUTCDay()+6)%7);key='Week of '+d.toISOString().slice(0,10);}
        if(group!=='none' && key!==priorGroup){const h=document.createElement('h2');h.className='h5 mt-3';h.textContent=key;results.append(h);priorGroup=key;}
        const wrap=document.createElement('div');wrap.className='col-md-6';const button=document.createElement('button');button.className='card p-3 text-start w-100';button.type='button';
        const title=document.createElement('strong');title.textContent=(entry.formName||entry.type)+(entry.product?' · '+entry.product:'');button.append(title);
        for(const text of [entry.occurredUtc?date(entry.occurredUtc):entry.occurredDate?entry.occurredDate+' · date only':'Unknown event date · time unavailable',entry.source+' · '+entry.status+(entry.formVersion?' · version '+entry.formVersion:entry.type==='FormSubmission'?' · historical version unavailable':''),entry.evidenceStatus,entry.caseReference||'']){const line=document.createElement('span');line.textContent=text;button.append(line);}
        button.addEventListener('click',()=>open(entry));wrap.append(button);results.append(wrap);
      }
      status.textContent=`${data.count} records · ${zone} · page ${page+1}`+(data.count?'':' · No matching history');
      document.getElementById('history-previous').disabled=page===0;document.getElementById('history-next').disabled=(page+1)*50>=data.count;
    }catch(e){status.textContent=e.message;}
  }
  filters.addEventListener('submit',e=>{e.preventDefault();page=0;snapshotUtc=null;load();});filters.addEventListener('reset',()=>setTimeout(()=>{page=0;snapshotUtc=null;load();},0));
  document.getElementById('history-previous').onclick=()=>{if(page>0){page--;load();}};document.getElementById('history-next').onclick=()=>{page++;load();};document.getElementById('history-close').onclick=()=>dialog.close();
  form.addEventListener('submit',async e=>{e.preventDefault();const submit=form.querySelector('button');submit.disabled=true;
    const payload={requestId,type:field(form,'type'),account:field(form,'account'),occurredDate:field(form,'occurredUtc')?null:field(form,'occurredDate')||null,occurredUtc:field(form,'occurredUtc')?field(form,'occurredUtc')+':00Z':null,timeZoneId:zone,channel:field(form,'channel'),direction:field(form,'direction'),participants:field(form,'participants'),outcome:field(form,'outcome'),nextAction:field(form,'nextAction'),caseReference:field(form,'caseReference'),product:field(form,'product'),relatedEntryId:field(form,'relatedEntryId')||null,assignedOwner:field(form,'assignedOwner')||null,dueUtc:field(form,'dueUtc')?field(form,'dueUtc')+':00Z':null,evidenceSource:field(form,'evidenceSource'),evidenceReference:field(form,'evidenceReference'),evidenceState:field(form,'evidenceState')};
    if(savedPayload && JSON.stringify(payload)!==savedPayload){status.textContent='The prior save is uncertain. Retry the unchanged entry before editing it.';submit.disabled=false;return;}
    savedPayload=JSON.stringify(payload);rememberDraft();status.textContent='Saving…';
    try{const response=await json(endpoint('Entries'),{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':token},body:savedPayload});if(!response.saved||!response.id)throw new Error('Save confirmation missing; retry this entry.');form.reset();requestId=crypto.randomUUID();savedPayload=null;try{sessionStorage.removeItem(draftKey);}catch{}page=0;snapshotUtc=null;await load();status.textContent='Filed · '+response.id;}catch(error){if([400,403,404,422].includes(error.status)){savedPayload=null;rememberDraft();status.textContent='Not saved. '+error.message;}else status.textContent=error.message+' Save may be uncertain; retry the unchanged entry.';}finally{submit.disabled=false;}
  });
  const evidenceForm=document.getElementById('history-evidence');
  const evidenceDraftKey=draftKey+':evidence';
  let evidenceRequest=crypto.randomUUID(),evidencePending=null;
  try{const draft=JSON.parse(sessionStorage.getItem(evidenceDraftKey)||'null');if(draft){evidenceRequest=draft.requestId;evidencePending=draft.signature;for(const [name,value] of Object.entries(draft.fields||{}))evidenceForm.elements.namedItem(name).value=value;status.textContent='Recovered uncertain evidence upload. Reselect the same PDF and retry unchanged.';}}catch{}
  evidenceForm.addEventListener('submit',async event=>{event.preventDefault();const button=evidenceForm.querySelector('button');button.disabled=true;
    try{const body=new FormData(evidenceForm),file=body.get('file');
      if(!file || file.size>20*1024*1024)throw new Error('Select the original PDF, no larger than 20 MB.');
      const hash=Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',await file.arrayBuffer())),x=>x.toString(16).padStart(2,'0')).join('');
      const fields={source:String(body.get('source')),completeness:String(body.get('completeness')),previousVersion:String(body.get('previousVersion')||'')};
      const signature=JSON.stringify({hash,bytes:file.size,name:file.name,...fields});
      if(evidencePending&&evidencePending!==signature)throw new Error('The prior upload is uncertain. Reselect the same file and retry unchanged before filing a new version.');
      evidencePending=signature;
      sessionStorage.setItem(evidenceDraftKey,JSON.stringify({requestId:evidenceRequest,signature,fields}));
      body.set('requestId',evidenceRequest);if(!body.get('previousVersion'))body.delete('previousVersion');
      const result=await json(endpoint('Evidence'),{method:'POST',headers:{'RequestVerificationToken':token},body});
      if(!result.saved||!result.id)throw new Error('Evidence confirmation missing. Retry unchanged.');
      sessionStorage.removeItem(evidenceDraftKey);evidencePending=null;evidenceRequest=crypto.randomUUID();evidenceForm.reset();page=0;snapshotUtc=null;await load();status.textContent='Evidence version filed · '+result.id;
    }catch(error){if([400,403,404,413,422].includes(error.status)){evidencePending=null;sessionStorage.removeItem(evidenceDraftKey);}status.textContent=error.message+' An uncertain response is not confirmation.';}finally{button.disabled=false;}
  });
  function recoverActionDraft(actionForm,key){
    try{const draft=JSON.parse(sessionStorage.getItem(key)||'null');if(draft){for(const [name,value] of Object.entries(draft.fields||{})){const input=actionForm.elements.namedItem(name);if(input)input.value=value;}if(draft.expectedVersion)actionForm.dataset.expectedVersion=draft.expectedVersion;return draft;}}catch{}
    return {requestId:crypto.randomUUID(),pending:null};
  }
  function retainActionDraft(actionForm,key,requestId,pending){sessionStorage.setItem(key,JSON.stringify({requestId,pending,fields:Object.fromEntries(new FormData(actionForm)),expectedVersion:actionForm.dataset.expectedVersion||null}));}
  const preservationForm=document.getElementById('history-preservation');
  const preservationKey=draftKey+':preservation';
  const preservationDraft=preservationForm?recoverActionDraft(preservationForm,preservationKey):{requestId:crypto.randomUUID(),pending:null};
  let preservationRequest=preservationDraft.requestId,preservationPending=preservationDraft.pending;
  preservationForm?.addEventListener('submit',async event=>{event.preventDefault();const button=preservationForm.querySelector('button');button.disabled=true;
    const body=JSON.stringify({requestId:preservationRequest,release:field(preservationForm,'release')==='true',holdEntryId:field(preservationForm,'holdEntryId')||null,reason:field(preservationForm,'reason')});
    try{if(preservationPending&&preservationPending!==body)throw new Error('Retry the unchanged uncertain instruction before editing.');preservationPending=body;retainActionDraft(preservationForm,preservationKey,preservationRequest,body);const result=await json(endpoint('Preservation'),{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':token},body});if(!result.saved||!result.id)throw new Error('No durable instruction confirmed. Retry unchanged.');sessionStorage.removeItem(preservationKey);preservationPending=null;preservationRequest=crypto.randomUUID();preservationForm.reset();page=0;snapshotUtc=null;await load();status.textContent='Preservation instruction filed · '+result.id;}catch(error){if([400,403,404,422].includes(error.status)){preservationPending=null;sessionStorage.removeItem(preservationKey);}status.textContent=error.message;}finally{button.disabled=false;}
  });
  const associationForm=document.getElementById('history-association');
  const normalizedGuid=value=>/^[0-9a-f]{32}$/i.test(value)?value.replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/,'$1-$2-$3-$4-$5'):value;
  const associationKey=draftKey+':association',associationDraft=recoverActionDraft(associationForm,associationKey);
  let associationRequest=associationDraft.requestId,associationPending=associationDraft.pending;
  associationForm.addEventListener('submit',async event=>{event.preventDefault();const button=associationForm.querySelector('button');button.disabled=true;
    const body=JSON.stringify({requestId:associationRequest,submissionId:field(associationForm,'submissionId'),expectedVersion:associationForm.dataset.expectedVersion||null,targetLeadId:field(associationForm,'targetKind')==='lead'?field(associationForm,'targetLeadId'):'',targetClientProfileId:field(associationForm,'targetKind')==='profile'?normalizedGuid(field(associationForm,'targetLeadId')):null,reason:field(associationForm,'reason')});
    try {if(associationPending && body!==associationPending)throw new Error('Retry the unchanged uncertain correction before editing it.');associationPending=body;retainActionDraft(associationForm,associationKey,associationRequest,body);const result=await json(endpoint('CorrectAssociation'),{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':token},body});if(!result.saved||!result.id)throw new Error('Correction confirmation missing. Retry unchanged.');sessionStorage.removeItem(associationKey);associationPending=null;associationRequest=crypto.randomUUID();associationForm.reset();delete associationForm.dataset.expectedVersion;page=0;snapshotUtc=null;await load();status.textContent='Association correction filed · '+result.id;}catch(error){if([400,403,404,409,422].includes(error.status)){associationPending=null;sessionStorage.removeItem(associationKey);}status.textContent=error.message;}finally{button.disabled=false;}
  });
  let unlinkedCursor=0;
  document.getElementById('history-unlinked-load').onclick=async()=>{const list=document.getElementById('history-unlinked-results');list.replaceChildren();try{const data=await json(endpoint('UnlinkedSubmissions',{afterRowId:unlinkedCursor}));unlinkedCursor=data.nextAfterRowId||0;document.getElementById('history-unlinked-load').textContent=data.hasMore?'Load next unlinked submissions':'Reload unlinked submissions';for(const item of data.submissions){const row=document.createElement('p');row.textContent=[item.firstName,item.lastName,item.sourcePageKey,date(item.createdUtc)].filter(Boolean).join(' · ');const action=document.createElement('button');action.type='button';action.className='btn btn-outline-secondary ms-2';action.textContent='Reconcile';action.onclick=async()=>{action.disabled=true;try{const saved=await json(endpoint('ReconcileSubmission/'+item.leadId),{method:'POST',headers:{'RequestVerificationToken':token}});if(!saved.saved||!saved.id)throw new Error('No durable reconciliation confirmed.');row.textContent='Reconciled to lead '+saved.id;}catch(error){status.textContent=error.message;action.disabled=false;}};row.append(action);list.append(row);}if(!data.submissions.length)list.textContent='No unlinked submissions in your ownership scope.';if(data.hasMore){const more=document.createElement('p');more.textContent='Showing 100 records. Use Load next to continue; reconciliation does not shift the cursor.';list.append(more);}}catch(error){list.textContent=error.message;}};
  document.getElementById('history-export').onclick=async()=>{
    try {const r=await fetch(endpoint('Export'),{method:'POST',credentials:'same-origin',headers:{'RequestVerificationToken':token}});if(!r.ok)throw new Error('Export failed; no complete export was produced.');const blob=await r.blob();const url=URL.createObjectURL(blob);const a=document.createElement('a');a.href=url;a.download='case-history.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);status.textContent='Case history exported. Review the included scope and limitations.';}catch(e){status.textContent=e.message;}
  };
  json(endpoint('Backfill'),{method:'POST',headers:{'RequestVerificationToken':token}}).then(async()=>{await load();const entryId=new URLSearchParams(location.search).get('entry');if(entryId&&/^[0-9a-f-]{36}$/i.test(entryId))await open({id:entryId});}).catch(e=>status.textContent=e.message);
})();
