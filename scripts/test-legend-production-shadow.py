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
name=sys.argv[sys.argv.index('--filter')+1].split('=',1)[1]
if '--list-tests' in sys.argv:
 if case=='discovery_exit':sys.exit(3)
 if case!='missing_discovery':print('    '+name)
 if case=='duplicate_discovery':print('    '+name)
 sys.exit(0)
root=pathlib.Path(os.environ['GITHUB_WORKSPACE'])/'diagnostics/legend-shadow'
(root/'private/executed.marker').write_text('SIMULATION ONLY')
now=datetime.datetime.now(datetime.timezone.utc); stamp=now.isoformat()
record={'SimulationOnly':True,'CandidateSha':os.environ['LEGEND_VALIDATION_CANDIDATE_SHA'],
'RunIdentity':os.environ['LEGEND_VALIDATION_RUN_IDENTITY'],'Authority':'non-authoritative','DeployedSha':'unavailable',
'StartedUtc':stamp,'CompletedUtc':stamp,'Status':'passed','SqlPrincipalVerified':True,
'SelectCommandCount':4,'ProviderClientCount':0,'ProviderHttpCallCount':0,'QueryTimeoutSeconds':15,'ElapsedMilliseconds':1}
if scope=='canonical_matrix':
 categories=['exact_endpoint','held_out_paraphrase','discourse','cross_family_negative','deduction','uncertainty','diagnosis','planning','audience_constraints','language_routing','native_only_isolation']
 cases=[{'Reference':'case-'+str(i),'Category':cat,'Phase':'execution','Status':'passed','ExpectedNative':i!=10,'NativeSupported':i!=10,'EvidenceCount':1 if i!=10 else 0,'ResponseAuthority':'LegendAi' if i!=10 else 'SystemDiagnostic','Stage':'native_response' if i!=10 else 'native_only_blocked','ProviderClientCount':0,'ElapsedMilliseconds':1} for i,cat in enumerate(categories)]
 record.update(MatrixVersion='lai-027-029-v1',IsolatedReadOnlyMode=True,Categories=categories,CaseResults=cases,ExecutedCases=11,TotalCases=11,FailedCases=0,NativePasses=10,NegativePasses=1,ProductionWriteCommandCount=0,ProductionSaveChangesAttempts=0,ObservationTimeoutSeconds=600)
else:
 categories=['learning','machine-learning-lifecycle','governed_cohort','held-out-competing-hypotheses','held-out-discriminating-check','native-only-provider-isolation']
 cases=[{'Category':cat,'Status':'passed','ElapsedMilliseconds':1} for cat in categories]
 for i in range(3,6):cases[i].update(ExpectedNative=i!=5,NativeSupported=i!=5,ProviderClientCount=0,ProviderHttpCallCount=0,GraphComposed=i!=5,UnknownComponentCount=0,EvidenceCount=1,NativeReason='semantic_transition_governed_composed',AnswerSha256='a'*64)
 record.update(Version='candidate-select-observation-v2',Coverage=categories,CaseResults=cases,ExecutedCases=6,BlockedCommandCount=0,SaveChangesAttempts=0,ObservationTimeoutSeconds=120)
