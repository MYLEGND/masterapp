#!/usr/bin/env python3
"""Adversarial runner contract tests: simulated dotnet only; no SQL or builds.

Run with python3 scripts/test-legend-production-shadow.py on a POSIX host.
The production runner is copied unchanged into a temporary Git fixture, and a
fake dotnet executable writes controlled discovery/TRX/JSON evidence. Passing
these tests proves runner rejection behavior, never live SQL/native capability.
"""
import os
import subprocess
import pathlib
import json
import tempfile
import uuid

if os.name != "posix":
    raise SystemExit("NOT_CONFIGURED: these runner contract tests require POSIX bash and timeout")
source_repo = pathlib.Path(__file__).resolve().parents[1]
root = pathlib.Path(tempfile.mkdtemp(prefix="legend-runner-simulation-"))
repo = root / "source-fixture"
(repo / "scripts").mkdir(parents=True)
(repo / "scripts/run-legend-production-shadow.sh").write_bytes(
    (source_repo / "scripts/run-legend-production-shadow.sh").read_bytes())
subprocess.run(["git", "init", "-q", "--initial-branch=runner-contract", str(repo)], check=True)
subprocess.run(["git", "add", "scripts/run-legend-production-shadow.sh"], cwd=repo, check=True)
subprocess.run(["git", "-c", "user.name=Runner contract fixture", "-c", "user.email=runner@example.invalid",
                "-c", "commit.gpgsign=false", "-c", "core.hooksPath=/dev/null", "commit", "-qm", "Fixture"],
               cwd=repo, check=True)
