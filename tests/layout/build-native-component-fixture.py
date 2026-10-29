"""Assemble offline component inputs from this checkout; never serve repository files."""
from pathlib import Path
import argparse
import base64
import hashlib
import json
import re
import subprocess
from datetime import datetime, timedelta, timezone



def quote_razor_projection(text, values, conditions):
    """Finite synthetic model projection, not a general Razor renderer.

    Preserve source HTML, choose only explicitly declared branch values, and
    reject every unknown expression. Option loops retain original node templates
    with one synthetic model row: never claim full-route/content-height proof.
    """
    import html
    text=text.replace('you@example.com','you&#64;example.com')
    def end(text, start, opening='{', closing='}'):
        level=0;quote=None;escape=False
        for i in range(start,len(text)):
            char=text[i]
            if quote:
                if escape:escape=False
                elif char=='\\':escape=True
                elif char==quote:quote=None
                continue
            if char in ['"',"'"]:quote=char;continue
            if char==opening:level+=1
            elif char==closing:
                level-=1
                if level==0:return i
        raise ValueError('Unbalanced quote projection source')
    pattern=re.compile(r'@if\s*\(([^\n]*?)\)\s*\{')
    while (match:=pattern.search(text)):
        condition=match[1]
        if condition not in conditions:raise ValueError('Unknown quote condition: '+condition)
        close=end(text,match.end()-1);tail=close+1
        choices=[(conditions[condition],text[match.end():close])]
        while True:
            otherwise=re.match(r'\s*else(?:\s+if\s*\(([^\n]*?)\))?\s*\{',text[tail:])
            if not otherwise:break
            branch=otherwise[1]
            if branch is not None and branch not in conditions:raise ValueError('Unknown quote chained condition: '+branch)
            begin=tail+otherwise.end()-1;finish=end(text,begin)
            choices.append((True if branch is None else conditions[branch],text[begin+1:finish]));tail=finish+1
            if branch is None:break
        selected=next((body for chosen,body in choices if chosen),'')
        text=text[:match.start()]+quote_razor_projection(selected,values,conditions)+text[tail:]
    if re.search(r'\belse(?:\s+if\s*\([^\n]*\))?\s*\{',text):raise ValueError('Unresolved quote branch chain')
    loops={'var chip in contactFirstProofChips','var opt in firstDiscoveryStep.Options','var opt in secondDiscoveryStep.Options',
           'var opt in coverageAmountStep.Options','var opt in tobaccoStep.Options','var markerAge in ageSliderMarkerValues'}
    pattern=re.compile(r'@foreach\s*\(([^\n]*?)\)\s*\{')
    while (match:=pattern.search(text)):
        if match[1] not in loops:raise ValueError('Unknown quote model loop: '+match[1])
        close=end(text,match.end()-1)
        text=text[:match.start()]+quote_razor_projection(text[match.end():close],values,conditions)+text[close+1:]
    while (match:=re.search(r'@\{',text)):
        close=end(text,match.end()-1);body=text[match.end():close].strip()
        if not body.startswith('var choiceMeta = ') or not body.endswith(';'):raise ValueError('Unknown quote Razor local block')
        text=text[:match.start()]+text[close+1:]
    text=re.sub(r'^\s*var (?:isChecked|choiceMeta) = (?:IsStepOptionChecked|GetChoiceCardMeta)\((?:firstDiscoveryFieldName|secondDiscoveryFieldName|\"(?:CoverageAmountOption|TobaccoUse)\"), opt.Code\);[ \t]*$', '', text, flags=re.M)
    if re.search(r'\bvar \w+ =',text):raise ValueError('Unprojected quote loop code')
    text=text.replace('@Html.AntiForgeryToken()','')
    text=text.replace('@await Html.PartialAsync("_QuoteMetaSignalBootstrap")','')
    text=re.sub(r'@(CheckedIf|SelectedClassIf)\((?:form\.[A-Za-z]+|selectedAgeRange) == "[^"\n]+"\)', '', text)
    while (match:=re.search(r'@\(',text)):
        close=end(text,match.end()-1,'(',')');expression=text[match.end():close]
        key='('+expression+')'
        if key not in values:raise ValueError('Unknown quote expression: '+key)
        text=text[:match.start()]+html.escape(str(values[key]),quote=True)+text[close+1:]
    def variable(match):
        key=match[1]
        if key not in values:raise ValueError('Unknown quote model value: '+key)
        return html.escape(str(values[key]),quote=True)
    text=re.sub(r'@([A-Za-z][A-Za-z0-9_.]*(?:\[[0-9]+\])?)',variable,text)
    if re.search(r'@[A-Za-z({]',text):raise ValueError('Unresolved quote Razor')
    return text


