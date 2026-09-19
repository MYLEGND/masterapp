(() => {
  'use strict';
  const toggle=document.querySelector('[data-public-nav-toggle]');
  const nav=document.querySelector('[data-public-nav]');
  if(toggle&&nav){
    const close=()=>{nav.dataset.open='false';toggle.setAttribute('aria-expanded','false');};
    toggle.addEventListener('click',()=>{const open=nav.dataset.open==='true';nav.dataset.open=open?'false':'true';toggle.setAttribute('aria-expanded',open?'false':'true');});
    nav.addEventListener('click',e=>{if(e.target.closest('a')) close();});
    document.addEventListener('keydown',e=>{if(e.key==='Escape') close();});
    window.addEventListener('resize',()=>{if((nav.closest('.legend-cms-preview')?.clientWidth??window.innerWidth)>980) close();});
  }
})();