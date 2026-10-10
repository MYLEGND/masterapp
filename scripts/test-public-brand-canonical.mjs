import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,readdirSync} from 'node:fs';
import {resolve} from 'node:path';

const root=resolve(import.meta.dirname,'..');
const read=path=>readFileSync(resolve(root,path),'utf8');
const brand=JSON.parse(read('SHARED/Branding/legend-brand.json'));
const canonical=read('SHARED/Branding/LegendBrand.cs');
const sharedProject=read('SHARED/Shared.csproj');

function walk(path) {
  return readdirSync(resolve(root,path),{withFileTypes:true}).flatMap(e=>
    e.isDirectory()?walk(path+'/'+e.name):[path+'/'+e.name]);
}

test('one immutable registry owns the registered LEGEND mark and the unregistered protection identity',()=>{
  assert.equal(brand.registeredUpper,'LEGEND®');
  assert.equal(brand.registeredTitle,'Legend®');
  assert.equal(brand.protectionName,'Legend Legacy Protection');
  assert.equal(brand.legalOwner,'MyLegnd, LLC');
  assert.equal(brand.registrationClass,'041');
  assert.ok(!brand.protectionName.includes('®'));
  assert.match(sharedProject,/EmbeddedResource Include="Branding\/legend-brand.json" LogicalName="LegendBrand.Canonical"/);
  assert.match(canonical,/GetManifestResourceStream\("LegendBrand.Canonical"\)/);
  assert.match(canonical,/Field\("protectionName"\)/);
  assert.match(canonical,/Field\("registeredUpper"\)/);
});

test('published website and .NET UIs obtain the mark from the one registry',()=>{
  for(const file of ['Legend-Website/scripts/build.mjs','Legend-Website/src/content.mjs']){
    const src=read(file);
    assert.match(src,/SHARED\/Branding\/legend-brand.json/);
    assert.ok(!src.includes('LEGEND®'),'Hard-coded mark in '+file);
  }
  for(const file of ['AgentPortal/Views/Shared/_Layout.cshtml',
                      'ClientApp/Views/Shared/_Layout.cshtml',
                      'Protect-Website/Views/Shared/_Layout.cshtml']){
    assert.match(read(file),/Shared.Branding.LegendBrand/);
  }
  const protect=read('Protect-Website/Views/Shared/_Layout.cshtml');
  assert.match(protect,/LegendBrand.ProtectionName/);
  assert.match(protect,/LegendBrand.RegisteredUpper/);
  assert.ok(!protect.includes('LEGEND® LEGACY PROTECTION'));
  const inquiry=read('SHARED/WebsitePlatform/public-inquiry-form.mjs');
  assert.ok(!inquiry.includes("'LEGEND®'"));
});

test('the four requested apps have no legacy ™ mark in customer-facing Razor markup',()=>{
  for(const base of ['AgentPortal/Views','ClientApp/Views','Protect-Website/Views']){
    for(const file of walk(base).filter(name=>name.endsWith('.cshtml'))){
      const src=read(file);
      assert.doesNotMatch(src,/LEGEND™|Legend™/,file);
      assert.doesNotMatch(src,/LEGEND®|Legend®/,file);
      assert.doesNotMatch(src,/LEGEND® Legacy Protection|Legend® Legacy Protection/,file);
    }
  }
  for(const file of [
    'AgentPortal/Services/ClientProvisioningService.cs',
    'AgentPortal/Services/ClientSubscriptionInvitationEmailService.cs',
    'AgentPortal/Controllers/OnboardingController.cs',
    'AgentPortal/Controllers/FactFinderController.cs',
    'AgentPortal/wwwroot/js/zoom-quick-popup.js'
  ])assert.doesNotMatch(read(file),/LEGEND™|Legend™/,file);
  for(const file of ['AgentPortal/wwwroot/js/legend-founder-ai.js','AgentPortal/wwwroot/js/legend-connect.js'])
    assert.doesNotMatch(read(file),/LEGEND®|Legend®|LEGEND™|Legend™/,file);
});