def quote_projection_values():
    names='''heroStepIntroTitle heroStepIntroHelper heroSubheadline heroHeadline heroTrustAriaLabel heroTrustNote heroTrustPersonInitials heroTrustPersonName heroTrustPersonRole heroTrustPersonSupport primaryStartCtaText primaryStartCtaNote contactFirstBannerAlt contactFirstTrustInitials contactFirstTrustLabelName contactFirstTrustIntroLine agentTrustHeadline resolvedAgentTrustMessage contactFirstProcessingHeading contactFirstProcessingHelper contactFirstProcessingNote stepTwoProgressText finalStepHeading finalStepHelper stepTwoContactKickerText stepTwoContactTitleText stepTwoContactCopyText stepTwoSubmitNoteText finalSubmitButtonText consentPromptText consentErrorText contactMethodHelperText contactMethodErrorText step1IntroTitle step1IntroHelper firstDiscoveryStep.Question secondDiscoveryStep.Question coverageAmountQuestionText coverageAmountErrorText ageStepQuestionText tobaccoStep.Question stepOneContinueButtonText choiceMeta.Helper choiceMeta.Kicker choiceMeta.VisualValue opt.Label chip'''.split()
    values={key:'Synthetic '+key.split('.')[-1] for key in names}
    values.update({'cfg.PostAction':'FixtureBlocked','cfg.OfferKey':'life','cfg.ProductType':'life','contactFirstBannerImageUrl':'/images/ads/life-top-banner.png','pageVariant':'contact_first_education_v1','pageMode':'site_mode','formKey':'fixture-quote','pageKey':'fixture_quote','example.com':'@example.com','form.FirstName':'','form.LastName':'','form.Phone':'','form.Email':'','form.State':'',
        'form.ProtectFocus':'','form.Answer1':'','form.Answer2':'','form.RecommendationPrimaryKey':'','form.RecommendationPrimaryTitle':'',
        'form.RecommendationSecondaryKey':'','form.RecommendationSecondaryTitle':'','choiceMeta.ToneClass':'', 'opt.Code':'fixture-option',
        'firstDiscoveryFieldName':'ProtectingWho','firstDiscoveryQuestionId':'lbl-step1-protect','secondDiscoveryFieldName':'CoverageGoal',
        'secondDiscoveryQuestionId':'lbl-step1-goal','ageValue':'40','initialAgeValue':'40','markerAge':'40','minSelectableAge':'18','maxSelectableAge':'85',
        'heroProofChips[0]':'Income review','heroProofChips[1]':'Simple questions','heroProofChips[2]':'No obligation'})
    conditions={'usesContactFirstShell':True,'isLandingView':True,'isLowFrictionOptionsVariant':False,'!isLandingView':False,
        '!isLandingView && !string.IsNullOrWhiteSpace(heroSubheadline)':False,
        '!string.IsNullOrWhiteSpace(heroStepIntroTitle) || !string.IsNullOrWhiteSpace(heroStepIntroHelper)':True,
        '!string.IsNullOrWhiteSpace(heroStepIntroTitle)':True,'!string.IsNullOrWhiteSpace(heroStepIntroHelper)':True,
        '!string.IsNullOrWhiteSpace(heroTrustPersonImageUrl)':False,'!string.IsNullOrWhiteSpace(agentSlug)':False,
        '!string.IsNullOrWhiteSpace(step1IntroTitle)':True,'!string.IsNullOrWhiteSpace(step1IntroHelper)':True,
        '!string.Equals(choiceMeta.VisualMode, "none", StringComparison.OrdinalIgnoreCase)':False,
        'usesAgeBeforeTobaccoFlow':True,'!string.IsNullOrWhiteSpace(contactFirstTrustImageUrl)':False,
        '!string.IsNullOrWhiteSpace(agentTrustNpn)':False,'showCarrierReviewBeforeContact':False,'showCarrierDisclosure':False,
        'showPreContactEducationSummary':False,'showGenericCoverageOverview':False,'showInlineMobileTrustBlock':False,
        '!usesLeanContactFields':False,'hasServerErrors':False}
    expressions={'isChecked ? "checked" : null':'','hasExplicitAgeSelection ? "true" : "false"':'false','form.AcknowledgedDisclaimer ? "checked" : null':'','pageKey':'fixture_quote', 'hasExplicitAgeSelection ? "is-active" : ""':'', 'hasExplicitAgeSelection ? "false" : "true"':'true', 'markerAge == maxSelectableAge ? "+" : string.Empty':'', '!showContactStepByDefault ? "hidden" : ""':'hidden',
        '(showContactStepByDefault ? "hidden" : null)':'', '(showContactStepByDefault ? null : "hidden")':'hidden',
        'string.IsNullOrWhiteSpace(finalStepHelper) ? "hidden" : null':'',
        '!string.IsNullOrWhiteSpace(contactFirstTrustImageUrl) ? "hidden" : null':'',
        'form.MarketingEmailConsent ? "checked" : null':'',
        'Context?.Request is { HasFormContentType: true } submissionRequest && Guid.TryParse(submissionRequest.Form["SubmissionId"], out var submissionToken) ? submissionToken : Guid.NewGuid()':'00000000-0000-4000-8000-000000000001'}
    expressions.update({'hasExplicitAgeSelection ? ageValue : "Select age"': 'Select age', 'hasExplicitAgeSelection ? ageValue : (form.Answer4 ?? string.Empty)': '', 'hasExplicitAgeSelection ? ageValue : string.Empty': '', 'hasExplicitAgeSelection ? selectedAgeBracket : string.Empty': '', 'isLandingView ? "lq-container" : "container lq-container"': 'lq-container', 'form.CoverageAmount?.ToString() ?? selectedCoverageAmountOption': '', 'showContactStepByDefault ? "hidden" : ""': '', 'string.Equals(choiceMeta.VisualMode, "none", StringComparison.OrdinalIgnoreCase) ? " no-visual" : ""': ' no-visual', 'string.IsNullOrWhiteSpace(form.Answer3) ? form.TobaccoUse : form.Answer3': '', 'string.IsNullOrWhiteSpace(form.BestTimeToContact) ? "Not specified" : form.BestTimeToContact': 'Not specified', 'string.IsNullOrWhiteSpace(form.ContactMethod) ? "Phone" : form.ContactMethod': 'Phone', 'string.Equals(firstDiscoveryFieldName, "CoverageGoal", StringComparison.OrdinalIgnoreCase) ? "What matters most" : "Who depends on this"': 'Who depends on this', 'string.Equals(firstDiscoveryFieldName, "CoverageGoal", StringComparison.OrdinalIgnoreCase) ? "err-step1-coverage-goal" : "err-step1-protecting-who"': 'err-step1-protecting-who', 'string.Equals(firstDiscoveryFieldName, "CoverageGoal", StringComparison.OrdinalIgnoreCase) ? coverageGoalErrorText : protectingWhoErrorText': 'Select one option.', 'string.Equals(secondDiscoveryFieldName, "CoverageGoal", StringComparison.OrdinalIgnoreCase) ? "What matters most" : "Who depends on this"': 'What matters most', 'string.Equals(secondDiscoveryFieldName, "CoverageGoal", StringComparison.OrdinalIgnoreCase) ? "err-step1-coverage-goal" : "err-step1-protecting-who"': 'err-step1-coverage-goal', 'string.Equals(secondDiscoveryFieldName, "CoverageGoal", StringComparison.OrdinalIgnoreCase) ? coverageGoalErrorText : protectingWhoErrorText': 'Select one option.'})
    values.update({'('+key+')':value for key,value in expressions.items()})
    return values,conditions


