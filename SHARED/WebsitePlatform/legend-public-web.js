(() => {
  'use strict';
  const toggle=document.querySelector('[data-public-nav-toggle]');
  const nav=document.querySelector('[data-public-nav]');
  if(toggle&&nav){
    const close=({restoreFocus=false}={})=>{
      const wasOpen=nav.dataset.open==='true';
      nav.dataset.open='false';
      toggle.setAttribute('aria-expanded','false');
      if(wasOpen&&restoreFocus){
        try{toggle.focus({preventScroll:true});}catch{toggle.focus();}
      }
    };
    const open=()=>{
      nav.dataset.open='true';
      toggle.setAttribute('aria-expanded','true');
      nav.scrollTop=0;
    };
    toggle.addEventListener('click',event=>{
      event.preventDefault();
      if(nav.dataset.open==='true') close({restoreFocus:true});
      else open();
    });
    nav.addEventListener('click',e=>{if(e.target.closest('a')) close();});
    document.addEventListener('click',event=>{
      if(nav.dataset.open!=='true') return;
      if(nav.contains(event.target)||toggle.contains(event.target)) return;
      close();
    });
    document.addEventListener('keydown',e=>{if(e.key==='Escape') close({restoreFocus:true});});
    window.addEventListener('resize',()=>{if((nav.closest('.legend-cms-preview')?.clientWidth??window.innerWidth)>980) close();},{passive:true});
  }
})();