if case=='wrong_sha':record['CandidateSha']='0'*40
if case=='wrong_identity':record['RunIdentity']='old-run'
if case=='stale_json':record['StartedUtc']=(now-datetime.timedelta(days=1)).isoformat()
if case=='future_json':record['CompletedUtc']=(now+datetime.timedelta(days=1)).isoformat()
if case=='provider_call':record['ProviderClientCount']=1
if case=='save_attempt':record['ProductionSaveChangesAttempts']=1
if case=='missing_category':record['CaseResults'].pop()
if case=='duplicate_case_reference':record['CaseResults'][-1]['Reference']=record['CaseResults'][0]['Reference']
if case=='matrix_failure':record['CaseResults'][0]['Status']='failed'
if case=='inconsistent_native_case':record['CaseResults'][0]['NativeSupported']=False
if case=='wrong_native_authority':record['CaseResults'][0]['ResponseAuthority']='SystemDiagnostic'
if case=='wrong_native_stage':record['CaseResults'][0]['Stage']='native_only_blocked'
if case=='wrong_native_aggregate':record['NativePasses'],record['NegativePasses']=1,10
(root/'observation.json').write_text(json.dumps(record))
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
el('Counters',{'total':'1','executed':'1','passed':'1','failed':'0','notExecuted':'0'},summary)
results=el('Results',parent=trx)
unit_time=trx_time if case!='stale_unit_content' else (now-datetime.timedelta(days=1)).isoformat()
attrs={'outcome':'Passed','testId':'simulated-test-id','executionId':'simulated-execution-id','testName':name,'startTime':unit_time,'endTime':unit_time}
el('UnitTestResult',attrs,results)
if case=='duplicate_trx_results':el('UnitTestResult',attrs,results)
definitions=el('TestDefinitions',parent=trx);definition=el('UnitTest',{'id':'simulated-test-id','name':name},definitions)
el('Execution',{'id':'simulated-execution-id' if case!='wrong_execution_identity' else 'old-execution-id'},definition)
class_name,method=name.rsplit('.',1)
el('TestMethod',{'className':class_name,'name':method if case!='wrong_trx_method' else 'DifferentMethod'},definition)
if case!='missing_trx':ET.ElementTree(trx).write(root/'private/observation.trx',encoding='utf-8',xml_declaration=True)
if case=='stale_trx_mtime':os.utime(root/'private/observation.trx',(0,0))
sys.exit(1 if case=='execution_exit' else 0)
''')
stub.chmod(0o755)
sha=subprocess.check_output(['git','rev-parse','HEAD'],cwd=repo,text=True).strip()
rows=[]
cases=['valid_matrix','valid_observation','missing_sql','duplicate_discovery','missing_discovery','discovery_exit','missing_trx','duplicate_trx_results','wrong_trx_method','stale_trx_mtime','stale_trx_content','wrong_sha','wrong_identity','stale_json','future_json','malformed_json','provider_call','save_attempt','missing_category','duplicate_case_reference','matrix_failure','inconsistent_native_case','execution_exit','missing_trx_times','stale_unit_content','wrong_execution_identity','wrong_native_authority','wrong_native_stage','wrong_native_aggregate','preexisting_result','preexisting_trx']
for case in cases:
 workspace=root/case;workspace.mkdir()
 if case in ('preexisting_result','preexisting_trx'):
  stale=workspace/('diagnostics/legend-shadow/observation.json' if case=='preexisting_result' else 'diagnostics/legend-shadow/private/observation.trx')
  stale.parent.mkdir(parents=True);stale.write_text('SIMULATED STALE EVIDENCE')
 env={'PATH':str(bin_dir)+':'+os.environ['PATH'],'GITHUB_WORKSPACE':str(workspace),
'LEGEND_VALIDATION_CANDIDATE_SHA':sha,'LEGEND_VALIDATION_WORKFLOW_SHA':sha,'LEGEND_VALIDATION_NONCE':uuid.uuid4().hex,
'LEGEND_PRODUCTION_READONLY_CONNECTION':'' if case=='missing_sql' else 'SIMULATED-NOT-A-SQL-CONNECTION',
'LEGEND_PRODUCTION_READONLY_FOUNDER_OID':'SIMULATED-NOT-A-FOUNDER','LEGEND_PRODUCTION_OBSERVATION_REQUIRED':'true',
'OPENAI_API_KEY':'','OpenAI__ApiKey':'','SIMULATED_CASE':case,'LEGEND_VALIDATION_SCOPE':'observation' if case=='valid_observation' else 'canonical_matrix'}
 result=subprocess.run(['bash',str(repo/'scripts/run-legend-production-shadow.sh')],cwd=repo,env=env,capture_output=True,text=True,timeout=8)
 summary_path=workspace/'diagnostics/legend-shadow/summary.json'
 summary=json.loads(summary_path.read_text()) if summary_path.exists() else {}
 expected=0 if case.startswith('valid_') else 1
 rows.append({'Case':case,'Expected':'pass' if expected==0 else 'fail','Exit':result.returncode,'Status':summary.get('Status'),'FailureCode':summary.get('FailureCode'),'MatchesExpectation':(result.returncode==0)==(expected==0),'SimulatedTestProcessInvoked':(workspace/'diagnostics/legend-shadow/private/executed.marker').exists()})
 print(json.dumps(rows[-1]))
(root/'simulation-report.json').write_text(json.dumps({'SimulationOnly':True,'SourceSha':sha,'Cases':rows},indent=2))
print('SIMULATED_REPORT='+str(root/'simulation-report.json'))
failed = [row['Case'] for row in rows if not row['MatchesExpectation']]
if failed:
    raise SystemExit('Runner contract regressions: ' + ', '.join(failed))
print(f'Runner contract checks passed: {len(rows)}. Simulation only; no SQL or builds executed.')