def validate_protect_layout_projection(layout):
    """Bind component projection to the current request-owned native slot."""
    anchors = [
        '<body class="legend-public--protect @bodyClasses" style="@compiledPublication?.BodyStyle">',
        'var mainClasses = isStandaloneQuoteLanding ? "layout-content" : "public-main layout-content";',
        '<main id="main" class="@mainClasses">\n    @if (compiledPublication is not null && !compiledPublication.NativeSlot)\n    {\n        IgnoreBody();\n        @Html.Raw(compiledPublication.Main)\n    }\n    else\n    {\n        @RenderBody()\n    }\n</main>',
        '@if (compiledPublication?.Footer is not null)\n{\n    @Html.Raw(compiledPublication.Footer)\n}\nelse\n{\n<footer class="site-footer">'
    ]
    if any(layout.count(anchor) != 1 for anchor in anchors) or layout.count('@RenderBody()') != 1:
        raise ValueError('Quote layout authority changed')


def protect_scripture_projection(layout):
    """Exact production modal markup and its owning inline lifecycle."""
    validate_protect_layout_projection(layout)
    start = '<div id="psalmsModal"'
    end = '\n\n@if (compiledPublication?.Footer is not null)'
    if layout.count(start) != 1 or layout.count(end) != 1:
        raise ValueError('Protect scripture source boundary changed')
    before, after = layout.split(start)
    modal_markup, _ = after.split(end)
    modal_markup = start + modal_markup
    scripts = [m[1] for m in re.finditer(r'<script>([\s\S]*?)</script>', layout)
               if "const trigger = document.getElementById('psalmsTrigger');" in m[1]]
    if len(scripts) != 1 or '@' in modal_markup or '@' in scripts[0]:
        raise ValueError('Protect scripture source boundary changed')
    return modal_markup, '<script data-source="Protect-Website/Views/Shared/_Layout.cshtml">' + scripts[0] + '</script>'


