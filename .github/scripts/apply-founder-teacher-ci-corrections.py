from pathlib import Path

service = Path('AgentPortal/Services/LegendFounderAiConversationService.cs')
text = service.read_text()

old = 'private const int MinimumProviderConversationCharacters = 120_000;'
new = 'private const int MinimumProviderConversationCharacters = 60_000;'
assert old in text, 'provider minimum anchor changed'
text = text.replace(old, new, 1)

old = '''                    if (IsReadOnlyFounderTool(call.Name))
                    {
                        governedReadAttempts++;

                        if (IsGovernedEvidenceTool(call.Name) &&
                            IsSuccessfulFounderToolOutput(toolOutput))
                        {
                            successfulGovernedEvidenceTools.Add(call.Name);
                            governedInspectionCompleted =
                                successfulGovernedEvidenceTools.Count >=
                                requiredGovernedEvidenceReads;
                        }
                    }
'''
new = '''                    if (IsReadOnlyFounderTool(call.Name))
                    {
                        governedReadAttempts++;

                        var governedReadSucceeded =
                            IsSuccessfulFounderToolOutput(toolOutput);

                        // A broad Founder diagnostic must survive an individual
                        // read-authority failure and keep inspecting independent
                        // sources. A narrow single-authority inspection cannot
                        // truthfully continue when its only requested evidence
                        // failed, so preserve the established structured 502
                        // contract and identify the exact failed tool.
                        if (!governedReadSucceeded &&
                            !requiresComprehensiveGovernedInspection)
                        {
                            return LegendFounderAiChatResponse.ModeFailure(
                                mode,
                                FailureMessageForMode(
                                    mode,
                                    $"Governed LEGEND read '{call.Name}' failed. Independent broad inspection was not requested for this turn."),
                                "governed_tool",
                                "governed_tool",
                                "tool_read_failed");
                        }

                        if (IsGovernedEvidenceTool(call.Name) &&
                            governedReadSucceeded)
                        {
                            successfulGovernedEvidenceTools.Add(call.Name);
                            governedInspectionCompleted =
                                successfulGovernedEvidenceTools.Count >=
                                requiredGovernedEvidenceReads;
                        }
                    }
'''
assert old in text, 'governed read completion anchor changed'
text = text.replace(old, new, 1)

service.write_text(text)
Path('.github/workflows/apply-founder-teacher-ci-corrections.yml').unlink(missing_ok=True)
Path('.github/scripts/apply-founder-teacher-ci-corrections.py').unlink(missing_ok=True)