bin_dir=root/'bin';bin_dir.mkdir()
stub=bin_dir/'dotnet'
stub.write_text(r'''#!/usr/bin/env python3
import sys,os,json,datetime,pathlib,xml.etree.ElementTree as ET
case=os.environ['SIMULATED_CASE']; scope=os.environ['LEGEND_VALIDATION_SCOPE']
names=[part.split('=',1)[1] for part in sys.argv[sys.argv.index('--filter')+1].split('|')]
name=names[0]
if '--list-tests' in sys.argv:
 if case=='discovery_exit':sys.exit(3)
 if case!='missing_discovery':
  for found in names[:-1] if case=='resource_missing_discovery' else names:print('    '+found)
 if case=='duplicate_discovery':print('    '+name)
 if case=='resource_extra_discovery':print('    AgentPortal.Tests.Extra.UnselectedTest')
 sys.exit(0)
root=pathlib.Path(os.environ['GITHUB_WORKSPACE'])/'diagnostics/legend-shadow'
(root/'private/executed.marker').write_text('SIMULATION ONLY')
now=datetime.datetime.now(datetime.timezone.utc); stamp=now.isoformat()
record={'SimulationOnly':True,'CandidateSha':os.environ['LEGEND_VALIDATION_CANDIDATE_SHA'],
'RunIdentity':os.environ['LEGEND_VALIDATION_RUN_IDENTITY'],'Authority':'non-authoritative','DeployedSha':'unavailable',
'StartedUtc':stamp,'CompletedUtc':stamp,'Status':'passed','SqlPrincipalVerified':True,
'SelectCommandCount':4,'ProviderClientCount':0,'ProviderHttpCallCount':0,'ExternalTranslationProviderCallAttempts':0,'QueryTimeoutSeconds':15,'ElapsedMilliseconds':1}
if scope=='canonical_matrix':
 categories=['exact_endpoint','held_out_paraphrase','discourse','cross_family_negative','deduction','uncertainty','diagnosis','planning','audience_constraints','language_routing','native_only_isolation']
 cases=[{'Reference':'case-'+str(i),'Category':cat,'Phase':'execution','Status':'passed','ExpectedNative':i!=10,'NativeSupported':i!=10,'EvidenceCount':1 if i!=10 else 0,'ResponseAuthority':'LegendAi' if i!=10 else 'SystemDiagnostic','Stage':'native_response' if i!=10 else 'native_only_blocked','ProviderClientCount':0,'ElapsedMilliseconds':1} for i,cat in enumerate(categories)]
 cases.append(dict(cases[9], Reference='automatic-language-governed-greeting'))
 record.update(MatrixVersion='lai-027-029-v1',IsolatedReadOnlyMode=True,Categories=categories,CaseResults=cases,ExecutedCases=12,TotalCases=12,FailedCases=0,NativePasses=11,NegativePasses=1,ProductionWriteCommandCount=0,ProductionSaveChangesAttempts=0,ObservationTimeoutSeconds=600)
elif scope=='observation':
 categories=['learning','machine-learning-lifecycle','governed_cohort','held-out-competing-hypotheses','held-out-discriminating-check','native-only-provider-isolation']
 cases=[{'Category':cat,'Status':'passed','ElapsedMilliseconds':1} for cat in categories]
 cases[1].update(LanguageCode='en',MinimumRows=1,Rows=1)
 for i in range(3,6):cases[i].update(ExpectedNative=i!=5,NativeSupported=i!=5,ProviderClientCount=0,ProviderHttpCallCount=0,GraphComposed=i!=5,UnknownComponentCount=0,EvidenceCount=1,NativeReason='semantic_transition_governed_composed',AnswerSha256='a'*64)
 record.update(Version='candidate-select-observation-v2',Coverage=categories,CaseResults=cases,ExecutedCases=6,BlockedCommandCount=0,SaveChangesAttempts=0,ObservationTimeoutSeconds=120)
else:
 for resource in ('azure','research','openai'):
  receipt={'CandidateSha':record['CandidateSha'],'RunIdentity':record['RunIdentity'],
   'Authority':'NonAuthoritativeResourceBoundaryDiagnostic','Environment':'LocalInMemoryObservabilityWithLiveProvider',
   'Resource':resource,'Status':'OBSERVED','StartedUtc':stamp,'CompletedUtc':stamp,'CredentialConfigured':True,'EndpointConfigured':True,
   'CanonicalWriteAttempts':0,'LocalObservabilityWrites':0,'ElapsedMilliseconds':1,'HttpCallCount':1,'HttpCallsDropped':0,
   'HttpCalls':[{'Client':resource,'StatusCode':200,'ElapsedMilliseconds':1}]}
  outcome={'Serving':'NonServing','Canonical':'NonCanonical','Provenance':'ProviderDerived'}
  if resource=='azure':
   outcome.update(Policy='ProviderEnabled',Provider='AzureTranslator',Succeeded=True,OutputPresent=True,NativeOnlyReason='external_provider_forbidden_by_native_only_policy')
   stage_pairs=[('native_only_policy','UNAVAILABLE'),('provider_translation','SUCCEEDED')]
  elif resource=='research':
   outcome.update(Policy='ProviderEnabled',Outcome='Conclusion',IsReadOnly=True,ZeroWrite=True,QueryReceipts=1,NativeOnlyReason='native_only_external_research_forbidden')
   receipt['LocalObservabilityWrites']=1
   stage_pairs=[('native_only_policy','RESEARCH_NOT_AUTHORIZED'),('research_policy','RESEARCH_REQUIRED'),('native_only_execution','Failure'),('governed_research','Conclusion')]
  else:
   outcome.update(Provider='OpenAI',Policy='ExplicitZeroWriteCatalogCanary',ResponsePresent=True,ProviderStore=False,ToolExecution='Disabled')
   receipt.update(SaveChangesAttempts=0,OperationsInvocationCount=0,ProviderClientCount=1)
   stage_pairs=[('candidate_assembly','VERIFIED'),('provider_catalog_acceptance','OBSERVED')]
  receipt['Stages']=[{'Stage':stage,'Outcome':state,'ElapsedMilliseconds':1} for stage,state in stage_pairs]
  receipt['HttpCalls'][0]['Client']={'azure':'AzureTranslator','research':'LegendInternetResearchSearch','openai':'OpenAI'}[resource]
  receipt['Outcome']=outcome
  if case=='resource_canonical_claim':outcome['Canonical']='Canonical'
  if case=='resource_openai_catalog_executed' and resource=='openai':receipt['OperationsInvocationCount']=1
  if case=='resource_research_no_receipt' and resource=='research':outcome['QueryReceipts']=0
  if case=='resource_unknown_research_state' and resource=='research':outcome['Outcome']='InventedSuccess'
  if case=='resource_no_accepted_http':receipt['HttpCalls'][0]['StatusCode']=500
  if case=='resource_missing_stages':receipt['Stages']=[]
  if resource=='azure':
   if case=='resource_wrong_sha':receipt['CandidateSha']='0'*40
   if case=='resource_wrong_identity':receipt['RunIdentity']='old-run'
   if case=='resource_wrong_authority':receipt['Authority']='production-proof'
   if case=='resource_wrong_tag':receipt['Resource']='research'
   if case=='resource_stale_json':receipt['StartedUtc']=(now-datetime.timedelta(days=1)).isoformat()
   if case=='resource_future_json':receipt['CompletedUtc']=(now+datetime.timedelta(days=1)).isoformat()
   if case=='resource_not_configured':receipt.update(Status='NOT_CONFIGURED',CredentialConfigured=False,HttpCallCount=0,HttpCalls=[])
   if case=='resource_failed_boundary':receipt['Status']='FAILED'
   if case=='resource_zero_http':receipt.update(HttpCallCount=0,HttpCalls=[])
   if case=='resource_write_attempt':receipt['CanonicalWriteAttempts']=1
   if case=='resource_false_truncation':receipt['HttpCallsDropped']=1
   if case=='resource_invalid_http_status':receipt['HttpCalls'][0]['StatusCode']=700
  if case!='resource_missing_receipt' or resource!='azure':(root/('resource-'+resource+'.json')).write_text(json.dumps(receipt))
if scope!='provider_resources':
 record.update(DiagnosticsVersion='legend-runtime-diagnostic-v1',ExercisedBoundaries={'InProcessNativeSql':True,'LiveProvider':False,'AuthenticatedHttp':False},SqlFailureCount=0,NotExecutedCases=0,DiagnosticsTruncated=False)
 for item in cases:
  item['UsesAutomaticLanguageIdentification']=item.get('Reference')=='automatic-language-governed-greeting'
  item['DiagnosticEvidence']={'SqlSnapshotReference':item.get('Reference',item['Category'])+'/sql','EvidencePrerequisites':{'ActiveExamples':2},
   'StageEvents':{'ObservedEvents':1,'RecordsDropped':0,'Truncated':False,'ExceptionEvents':0,'SqlFailureEvents':0,'Records':[{'Ordinal':1,'SqlErrorNumber':None}]},
   'SqlCommands':{'EventsObserved':1,'SucceededEvents':1,'FailedEvents':0,'CanceledEvents':0,'BlockedEvents':0,'RecordsDropped':0,'Truncated':False,'Records':[{'Ordinal':1,'QueryFingerprint':'a'*64,'Outcome':'succeeded','ElapsedMilliseconds':1,'ExceptionType':None,'HResult':None,'SqlErrorNumber':None}]}}
  item['FailureDiagnosis']={'ObservedStage':'case_assertions','AuthorityMethod':'ProductionReadOnlyNativeProofMatrix','Classification':'observed_native_response','RootCauseStatus':'no_failure_observed','NextVerification':'Verify authenticated HTTP separately.'}
 if scope=='observation':
  record['DiagnosticWindows']=[item['DiagnosticEvidence'] for item in cases]+[dict(cases[0]['DiagnosticEvidence'],SqlSnapshotReference='observation-preflight/sql')]
if case=='wrong_sha':record['CandidateSha']='0'*40
if case=='wrong_identity':record['RunIdentity']='old-run'
if case=='stale_json':record['StartedUtc']=(now-datetime.timedelta(days=1)).isoformat()
if case=='future_json':record['CompletedUtc']=(now+datetime.timedelta(days=1)).isoformat()
if case=='provider_call':record['ProviderClientCount']=1
if case=='external_translation_attempt':record['ExternalTranslationProviderCallAttempts']=1
if case=='missing_automatic_language_case':record['CaseResults'][-1]['Reference']='different-positive-case'
if case=='empty_lifecycle':record['CaseResults'][1]['Rows']=0
if case=='wrong_lifecycle_language':record['CaseResults'][1]['LanguageCode']='fr'
if case=='save_attempt':record['ProductionSaveChangesAttempts']=1
if case=='missing_category':record['CaseResults'].pop()
if case=='duplicate_case_reference':record['CaseResults'][-1]['Reference']=record['CaseResults'][0]['Reference']
if case=='matrix_failure':record['CaseResults'][0]['Status']='failed'
if case=='inconsistent_native_case':record['CaseResults'][0]['NativeSupported']=False
if case=='wrong_native_authority':record['CaseResults'][0]['ResponseAuthority']='SystemDiagnostic'
if case=='wrong_native_stage':record['CaseResults'][0]['Stage']='native_only_blocked'
if case=='wrong_native_aggregate':record['NativePasses'],record['NegativePasses']=1,10
if case=='missing_diagnostics_version':record.pop('DiagnosticsVersion')
if case=='missing_case_diagnostics':record['CaseResults'][0]['DiagnosticEvidence']=None
if case=='swallowed_sql_failure':record['CaseResults'][0]['DiagnosticEvidence']['SqlCommands'].update(FailedEvents=1,SucceededEvents=0)
if case=='swallowed_logged_sql_failure':record['CaseResults'][0]['DiagnosticEvidence']['StageEvents']['SqlFailureEvents']=1
if case=='false_truncation':record['DiagnosticsTruncated']=True
if case=='invalid_dropped_count':record['CaseResults'][0]['DiagnosticEvidence']['SqlCommands']['RecordsDropped']=1
if case=='invalid_sql_fingerprint':record['CaseResults'][0]['DiagnosticEvidence']['SqlCommands']['Records'][0]['QueryFingerprint']='raw SQL'
if case=='not_executed_case':record['NotExecutedCases']=1
if case=='automatic_language_bypassed':record['CaseResults'][-1]['UsesAutomaticLanguageIdentification']=False
if case=='wrong_exercised_boundaries':record['ExercisedBoundaries']['LiveProvider']=True
if case=='valid_truncated_diagnostics':
 record['CaseResults'][0]['DiagnosticEvidence']['SqlCommands'].update(EventsObserved=2,SucceededEvents=2,RecordsDropped=1,Truncated=True)
 record['DiagnosticsTruncated']=True
if case=='observation_missing_diagnostics':record['CaseResults'][1]['DiagnosticEvidence']=None
if case=='observation_swallowed_sql_failure':record['CaseResults'][1]['DiagnosticEvidence']['SqlCommands'].update(FailedEvents=1,SucceededEvents=0)
if case=='observation_missing_preflight':record['DiagnosticWindows']=record['DiagnosticWindows'][:-1]
if scope!='provider_resources':(root/'observation.json').write_text(json.dumps(record))
if case=='malformed_json':(root/'observation.json').write_text('{malformed')
ns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010';ET.register_namespace('',ns)
def el(name,attrs=None,parent=None):
 n=ET.Element('{'+ns+'}'+name,attrs or {})
 if parent is not None:parent.append(n)
 return n
trx=el('TestRun',{'id':'simulated-run-id','name':'SIMULATION ONLY'})
trx_time=stamp if case!='stale_trx_content' else (now-datetime.timedelta(days=1)).isoformat()
if case!='missing_trx_times':el('Times',{'creation':trx_time,'queuing':trx_time,'start':trx_time,'finish':trx_time},trx)
summary=el('ResultSummary',{'outcome':'Completed'},trx)
count=str(len(names))
el('Counters',{'total':count,'executed':count,'passed':count,'failed':'0','notExecuted':'0'},summary)
results=el('Results',parent=trx)
unit_time=trx_time if case!='stale_unit_content' else (now-datetime.timedelta(days=1)).isoformat()
definitions=el('TestDefinitions',parent=trx)
for index,name in enumerate(names):
 identity=str(index)
 attrs={'outcome':'Passed','testId':'simulated-test-'+identity,'executionId':'simulated-execution-'+identity,'testName':name,'startTime':unit_time,'endTime':unit_time}
 if case=='resource_skipped_test' and index==0:attrs['outcome']='NotExecuted'
 if case=='resource_missing_trx_test' and index==0:continue
 el('UnitTestResult',attrs,results)
 if case in ('duplicate_trx_results','resource_duplicate_trx_test'):el('UnitTestResult',attrs,results)
 definition=el('UnitTest',{'id':attrs['testId'],'name':name},definitions)
 el('Execution',{'id':attrs['executionId'] if case!='wrong_execution_identity' else 'old-execution-id'},definition)
 class_name,method=name.rsplit('.',1)
 el('TestMethod',{'className':class_name,'name':method if case not in ('wrong_trx_method','resource_wrong_test') else 'DifferentMethod'},definition)
if case!='missing_trx':ET.ElementTree(trx).write(root/'private/observation.trx',encoding='utf-8',xml_declaration=True)
if case=='stale_trx_mtime':os.utime(root/'private/observation.trx',(0,0))
sys.exit(1 if case in ('execution_exit','resource_execution_exit','resource_not_configured') else 0)
''')
stub.chmod(0o755)
sha=subprocess.check_output(['git','rev-parse','HEAD'],cwd=repo,text=True).strip()
rows=[]
cases=['valid_matrix','valid_observation','missing_sql','duplicate_discovery','missing_discovery','discovery_exit','missing_trx','duplicate_trx_results','wrong_trx_method','stale_trx_mtime','stale_trx_content','wrong_sha','wrong_identity','stale_json','future_json','malformed_json','provider_call','save_attempt','missing_category','duplicate_case_reference','matrix_failure','inconsistent_native_case','execution_exit','missing_trx_times','stale_unit_content','wrong_execution_identity','wrong_native_authority','wrong_native_stage','wrong_native_aggregate','preexisting_result','preexisting_trx']
cases += ['external_translation_attempt','missing_automatic_language_case','empty_lifecycle','wrong_lifecycle_language','missing_founder']
cases += ['missing_diagnostics_version','missing_case_diagnostics','swallowed_sql_failure','swallowed_logged_sql_failure','false_truncation','invalid_dropped_count','invalid_sql_fingerprint','not_executed_case','automatic_language_bypassed','wrong_exercised_boundaries','valid_truncated_diagnostics']
cases += ['valid_resources','resource_wrong_sha','resource_wrong_identity','resource_wrong_authority','resource_wrong_tag','resource_stale_json','resource_future_json','resource_not_configured','resource_failed_boundary','resource_zero_http','resource_write_attempt','resource_false_truncation','resource_invalid_http_status','resource_missing_receipt','resource_missing_discovery','resource_extra_discovery','resource_skipped_test','resource_missing_trx_test','resource_duplicate_trx_test','resource_wrong_test','resource_execution_exit','resource_flag_missing','resource_preexisting_receipt']
cases += ['resource_canonical_claim','resource_openai_catalog_executed','resource_research_no_receipt']
cases += ['resource_unknown_research_state','resource_no_accepted_http','resource_missing_stages','observation_missing_diagnostics','observation_swallowed_sql_failure','observation_missing_preflight']
for case in cases:
 workspace=root/case;workspace.mkdir()
 if case in ('preexisting_result','preexisting_trx'):
  stale=workspace/('diagnostics/legend-shadow/observation.json' if case=='preexisting_result' else 'diagnostics/legend-shadow/private/observation.trx')
  stale.parent.mkdir(parents=True);stale.write_text('SIMULATED STALE EVIDENCE')
 if case=='resource_preexisting_receipt':
  stale=workspace/'diagnostics/legend-shadow/resource-azure.json'
  stale.parent.mkdir(parents=True);stale.write_text('SIMULATED STALE RECEIPT')
 resource_scope=case=='valid_resources' or case.startswith('resource_')
 env={'PATH':str(bin_dir)+':'+os.environ['PATH'],'GITHUB_WORKSPACE':str(workspace),
'LEGEND_VALIDATION_CANDIDATE_SHA':sha,'LEGEND_VALIDATION_WORKFLOW_SHA':sha,'LEGEND_VALIDATION_NONCE':uuid.uuid4().hex,
'LEGEND_PRODUCTION_READONLY_CONNECTION':'' if case=='missing_sql' or resource_scope else 'SIMULATED-NOT-A-SQL-CONNECTION',
'LEGEND_PRODUCTION_READONLY_FOUNDER_OID':'' if case=='missing_founder' or resource_scope else 'SIMULATED-NOT-A-FOUNDER','LEGEND_PRODUCTION_OBSERVATION_REQUIRED':'true',
'LEGEND_RESOURCE_DIAGNOSTICS_REQUIRED':'' if case=='resource_flag_missing' else 'true',
'OPENAI_API_KEY':'SIMULATED-KEY' if resource_scope else '', 'OpenAI__ApiKey':'','SIMULATED_CASE':case,'LEGEND_VALIDATION_SCOPE':'provider_resources' if resource_scope else 'observation' if case.startswith('observation_') or case in ('valid_observation','empty_lifecycle','wrong_lifecycle_language') else 'canonical_matrix'}
 result=subprocess.run(['bash',str(repo/'scripts/run-legend-production-shadow.sh')],cwd=repo,env=env,capture_output=True,text=True,timeout=8)
 summary_path=workspace/'diagnostics/legend-shadow/summary.json'
 summary=json.loads(summary_path.read_text()) if summary_path.exists() else {}
 if case in ('missing_sql','missing_founder'):
  assert summary['FailureDiagnosis']['Classification']=='NOT_CONFIGURED'
  assert summary['ExecutedCases']==0 and summary['ProductionSqlExecutionVerified'] is False
  assert summary['FailureDiagnosis']['ProposedCodeFixVerified'] is False
  assert not (workspace/'diagnostics/legend-shadow/private/executed.marker').exists()
 if resource_scope:
  assert summary['ProductionSqlExecutionVerified'] is False and summary['ReleaseProof'] is False
  assert summary['ResourceCoverage']['ProductionSqlNative']=='NOT_EXECUTED_RESOURCE_SCOPE'
  if case=='valid_resources':assert summary['ResourceReceipts']=={'azure':'OBSERVED','research':'OBSERVED','openai':'OBSERVED'}
  if case=='resource_not_configured':
   assert summary['ResourceReceipts']['azure']=='NOT_CONFIGURED'
   assert (workspace/'diagnostics/legend-shadow/private/executed.marker').exists()
 expected=0 if case.startswith('valid_') else 1
 rows.append({'Case':case,'Expected':'pass' if expected==0 else 'fail','Exit':result.returncode,'Status':summary.get('Status'),'FailureCode':summary.get('FailureCode'),'MatchesExpectation':(result.returncode==0)==(expected==0),'SimulatedTestProcessInvoked':(workspace/'diagnostics/legend-shadow/private/executed.marker').exists()})
 print(json.dumps(rows[-1]))
(root/'simulation-report.json').write_text(json.dumps({'SimulationOnly':True,'SourceSha':sha,'Cases':rows},indent=2))
print('SIMULATED_REPORT='+str(root/'simulation-report.json'))
failed = [row['Case'] for row in rows if not row['MatchesExpectation']]
if failed:
    raise SystemExit('Runner contract regressions: ' + ', '.join(failed))
print(f'Runner contract checks passed: {len(rows)}. Simulation only; no SQL or builds executed.')