def build_quote_fixtures(root, out, raw):
    """Source-component projection only: not Razor execution or a submitted quote."""
    source=lambda path:raw(path).decode('utf-8')
    controller=source('Protect-Website/Controllers/LifeQuoteController.cs')
    for anchor in ['var usesContactFirstSiteControl = !isLandingRequested;', 'PageMode: isLandingRequested ? "paid_landing" : "site_mode"', ': usesContactFirstSiteControl\n                    ? ContactFirstEducationLandingVariant']:
        if controller.count(anchor)!=1:raise ValueError('Quote route authority changed: '+anchor)
    layout=source('Protect-Website/Views/Shared/_Layout.cshtml')
    validate_protect_layout_projection(layout)
    scripture_markup, scripture_script = protect_scripture_projection(layout)
    review_assets=source('Protect-Website/Views/Shared/_QuoteFinalReviewAssets.cshtml')
    if review_assets.count('href="~/css/quote-final-review.css"')!=1:raise ValueError('Quote review CSS authority changed')
    css_paths=['Protect-Website/wwwroot/lib/bootstrap/dist/css/bootstrap.min.css','Protect-Website/wwwroot/css/site.css','Legend-Design/legend-web-foundation.css','SHARED/WebsitePlatform/legend-public-web.css','SHARED/WebsitePlatform/public-inquiry-form.css','Protect-Website/wwwroot/css/quote-final-review.css']
    common=''.join('<style data-source="'+p+'">'+source(p)+'</style>' for p in css_paths)
    cms=source('SHARED/WebsitePlatform/legend-public-cms.js')
    navigation=source('SHARED/WebsitePlatform/legend-public-web.js')
    if '</script' in cms.lower():raise ValueError('Unsafe inline CMS terminator')
    routes=[('life','life','Life','quote_life'),('term-life','term','Life','quote_term_life'),('whole-life','wholelife','Life','quote_whole_life'),('final-expense','finalexpense','Life','quote_final_expense'),('mortgage-protection','mortgage','Life','quote_mortgage_protection'),('iul','iul','Life','quote_iul'),('disability','disability','Disability','quote_disability'),('dental-vision-hearing','dvh','DentalVisionHearing','quote_dvh')]
    for route,offer,view,page_key in routes:
        if view=='Life':
            route_name={'life':'Life','term-life':'Term-Life','whole-life':'Whole-Life','final-expense':'Final-Expense','mortgage-protection':'Mortgage-Protection','iul':'IUL'}[route]
            if controller.count('[HttpGet("'+route_name+'")]')!=1 or 'PageKey = "'+page_key+'"' not in controller:raise ValueError('Life route/config mapping changed')
            expected='return RenderWizard(LifeOfferKeys.Life);' if route=='life' else '=> RenderWizard("'+offer+'");'
            if expected not in controller:raise ValueError('Life route offer mapping changed')
    route_mapping=[{'route':'/quote/'+r,'view':'Protect-Website/Views/Quote/'+v+'.cshtml','pageKey':p,'offer':o} for r,o,v,p in routes]
    routes=[r for r in routes if r[0] in ['life','disability','dental-vision-hearing']]
    outputs={};assets={};proof=[]
    for route,offer,view,page_key in routes:
        path='Protect-Website/Views/Quote/'+view+'.cshtml';text=source(path)
        if view!='Life':
            owner=source('Protect-Website/Controllers/'+view+'QuoteController.cs')
            if 'isLandingPage: false' not in owner or '? (isLandingPage ? ContactFirstEducationVariant : WebsitePageVariant)' not in owner or '? (isLandingPage ? "paid_landing" : "site_mode")' not in owner:raise ValueError('Quote route mode authority changed:'+view)
        else:
            for anchor in ['var firstDiscoveryFieldName = usesGoalFirstDiscovery ? "CoverageGoal" : "ProtectingWho";', 'var firstDiscoveryQuestionId = usesGoalFirstDiscovery ? "lbl-step1-goal" : "lbl-step1-protect";']:
                if anchor not in text:raise ValueError('Life native identity authority changed')

        start_anchor='<div class="lq-hero-shell" data-protect-form-shell>' if view=='Life' else '<div class="dq-shell" data-protect-form-shell>'
        if text.count(start_anchor)!=1:raise ValueError('Quote shell boundary changed:'+view)
        start=text.index(start_anchor);end=text.index('<script',start) if view=='Life' else text.index('@section Scripts',start)
        preceding=text[:start].rsplit('</style>',1)[-1].strip()
        if not re.fullmatch(r'<svg\b[\s\S]*</svg>',preceding) or '@' in preceding:raise ValueError('Native symbol definition boundary changed')
        values,conditions=quote_projection_values()
        values.update({'(pageKey)':page_key,'pageKey':page_key,'formKey':page_key+'_form','pageVariant':('contact_first_education_v1' if view=='Life' else 'website'),'cfg.OfferKey':offer,'cfg.ProductType':('life_general' if offer=='life' else offer),'contactFirstBannerImageUrl':'/images/ads/'+{'life':'life','term':'term-life','wholelife':'whole-life','finalexpense':'final-expense','mortgage':'mortgage-protection','iul':'iul'}.get(offer,offer)+'-top-banner.png'})
        markup=quote_razor_projection(text[start:end],values,conditions).replace('src="~/','src="/')
        markup=re.sub(r'\sasp-(?:action|controller|append-version)="[^"]*"','',markup)
        if re.search(r'\b(?:asp-|@(?:if|foreach|Model))',markup):raise ValueError('Unresolved quote attribute')
        if len(re.findall(r'data-cms-native-step="[0-9]+"',markup))!=5:raise ValueError('Quote five native steps changed')
        review='lifeStep2EstimateSlot' if view=='Life' else ('disabilityReviewSummary' if view=='Disability' else 'dvhReviewSummary')
        if len(re.findall(r'\sid="'+re.escape(review)+r'"',markup))!=1:raise ValueError('Quote actual review mount changed')
        if view=='Life' and (len(re.findall(r'\sid="lifeStep2EstimateSlot"',markup))!=1 or 'id="lifeStep2EstimateSummary"' in markup):raise ValueError('Cold Life review mount changed')
        blocks=list(re.finditer(r'<style(?:\s[^>]*)?>([\s\S]*?)</style>',text))
        if not blocks:raise ValueError('Missing quote native styles')
        # Do not promote noscript styles or duplicate in-component blocks. Keep
        # pre/post-component cascade order; native noscript markup stays inert.
        def style_block(i,match):
            return '<style data-source="'+path+'#style-'+str(i)+'">'+match[1].replace('@@','@')+'</style>'
        native_before=''.join(style_block(i,m) for i,m in enumerate(blocks) if m.end()<=start)
        native_after=''.join(style_block(i,m) for i,m in enumerate(blocks) if m.start()>=end)
        # Assets retain exact native src and bytes. No remote image, generated placeholder or repository server.
        for asset in set(re.findall(r'<img\b[^>]*\bsrc="([^"]+)"',markup)):
            if not re.fullmatch(r'/images/ads/[a-z-]+\.png',asset):raise ValueError('Unknown quote static asset:'+asset)
            filename='quote-asset-'+asset.rsplit('/',1)[1];data=raw('Protect-Website/wwwroot'+asset)
            if not data.startswith(b'\x89PNG\r\n\x1a\n'):raise ValueError('Quote asset is not PNG')
            (out/filename).write_bytes(data);assets[asset]=filename
        template='life_wizard' if view=='Life' else ('disability_quote' if view=='Disability' else 'dvh_quote')
        body='legend-public--protect '+('life-landing life-offer-'+offer+' ' if view=='Life' else ('disability-quote ' if view=='Disability' else 'dvh-quote '))+'site-mode page-variant-'+('contact_first_education_v1' if view=='Life' else 'website')+' page-mode-site_mode'
        config={'route':'/quote/'+route,'template':'protect_template:'+template,'pageKey':page_key}
        bootstrap=r'''<script>
(()=>{'use strict';const cfg=CONFIG;const q=new URLSearchParams(location.search);const materialize=q.get('legendMaterialize')==='1';
const key='native-quote:'+cfg.route;window.__quoteErrors=[];window.__quoteRequests=[];window.__quoteMaterialized=null;
window.addEventListener('error',e=>window.__quoteErrors.push(String(e.message).slice(0,240)));window.addEventListener('unhandledrejection',e=>window.__quoteErrors.push(String(e.reason).slice(0,240)));
window.alert=x=>{window.__quoteErrors.push('alert:'+String(x).slice(0,240));};
const deny=kind=>{window.__quoteErrors.push(kind);throw new Error('Blocked fixture transport:'+kind);};
window.XMLHttpRequest=function(){deny('XMLHttpRequest');};window.WebSocket=function(){deny('WebSocket');};window.EventSource=function(){deny('EventSource');};navigator.sendBeacon=()=>{deny('beacon');};
document.addEventListener('submit',e=>{e.preventDefault();deny('submit');},true);
const doc={version:3,faviconImageDataUrl:null,store:{enabled:false,navigationLabel:'Store',cartIcon:'cart',cartIconSizePx:28},breakpoints:[{key:'mobile',label:'Mobile',minWidth:0,maxWidth:767,isSystem:true},{key:'tablet',label:'Tablet',minWidth:768,maxWidth:1199,isSystem:true},{key:'desktop',label:'Desktop',minWidth:1200,maxWidth:null,isSystem:true},{key:'fixture-wide',label:'Fixture wide',minWidth:1400,maxWidth:1700,isSystem:false}],shell:{header:[],footer:[]},pages:{},reusableComponents:{},collections:{},theme:{}};
doc.pages[cfg.route]={title:'Synthetic source component',navigation:{isDeleted:false},systemTemplateKey:cfg.template,composition:[]};
if(!materialize){const snapshot=JSON.parse(sessionStorage.getItem(key)||'null');if(!snapshot||snapshot.route!==cfg.route)throw new Error('Missing source materialization');doc.pages[cfg.route].composition=snapshot.composition;doc.shell=snapshot.shell;if(q.get('width')==='720')doc.theme.formMaxWidth=720;}
if(q.get('header')==='1'){
 const node=(id,type,tag,extra={})=>({id,type,tag,style:{},layout:{mode:'free'},breakpointStyles:{},breakpointLayouts:{},signals:[],animations:[],children:[],...extra});
 doc.shell.header=[node('fixture.header','container','header',{className:'site-header',style:{heightPx:96},children:[
 node('fixture.brand','container','div',{className:'brand-wordmark',style:{widthPercent:14,offsetYPx:-18.5},children:[node('fixture.name','text','strong',{text:q.get('tenant')==='other'?'Cedar Advisory':'LEGEND®',style:{fontSize:q.get('tenant')==='other'?32:44}})]}),
 node('fixture.nav','container','nav',{className:'nav',systemKey:'primary_navigation',children:[node('fixture.link','link','a',{text:'Contact',href:'/contact'})]})]})];
}
window.__quoteOriginalNodes=[...document.querySelectorAll('main form,main input,main img,main figure,main svg')];window.__quoteDocument=JSON.stringify(doc);window.__quoteDocumentObject=doc;
window.addEventListener('message',e=>{if(e.origin===location.origin&&e.source===window&&e.data?.type==='legend-site-materialized-page'){if(e.data.route!==cfg.route)throw new Error('Materialization route mismatch');window.__quoteMaterialized=e.data;sessionStorage.setItem(key,JSON.stringify(e.data));}});
window.LEGEND_PUBLIC_CMS_CONTEXT={siteKey:'protect',apiBase:'',pages:[]};
const payload={siteKey:'protect',revision:'r1',document:doc,business:null,store:null,publicationScopeId:'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',ctaCatalog:{options:[]},signalCatalog:{events:[],matchingFields:[],runtimeEnabled:true},capabilities:{compositionV3:true,canPublish:false},legacyMigration:materialize?{}:null};
window.fetch=async(input,options={})=>{const u=new URL(String(input),location.origin);const method=options.method||'GET';window.__quoteRequests.push({method,path:u.pathname});if(method!=='GET'||u.origin!==location.origin)deny(method+':'+u.pathname);let value;if(u.pathname==='/api/website-content/manage')value=payload;else if(u.pathname==='/api/website-content/manage/agent/contract')value={schema:'legend-website-studio-agent/v1',promptTemplate:'compact'};else if(u.pathname==='/api/website-content/manage/agent/summary')value={schema:'legend-creative-workspace/v1',siteKey:'protect',revision:'r1',pages:[],capabilities:{capabilities:[]}};else deny('GET:'+u.pathname);return{ok:true,status:200,json:async()=>structuredClone(value)};};
if(q.get('public')==='1')window.LEGEND_PUBLIC_CMS_RENDER_INPUT={...payload,server:true};
})();</script>'''.replace('CONFIG',json.dumps(config))
        html='<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src \'none\';script-src \'unsafe-inline\';style-src \'unsafe-inline\';img-src \'self\' data:;connect-src \'none\';form-action \'none\';base-uri \'none\'"><link rel="icon" href="data:,">'+common+'</head><body class="'+body+'"><main id="main" class="public-main layout-content">'+native_before+preceding+markup+native_after+'</main>'+scripture_markup+scripture_script+bootstrap+'<script>'+navigation+'</script><script>'+cms+'</script></body></html>'
        filename='quote-'+route+'.html';(out/filename).write_text(html);outputs[config['route']]=filename
        proof.append({'route':config['route'],'view':path,'inlineStyles':len(blocks),'branch':('Life normal quote contact-first site mode' if view=='Life' else 'Normal quote website/site_mode')+'; empty synthetic model','modelValues':values,'conditions':conditions})
    # Actual closed layout is tested before JS, after no-trigger initialization,
    # and with one synthetic opener exercising the unchanged production lifecycle.
    scripture_outputs = {}
    for mode in ['cold', 'no-trigger', 'trigger']:
        trigger = '<button id="psalmsTrigger" type="button" style="margin-top:300px">Open scripture</button>' if mode == 'trigger' else ''
        html = '<!doctype html><html><head><meta charset="utf-8"><link rel="icon" href="data:,"><meta http-equiv="Content-Security-Policy" content="default-src &apos;none&apos;;script-src &apos;unsafe-inline&apos;;style-src &apos;unsafe-inline&apos;;img-src data:;connect-src &apos;none&apos;;form-action &apos;none&apos;;base-uri &apos;none&apos;">' + common + '</head><body><button id="before" type="button">Before</button>' + trigger + scripture_markup + '<button id="after" type="button">After</button><div style="height:1800px"></div>' + ('' if mode == 'cold' else scripture_script) + '</body></html>'
        filename = 'scripture-' + mode + '.html'
        (out / filename).write_text(html)
        scripture_outputs[mode] = filename
    return {'scripture':scripture_outputs,'routes':outputs,'routeMapping':route_mapping,'assets':assets,'projection':proof,'limits':['Actual source shell, native static nodes, all inline styles and shipped CMS; finite synthetic model only.','One synthetic option row per model loop: NOT full-route height, content parity or Razor/controller execution.','No quote submission, provider, analytics, save or publish; all fetches finite offline GET fixtures.','Small effective viewport is not real browser zoom proof.','Header/footer, route handlers, native validation and server quote calculations are not executed; geometry is relative to component canvas.','Additional authored node/breakpoint width preservation remains in the existing website contract suite, not this native matrix.']}


