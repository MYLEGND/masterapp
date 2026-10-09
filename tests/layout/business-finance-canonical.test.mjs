import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const financeScript=readFileSync(new URL('../../SHARED/wwwroot/js/finance-tools.js',import.meta.url),'utf8');
const financeCss=readFileSync(new URL('../../SHARED/wwwroot/css/legend-finance-shared.css',import.meta.url),'utf8');
const agentState=readFileSync(new URL('../../AgentPortal/Controllers/API/FinanceToolStatesController.cs',import.meta.url),'utf8');
const clientState=readFileSync(new URL('../../ClientApp/Controllers/Api/FinanceToolStatesController.cs',import.meta.url),'utf8');
const syncService=readFileSync(new URL('../../Infrastructure/FinancialIntelligence/ExpenseLensSynchronizationService.cs',import.meta.url),'utf8');
const migration=readFileSync(new URL('../../Infrastructure/Migrations/20261003091500_CanonicalizeBusinessFinanceToolState.cs',import.meta.url),'utf8');

test('business clients use one canonical finance engine with contextual business templates',()=>{
  for(const [name,source] of [
    ['finance-tools',financeScript],
    ['finance-css',financeCss],
    ['agent-state',agentState],
    ['client-state',clientState],
    ['expense-sync',syncService]
  ]){
    assert.doesNotMatch(source, /["']Business(?:ExpenseLens|SavingsAccelerator)["']/, name);
    assert.doesNotMatch(source, /financeDualToolPopout|createDualToolPopout|expense-lens-dual|finance-shell--dual-tools/, name);
  }

  assert.match(financeScript,/const isBusinessExpenseLens = isBusinessClient;/);
  assert.match(financeScript,/const expenseLensToolStateId = "ExpenseLens";/);
  assert.match(financeScript,/const savingsToolStateId = "SavingsAccelerator";/);
  assert.match(financeScript,/isBusinessExpenseLens \? getDefaultBusinessExpenseRows\(\) : getDefaultPersonalExpenseRows\(\)/);
  assert.match(financeScript,/isBusinessSA \? getDefaultBusinessSavingsAllocationRows\(\) : getDefaultPersonalSavingsAllocationRows\(\)/);
  assert.match(financeScript,/await renderExpenseLensInstance\(embedContainer\);/);
  assert.match(financeScript,/await renderSavingsAcceleratorInstance\(embedContainer\);/);
});

test('legacy business finance state is converged into canonical tool ids by data migration',()=>{
  assert.match(migration,/BusinessExpenseLens/);
  assert.match(migration,/BusinessSavingsAccelerator/);
  assert.match(migration,/SET \[ToolId\] = N'ExpenseLens'/);
  assert.match(migration,/SET \[ToolId\] = N'SavingsAccelerator'/);
  assert.match(migration,/ExpenseLensStreamLinks/);
});
