(() => {
  "use strict";
  const iconPaths = {
    protect:'<path d="M12 3 19 6v5c0 5-3.2 8.5-7 10-3.8-1.5-7-5-7-10V6l7-3z"/><path d="M9 12l2 2 4-5"/>',
    family:'<path d="M8 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6z"/><path d="M16 10a2.5 2.5 0 1 0 0-5"/><path d="M3 20a5 5 0 0 1 10 0"/><path d="M13 15a4 4 0 0 1 7 3"/>',
    home:'<path d="M3 11 12 4l9 7"/><path d="M5 10v10h14V10"/><path d="M9 20v-6h6v6"/>',
    auto:'<path d="M5 16h14l-1.5-6h-11z"/><path d="M7 16v2M17 16v2"/><path d="M8 10l1-3h6l1 3"/>',
    health:'<path d="M12 20s-8-4.5-8-10a4 4 0 0 1 7-2.6A4 4 0 0 1 20 10c0 5.5-8 10-8 10z"/><path d="M8 12h3l1-3 2 6 1-3h2"/>',
    income:'<path d="M12 2v20"/><path d="M17 6.5C16 5.5 14.5 5 12.5 5 10 5 8 6.2 8 8s1.8 2.6 4.5 3.2C15.2 11.8 17 12.7 17 15s-2 4-4.5 4C10.2 19 8.6 18.3 7.5 17"/>',
    business:'<path d="M4 8h16v11H4z"/><path d="M9 8V5h6v3"/><path d="M4 12h16"/><path d="M10 12v2h4v-2"/>',
    contact:'<rect x="3" y="5" width="18" height="14" rx="2"/><path d="m4 7 8 6 8-6"/>',
    default:'<circle cx="12" cy="12" r="8"/><path d="M12 8v8M8 12h8"/>'
  };
  const selectIcon = text => {
    const t=(text||"").toLowerCase();
    if(/life|legacy|protect|coverage|insurance/.test(t)) return "protect";
    if(/family|people|household/.test(t)) return "family";
    if(/home|mortgage|property/.test(t)) return "home";
    if(/auto|vehicle|car/.test(t)) return "auto";
    if(/health|dental|vision|hearing|medical/.test(t)) return "health";
    if(/disability|income|financial/.test(t)) return "income";
    if(/commercial|business/.test(t)) return "business";
    if(/contact|email|support/.test(t)) return "contact";
    return "default";
  };
  const icon = key => {
    const span=document.createElement("span");
    span.className="protect-icon";
    span.setAttribute("aria-hidden","true");
    span.innerHTML='<svg viewBox="0 0 24 24">'+iconPaths[key]+'</svg>';
    return span;
  };
  function decorate(){
    const main=document.querySelector("main.layout-content");
    if(!main || document.body.classList.contains("standalone-quote-landing")) return;
    const candidates=[...main.children].filter(el=>el.nodeType===1);
    candidates.forEach((el,index)=>{
      if(el.matches("script,style")) return;
      el.classList.add("protect-flow-section");
      const heading=el.querySelector("h1,h2");
      if(heading && !heading.closest(".quote-hero,.legal-hero,.contact-clarity__header")){
        let wrap=heading.parentElement;
        if(wrap && !wrap.classList.contains("protect-flow-heading")){
          wrap.classList.add("protect-flow-heading");
        }
        if(!wrap?.querySelector(":scope > .protect-eyebrow")){
          const eyebrow=document.createElement("div");
          eyebrow.className="protect-eyebrow";
          eyebrow.append(icon(selectIcon(heading.textContent)));
          const label=document.createElement("span");
          label.textContent=index===0 ? "LEGEND® PROTECT" : "Protection built with clarity";
          eyebrow.append(label);
          heading.before(eyebrow);
        }
      }
    });
    document.querySelectorAll(".card,.feature-card,.service-card,.coverage-card").forEach(el=>el.classList.add("protect-design-card"));
  }
  if(document.readyState==="loading") document.addEventListener("DOMContentLoaded",decorate,{once:true}); else decorate();
})();