# ExpenseLens Due Date Implementation Plan

## Completed Steps
- [x] Analyzed ExpenseLens.cshtml (both apps) - no changes needed
- [x] Analyzed finance-tools.js structure in both apps
- [x] Confirmed persistence via save/loadExpenseLensState()
- [x] Created detailed surgical edit plan
- [x] User approved plan (#1E3A8A premium blue, type="date", auto-save)

## Remaining Steps
1. Edit AgentPortal/wwwroot/js/finance-tools.js:
   - Update createCategoryRow() to add due date input between name & amount
   - Update saveExpenseLensState() to capture due date
   - Update loadExpenseLensState() to pass due to createCategoryRow()
2. Edit ClientApp/wwwroot/js/finance-tools.js (identical changes)
3. Test both apps:
   - Add row with name/due/amount → verify auto-save
   - Refresh page → verify due date persists
   - Delete row → verify removal
   - Load old data without due → verify backward compat
4. attempt_completion

## Notes
- Style: border 2px #1E3A8A, bg #EFF6FF, color #1E3A8A, weight 600
- ID pattern: elCatDue${index}
- Safe defaults: due='' (empty string, no breakage)
- Both apps must match exactly for consistency
