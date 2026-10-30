// Invoked only by the admitted CRM xUnit lane against its synthetic loopback host.
const assert=require('node:assert/strict'),{spawn}=require('node:child_process'),fs=require('node:fs'),path=require('node:path');
(async()=>{
  assert.equal(process.env.GITHUB_ACTIONS,'true');assert.equal(process.env.GITHUB_REPOSITORY,'MYLEGND/masterapp');assert.equal(process.env.RUNNER_OS,'Linux');
  assert.equal(JSON.parse(fs.readFileSync(process.env.GITHUB_EVENT_PATH,'utf8')).repository.private,false);
  const origin=new URL(process.argv[2]);assert.equal(origin.protocol,'http:');assert.equal(origin.hostname,'127.0.0.1');assert.ok(origin.port);
  const deadline=Date.now()+150000;let driver,session,driverOrigin,log='',stage='startup';
  const pause=ms=>new Promise(r=>setTimeout(r,ms));
  const command=async(method,endpoint,body)=>{
    assert.ok(Date.now()<deadline,'Browser acceptance deadline exceeded at '+stage);
    stage=method+' '+endpoint;
    console.log('CRM_HOSTED_BROWSER:COMMAND:'+stage);
    const response=await fetch(driverOrigin+endpoint,{method,headers:{'content-type':'application/json'},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(8000)});
    const result=await response.json();assert.ok(response.ok,JSON.stringify(result));if(result.value?.error)throw new Error(JSON.stringify(result.value));return result.value;
  };
  const execute=script=>command('POST','/session/'+session+'/execute/sync',{script,args:[]});
  const wait=async script=>{const until=Date.now()+10000;while(Date.now()<until){if(await execute('return '+script))return;await pause(50);}assert.fail('Browser condition failed: '+script);};
  const keys=values=>command('POST','/session/'+session+'/actions',{actions:[{type:'key',id:'keyboard',actions:values.flatMap(value=>[{type:'keyDown',value},{type:'keyUp',value}])}]});
  try{
    const binary=process.env.CHROMEWEBDRIVER?path.join(process.env.CHROMEWEBDRIVER,'chromedriver'):'chromedriver';
    driver=spawn(binary,['--port=0','--allowed-ips=127.0.0.1'],{detached:true,stdio:['ignore','pipe','pipe']});
    driver.on('error',e=>{log+=e.message;});for(const stream of [driver.stdout,driver.stderr])stream.on('data',data=>{log=(log+data.toString()).slice(-12000);});
    const startup=Date.now()+8000;while(!/started successfully on port (\d+)/i.test(log)&&Date.now()<startup&&driver.exitCode===null)await pause(40);
    const port=log.match(/started successfully on port (\d+)/i)?.[1];assert.ok(port,'ChromeDriver failed to start');driverOrigin='http://127.0.0.1:'+port;
    session=(await command('POST','/session',{capabilities:{alwaysMatch:{browserName:'chrome','goog:chromeOptions':{binary:'/usr/bin/google-chrome',args:['--headless=new','--disable-background-networking','--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1']}}}})).sessionId;assert.ok(session);
    for(const width of [1180,393]){
      console.log('CRM_HOSTED_BROWSER:VIEWPORT:'+width);
      await command('POST','/session/'+session+'/window/rect',{width,height:800});
      await command('POST','/session/'+session+'/goog/cdp/execute',{cmd:'Emulation.setDeviceMetricsOverride',params:{width,height:800,deviceScaleFactor:1,mobile:width<600}});
      await command('POST','/session/'+session+'/url',{url:origin.origin+'/__synthetic/signin'});
      await wait('!!document.getElementById("crm-history")');
      await execute('document.querySelector("#history-filters [name=type]").value="FormSubmission";const viewport=document.createElement("meta");viewport.name="viewport";viewport.content="width=device-width, initial-scale=1";document.head.append(viewport);const css=document.createElement("link");css.onload=()=>window.__crmCssReady=true;css.rel="stylesheet";css.href="/lib/bootstrap/dist/css/bootstrap.min.css";document.head.append(css);const js=document.createElement("script");js.src="/js/crm-history.js";document.body.append(js);');
      await wait('window.__crmCssReady===true');assert.equal(await execute('return innerWidth'),width);
      await wait('document.querySelectorAll("#history-results button").length===2');
      await execute('document.querySelector("#history-results button").focus();');await keys(['\uE007']);
      await wait('document.getElementById("history-detail").open && document.getElementById("history-detail-content").textContent.includes("END-OF-DISCLOSURE")');
      assert.equal(await execute('return document.getElementById("history-detail").getAttribute("aria-labelledby")'),'history-detail-title');
      assert.equal(await execute('return document.activeElement.id'),'history-close');
      const text=await execute('return document.getElementById("history-detail-content").textContent');
      for(const value of ['FINAL-SYNTHETIC-ANSWER','Original synthetic disclosure.','END-OF-DISCLOSURE','Not checked','Evidence unavailable','false','Amount','Empty submitted value'])assert.ok(text.includes(value),value);
      assert.ok(await execute('const d=document.getElementById("history-detail"),r=d.getBoundingClientRect();return d.scrollHeight>d.clientHeight&&r.left>=0&&r.right<=innerWidth+1&&r.bottom<=innerHeight+1;'));
      await execute('const p=[...document.querySelectorAll("#history-detail-content p")].find(x=>x.textContent.includes("END-OF-DISCLOSURE"));p.scrollIntoView({block:"end"});');
      assert.ok(await execute('const p=[...document.querySelectorAll("#history-detail-content p")].find(x=>x.textContent.includes("END-OF-DISCLOSURE")),d=document.getElementById("history-detail");return p.getBoundingClientRect().bottom<=d.getBoundingClientRect().bottom+1;'));
      await keys(['\uE004']);assert.ok(await execute('return document.getElementById("history-detail").contains(document.activeElement)'));
      await execute('document.getElementById("history-close").focus();');
      await command('POST','/session/'+session+'/actions',{actions:[{type:'key',id:'keyboard',actions:[{type:'keyDown',value:'\uE008'},{type:'keyDown',value:'\uE004'},{type:'keyUp',value:'\uE004'},{type:'keyUp',value:'\uE008'}]}]});
      assert.ok(await execute('return document.getElementById("history-detail").contains(document.activeElement)&&document.activeElement.id!=="history-close"'));
      await keys(['\uE004']);assert.equal(await execute('return document.activeElement.id'),'history-close');
      await keys(['\uE00C']);await wait('!document.getElementById("history-detail").open');
      assert.ok(await execute('return document.activeElement.matches("#history-results button")'));
      await keys([' ']);await wait('document.getElementById("history-detail").open');
      await execute('document.getElementById("history-close").focus();');await keys(['\uE007']);await wait('!document.getElementById("history-detail").open');
      await execute('document.querySelector("#history-filters [name=range]").value="today";document.querySelector("#history-filters button").focus();');await keys(['\uE007']);
      await wait('document.getElementById("history-status").textContent.startsWith("1 records")&&document.querySelectorAll("#history-results button").length===1');
      assert.equal(await execute('return document.querySelector("#history-filters [name=range]").value'),'today');
    }
    console.log('CRM_HOSTED_BROWSER:PASS:desktop-mobile-keyboard-actual-view-and-endpoints:synthetic-only:not-live');
  }catch(error){
    console.error('CRM_HOSTED_BROWSER:FAIL:'+stage+':'+error.message);
    throw error;
  }finally{
    if(session)await fetch(driverOrigin+'/session/'+session,{method:'DELETE',signal:AbortSignal.timeout(3000)}).catch(()=>{});
    if(driver?.pid){
      // Own the driver/browser process group, including children that retain pipes.
      try{process.kill(-driver.pid,'SIGTERM');}catch(error){if(error.code!=='ESRCH')throw error;}
      await pause(150);
      try{process.kill(-driver.pid,'SIGKILL');}catch(error){if(error.code!=='ESRCH')throw error;}
      driver.stdout.destroy();driver.stderr.destroy();
    }
  }
})().catch(error=>{console.error(error.message);process.exitCode=1;});