def build(root, out):
    root, out = root.resolve(), out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    inputs = {}
    substitutions = []
    fixture_date = (datetime.now(timezone.utc) + timedelta(days=30)).date().isoformat()

    def raw(path):
        file = (root / path).resolve()
        if not file.is_relative_to(root) or not file.is_file():
            raise ValueError('Required canonical input missing: ' + path)
        data = file.read_bytes()
        inputs[path] = {'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest(),
                        'gitBlob': hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()}
        return data

    # Both hosts currently ship identical native library/brand bytes. Refuse a
    # future divergence rather than silently render Client with Portal assets.
    for suffix in ['lib/bootstrap/dist/css/bootstrap.min.css', 'lib/bootstrap/dist/js/bootstrap.bundle.min.js', 'images/company-icons/legend-protect-transparent.png']:
        if raw('AgentPortal/wwwroot/' + suffix) != raw('ClientApp/wwwroot/' + suffix):
            raise ValueError('Host asset divergence requires explicit fixture mapping: ' + suffix)

    def source(path):
        return raw(path).decode('utf-8')

    def style(path):
        text = source(path)
        if '</style' in text.lower():
            raise ValueError('Unexpected style terminator: ' + path)
        return '<style data-source="' + path + '">' + text + '</style>'

    def script(path):
        text = source(path)
        if '</script' in text.lower():
            raise ValueError('Unexpected script terminator: ' + path)
        return '<script data-source="' + path + '">' + text + '</script>'

    def image(path):
        return 'data:image/png;base64,' + base64.b64encode(raw(path)).decode()

    def exactly(text, old, new, count=1):
        if text.count(old) != count:
            raise ValueError('Canonical extraction anchor changed: ' + old)
        return text.replace(old, new)

    def balanced_end(text, start):
        depth = 0
        for i in range(start, len(text)):
            if text[i] == '{': depth += 1
            if text[i] == '}':
                depth -= 1
                if depth == 0: return i
        raise ValueError('Unbalanced Razor fixture block')

    def conditions(text, values):
        # Only known header conditionals, not a Razor interpreter.
        pattern = re.compile(r'@if \(([^\n]*?)\)\s*\{')
        while (match := pattern.search(text)):
            condition = match.group(1)
            if condition not in values:
                raise ValueError('Unreviewed header condition: ' + condition)
            end = balanced_end(text, match.end() - 1)
            body = conditions(text[match.end():end], values) if values[condition] else ''
            text = text[:match.start()] + body + text[end + 1:]
        return text

    profile = source('SHARED/Views/Shared/_PortalProfileMenu.cshtml')
    profile = exactly(profile, '@model Shared.PortalShell.PortalShellIdentity', '')
    for old, new in {'@Model.DisplayName': 'Synthetic Fixture Operator', '@Model.Initials': 'SF',
                     '@Model.RoleLabel': 'Synthetic role', '@Model.ProfileUrl': '#fixture-profile',
                     '@Model.AvatarUrl': 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==',
                     '@Html.AntiForgeryToken()': ''}.items():
        if old not in profile: raise ValueError('Missing profile substitution ' + old)
        profile = profile.replace(old, new)
    profile = exactly(profile, 'action="/Account/Logout"', 'action="#fixture-blocked-logout"')
    trigger = source('SHARED/Views/Messaging/_LifeHacTrigger.cshtml')
    message = source('SHARED/Views/Messaging/_NavButton.cshtml')
    brand = image('AgentPortal/wwwroot/images/company-icons/legend-protect-transparent.png')

    def header(host):
        path = ('AgentPortal' if host == 'authenticated' else 'ClientApp') + '/Views/Shared/_Layout.cshtml'
        layout = source(path)
        if layout.count('<header>') != 1 or layout.count('</header>') != 1:
            raise ValueError('Canonical header boundary changed')
        text = layout.split('<header>', 1)[1].split('</header>', 1)[0]
        if host == 'authenticated':
            # Select the existing ordinary signed-in founder branch; retain exact markup.
            start = text.index('@if (isOnboardingPublic)')
            end = text.index('<button class="nav-toggle"', start)
            brand_block = text[start:end]
            selected = re.search(r'else\s*\{\s*(<a class="navbar-brand".*?</a>)\s*\}', brand_block, re.S)
            if not selected: raise ValueError('Ordinary Portal brand branch changed')
            text = text[:start] + selected.group(1) + '\n' + text[end:]
            text = conditions(text, {'!isOnboardingPublic': True, '!isAssistant': True,
                                     'isFounder': True, 'isAssistant': False})
            text = exactly(text, '@(string.IsNullOrWhiteSpace(layoutClientUserId) ? null : layoutClientUserId)', '')
            text = exactly(text, '~/images/legend-ai/legendai.png', image('AgentPortal/wwwroot/images/legend-ai/legendai.png'))
        else:
            while '@{' in text:
                start = text.index('@{'); end = balanced_end(text, start + 1)
                text = text[:start] + text[end + 1:]
            text = conditions(text, {'isAgentView || isBusinessOwnerView': False,
                                     '!isAgentView && !string.IsNullOrWhiteSpace(messagingActorId)': True,
                                     'User.Identity?.IsAuthenticated == true': True})
        text = exactly(text, '@await Html.PartialAsync("~/Views/Messaging/_LifeHacTrigger.cshtml")', trigger)
        text = exactly(text, '@await Html.PartialAsync("~/Views/Messaging/_NavButton.cshtml")', message)
        text = exactly(text, '@await Html.PartialAsync("~/Views/Shared/_PortalProfileMenu.cshtml", portalShellIdentity)', profile)
        text = re.sub(r'@\*.*?\*@', '', text, flags=re.S)
        text = re.sub(r'@Active\("[A-Za-z]+"\)', '', text)
        text = text.replace('@portalShellIdentity.StoreUrl', '#fixture-store')
        text = text.replace('@Shared.Branding.LegendBrand.RegisteredTitle', 'LEGEND®')
        text = text.replace('~/images/company-icons/legend-protect-transparent.png', brand)
        text = re.sub(r' href="https?://[^"]*"', ' href="#fixture-external-disabled"', text)
        # Resolve anchor TagHelpers without changing nodes/classes/labels.
        text = re.sub(r'<a([^>]*?) asp-controller="([^"]+)"([^>]*)>',
                      lambda m: '<a' + m[1] + ' href="#fixture-' + m[2].lower() + '"' + m[3] + '>', text)
        text = re.sub(r'\sasp-[\w-]+="[^"]*"', '', text)
        if '@' in text or 'src="~/' in text:
            raise ValueError('Unresolved header Razor/asset')
        substitutions.append(host + ': canonical signed-in header branches; synthetic profile/avatar; inert hrefs; no TagHelper/Razor server evaluation')
        return '<header>' + text + '</header>'

    bootstrap = style('AgentPortal/wwwroot/lib/bootstrap/dist/css/bootstrap.min.css')
    bootjs = script('AgentPortal/wwwroot/lib/bootstrap/dist/js/bootstrap.bundle.min.js')
    qv = style('AgentPortal/wwwroot/css/qv-booking.css')
    qvjs = script('AgentPortal/wwwroot/js/scheduling/qv-booking.js')
    partial = source('AgentPortal/Views/Shared/_QuickViewBookingModal.cshtml')
    if '@' in partial: raise ValueError('Unexpected Razor in canonical booking partial')
    site = style('AgentPortal/wwwroot/css/site.css')
    founder = style('AgentPortal/wwwroot/css/legend-founder-ai.css')
    foundation = style('Legend-Design/legend-web-foundation.css')
    shell = style('Legend-Design/legend-app-shell.css')
    shared = style('SHARED/wwwroot/css/dashboard-home-shared.css')
    management = style('Legend-Design/legend-website-management.css')
    modaljs = script('SHARED/wwwroot/js/legend-modal.js')
    navjs = script('SHARED/wwwroot/js/legend-global-navigation.js')
    clients = style('AgentPortal/wwwroot/css/clients-index.css')
    client_mobile = style('ClientApp/wwwroot/css/client-mobile.css')
    forms = style('AgentPortal/wwwroot/css/legend-forms.css')
    connect = style('AgentPortal/wwwroot/css/legend-connect.css')
    onboard = source('AgentPortal/Views/Onboarding/Index.cshtml')
    onboard_styles = re.findall(r'<style>(.*?)</style>', onboard, re.S)
    if not onboard_styles: raise ValueError('Missing canonical Onboarding style')
    contrast = '''<section id="fixtureContrast" class="legend-app-content dashboard-page-shell"><article class="card" id="fixtureNavyCard"><div class="card-body">Synthetic card</div></article><article class="card" style="--bs-card-color:#112233;background:white" id="fixtureExplicitCard"><div class="card-body">Explicit consumer token</div></article><section class="legend-shell legend-form"><h2 class="fw-bold mb-3" style="color:var(--legend-navy)">Onboarding</h2><p class="text-muted">Synthetic introduction</p><label class="form-label" for="fixtureInput">Synthetic label</label><input class="form-control" id="fixtureInput" value="Synthetic value"></section><details class="lc-card lc-panel"><summary>Synthetic public copy</summary><p>Synthetic body</p></details></section>'''
    connect_view = source('AgentPortal/Views/LegendConnect/Index.cshtml')
    details = re.search(r'(<details[^>]*data-application-copy-panel[^>]*>)\s*(<summary>[^<]+</summary>)', connect_view)
    if not details: raise ValueError('Canonical application-copy details anchor changed')
    contrast = exactly(contrast, '<details class="lc-card lc-panel"><summary>Synthetic public copy</summary>', details.group(1) + details.group(2))
    label = re.search(r'<label asp-for="NewInvite.FirstName" class="([^"]+)"', onboard)
    field = re.search(r'<input asp-for="NewInvite.FirstName" class="([^"]+)"', onboard)
    intro = re.search(r'<p class="([^"]+)">Owner-only onboarding console\.', onboard)
    if not all([label, field, intro]): raise ValueError('Canonical Onboarding control anchors changed')
    contrast = exactly(contrast, 'class="form-label" for="fixtureInput"', 'class="' + label.group(1) + '" for="fixtureInput"')
    contrast = exactly(contrast, 'class="form-control" id="fixtureInput"', 'class="' + field.group(1) + '" id="fixtureInput"')
    contrast = exactly(contrast, '<p class="text-muted">Synthetic introduction', '<p class="' + intro.group(1) + '">Synthetic introduction')
    # Transport is intentionally finite and strict. No provider/client data leaves the fixture.
    safety = r'''<script>
window.__fixtureErrors=[];window.__fixtureCalls=[];window.__shown=[];
document.addEventListener('shown.bs.modal',e=>window.__shown.push(e.target.id));
addEventListener('error',e=>window.__fixtureErrors.push(e.message));
addEventListener('unhandledrejection',e=>window.__fixtureErrors.push(String(e.reason)));
addEventListener('securitypolicyviolation',e=>{if(!['font-src','img-src'].includes(e.effectiveDirective))window.__fixtureErrors.push('CSP:'+e.effectiveDirective)});
window.fetch=async(input,options={})=>{
 const deny=message=>{window.__fixtureErrors.push(message);throw Error(message)};
 const u=new URL(String(input),location.origin);const method=(options.method||'GET').toUpperCase();
 if(u.origin!==location.origin||method!=='GET'||window.__fixtureCalls.length>=100)deny('Blocked fixture transport');
 window.__fixtureCalls.push(u.pathname);
 if(u.pathname==='/mobile/agent/booking/appointments'&&!u.search)return new Response(JSON.stringify([{id:'synthetic-appointment',scheduledStartUtc:'2027-12-15T12:00:00Z',status:'Synthetic upcoming appointment'}]),{status:200});
 if(!['/mobile/agent/booking/availability','/calendar/day-availability'].includes(u.pathname))deny('Unexpected fixture GET:'+u.pathname);
 if([...u.searchParams.keys()].some(k=>!['date','v','excludeEventId'].includes(k)))deny('Unexpected fixture query');
 const date=u.searchParams.get('date');if(!/^\d{4}-\d{2}-\d{2}$/.test(date||''))deny('Fixture date required');
 return new Response(JSON.stringify({services:[{durationMinutes:30,name:'Synthetic thirty-minute appointment with localized-length label'}],slotIntervalMinutes:30,freeSlots:[{startIso:date+'T09:00:00',endIso:date+'T18:00:00'}]}),{status:200});
};
document.addEventListener('submit',e=>{e.preventDefault();e.stopImmediatePropagation();window.__fixtureErrors.push('Unexpected submit')},true);
document.addEventListener('click',e=>{if(e.target.closest('#btnBookAppointment,[data-fixture-forbid-mutation]')){e.preventDefault();e.stopImmediatePropagation();window.__fixtureErrors.push('Unexpected mutation click')}},true);
</script>'''
    safety = safety.replace('2027-12-15', fixture_date)
    adapter = '''<script>window.quickViewCalendarAdapter={fetchAvailability:(url,options)=>fetch(url,options),calendarNow:()=>new Date('2026-01-01T00:00:00')};</script>'''
    context = '''<div hidden><span id="dName">Synthetic Fixture Contact</span><span id="dEmail">fixture@example.invalid</span><span id="dPhone"></span><input id="dNextDate" value="2027-12-15"><input id="dNextText" value="Synthetic appointment"></div>'''
    context = context.replace('2027-12-15', fixture_date)
    csp = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'none'; form-action 'none'; base-uri 'none'"
    head = '<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"><meta http-equiv="Content-Security-Policy" content="' + csp + '">'
    home = source('ClientApp/Views/Home/Index.cshtml')
    home_hub = home.split('<div class="home-focus-hub" id="homeFocusHub" hidden>', 1)
    if len(home_hub) != 2 or home_hub[1].count('@section Scripts {') != 1: raise ValueError('Home hub boundary changed')
    home_markup = '<div class="home-focus-hub" id="homeFocusHub" hidden>' + home_hub[1].split('@section Scripts {', 1)[0]
    home_markup = home_markup.replace('@Shared.Branding.LegendBrand.RegisteredTitle', 'LEGEND®')
    home_markup = re.sub(r'<a([^>]*?) asp-controller="([^"]+)"([^>]*)>', lambda m: '<a' + m[1] + ' href="#fixture-protection-disabled"' + m[3] + '>', home_markup)
    home_markup = re.sub(r'\sasp-[\w-]+="[^"]*"', '', home_markup)
    home_scripts = re.findall(r'<script>([\s\S]*?)</script>', home_hub[1].split('@section Scripts {', 1)[1])
    if len(home_scripts) != 1 or '@' in home_markup: raise ValueError('Home script/markup extraction changed')
    home_script = '<script data-source="ClientApp/Views/Home/Index.cshtml">' + home_scripts[0] + '</script>'
    triggers = re.findall(r'<button type="button"\s+class="dashboard-tool home-focus-trigger"[\s\S]*?</button>', home)
    if len(triggers) != 1: raise ValueError('Home trigger template changed')
    home_trigger = triggers[0]
    for old,new in {'@area.Name':'Synthetic Focus Area','@area.Description':'Synthetic non-customer description','@area.Points[0]':'Synthetic first point','@area.Points[1]':'Synthetic second point','@area.Points[2]':'Synthetic third point','@Url.Content($"~/images/company-icons/{area.Icon}")':brand}.items():
        if old not in home_trigger: raise ValueError('Home trigger substitution changed:'+old)
        home_trigger = home_trigger.replace(old,new)
    if '@' in home_trigger: raise ValueError('Unresolved home trigger')
    substitutions.append('Client Home exact hub/trigger-template/local script; synthetic harmless focus-area text and existing brand image; inert ProtectionSnapshot link.')
    for host in ['authenticated', 'client']:
        css = bootstrap + foundation + shell + (site + founder + clients if host == 'authenticated' else '') + qv
        css += (client_mobile if host == 'client' else '') + shared + management + forms + connect
        css += ''.join('<style>' + x.replace('@@', '@') + '</style>' for x in onboard_styles)
        markup = '<div class="legend-app-content layout-content flex-grow-1"><main role="main"><button type="button" data-qv-booking-open id="fixtureBookingOpen">Open synthetic booking</button></main></div>'
        markup = markup.replace('</main>', contrast + '</main>')
        if host == 'client': markup = markup.replace('</main>', home_trigger + home_markup + '</main>')
        layout = source(('AgentPortal' if host == 'authenticated' else 'ClientApp') + '/Views/Shared/_Layout.cshtml')
        if host == 'authenticated':
            match = re.search(r'var layoutBodyClass = "([^"]+)"', layout)
        else:
            match = re.search(r'<body class="([^"@]+)@', layout)
        if not match: raise ValueError('Canonical body class anchor changed')
        body_class = match[1]
        if host == 'client':
            if 'string.Equals(layoutController, "Home", StringComparison.OrdinalIgnoreCase)' not in layout or '@(usesSharedDashboardHost ? " dashboard-shared-host" : "")' not in layout:
                raise ValueError('Client Home body condition changed')
            body_class += ' dashboard-shared-host'
        if not all(token in body_class.split() for token in ['legend-app', 'd-flex', 'flex-column', 'min-vh-100']):
            raise ValueError('Canonical body geometry changed')
        footers = re.findall(r'<footer\b[\s\S]*?</footer>', layout)
        if len(footers) != 1 or '@' in footers[0]: raise ValueError('Canonical footer extraction changed')
        footer = re.sub(r' href="[^"]*"', ' href="#fixture-footer-disabled"', footers[0])
        html = '<!doctype html><html><head>' + head + css + '</head><body class="' + body_class + '">' + safety + header(host) + markup + footer + context + partial + bootjs + modaljs + navjs + adapter + qvjs + (home_script if host == 'client' else '') + '</body></html>'
        (out / (host + '.html')).write_text(html)
    mobile = source('AgentPortal/Views/Calendar/MobileBooking.cshtml')
    mobile = exactly(mobile, '@model AgentPortal.Mobile.MobileClientBookingPage', '')
    mobile = exactly(mobile, '@{ Layout = null; }', '')
    for old, new in {'@Model.Ticket': 'synthetic-no-authority', '@Model.ProfileId': 'synthetic-profile',
                     '@Model.ClientUserId': 'synthetic-user', '@Model.TimeZone': 'UTC',
                     '@Model.DisplayName': 'Synthetic Fixture Contact', '@Model.Email': 'fixture@example.invalid', '@Model.Phone': ''}.items():
        mobile = exactly(mobile, old, new)
    mobile = exactly(mobile, '@await Html.PartialAsync("_QuickViewBookingModal")', partial)
    mobile = exactly(mobile, '<link rel="stylesheet" href="~/lib/bootstrap/dist/css/bootstrap.min.css" />', bootstrap)
    mobile = exactly(mobile, '<link rel="stylesheet" href="~/css/qv-booking.css" asp-append-version="true" />', qv)
    for path, embedded in [('~/lib/bootstrap/dist/js/bootstrap.bundle.min.js', bootjs), ('~/js/scheduling/qv-booking.js', qvjs), ('~/js/scheduling/mobile-client-booking.js', script('AgentPortal/wwwroot/js/scheduling/mobile-client-booking.js'))]:
        pattern = re.compile(r'<script src="' + re.escape(path) + r'"(?: asp-append-version="true")?></script>')
        mobile, count = pattern.subn(lambda _: embedded, mobile)
        if count != 1: raise ValueError('Mobile script anchor changed: ' + path)
    mobile = exactly(mobile, '<input id="dNextDate" />', '<input id="dNextDate" value="' + fixture_date + '" />')
    mobile = exactly(mobile, '<head>', '<head>' + head)
    mobile = exactly(mobile, '<body class="mobile-client-booking">', '<body class="mobile-client-booking">' + safety)
    if '@Model.' in mobile or '@await' in mobile or 'src="~/' in mobile:
        raise ValueError('Unresolved MobileBooking source')
    (out / 'standalone.html').write_text(mobile)
    substitutions.extend(['Standalone exact host/script; Model values and future date synthetic; canonical partial/style/script tags embedded.', 'Representative contrast selectors; exact Onboarding inline styles with Razor @@ escape resolved; not full Razor execution.', 'Remote fonts blocked; system fallback metrics. No API/authentication/tenant/live journey proof.'])
    quotes = build_quote_fixtures(root, out, raw)
    sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True, timeout=5).strip()
    if not re.fullmatch('[0-9a-f]{40}', sha): raise ValueError('Invalid checkout identity')
    manifest = {'checkout': sha, 'syntheticDate': fixture_date, 'quotes': quotes, 'inputs': inputs, 'substitutions': substitutions, 'outputs': {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in out.iterdir() if p.is_file() and p.name != 'manifest.json'}}
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    return manifest


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(build(args.root, args.out)))
