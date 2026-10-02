#!/usr/bin/env python3
"""Fail-closed validation resumption for LEGEND GitHub Actions.

The planner preserves a successful gate only when it can prove all of the following:
1. the evidence came from the same workflow/trust event and branch (or the prior
   attempt of the exact same run);
2. the prior gate step concluded successfully;
3. no file changed since that evidence which is declared to invalidate the gate;
4. no validation-control file changed; and
5. every changed file is understood by the workflow's gate map.

Anything uncertain falls back to running the gate. Production/source changes are
intentionally conservative and invalidate the whole architecture suite.
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import os
from pathlib import Path
import subprocess
import sys
import urllib.parse
import urllib.request


CONTROL_PATHS = {
    "scripts/validation-resume.py",
}

TRUSTED_PR_BASE = "legend/approved-changes"
MAX_HISTORICAL_EVIDENCE_RUNS = 8

# Single canonical web release inventory. Validation, release baseline discovery,
# deployment reconciliation, live-resume probing, package naming and final
# enforcement consume this exact definition instead of maintaining parallel maps.
RELEASE_TARGETS = {
    "portal": {
        "releaseName": "masterapp-portal",
        "host": "portal.mylegnd.com",
        "azureHost": "masterapp-portal.azurewebsites.net",
        "project": "AgentPortal/AgentPortal.csproj",
        "package": "agentportal.zip",
        "provenancePath": "/api/runtime-provenance",
        "static": False,
    },
    "client": {
        "releaseName": "masterapp-client",
        "host": "client.mylegnd.com",
        "azureHost": "masterapp-client.azurewebsites.net",
        "project": "ClientApp/ClientApp.csproj",
        "package": "clientapp.zip",
        "provenancePath": "/api/runtime-provenance",
        "static": False,
    },
    "protect": {
        "releaseName": "masterapp-protect",
        "host": "masterapp-protect.azurewebsites.net",
        "azureHost": "masterapp-protect.azurewebsites.net",
        "project": "Protect-Website/ProtectWebsite.csproj",
        "package": "protect.zip",
        "provenancePath": "/api/runtime-provenance",
        "static": False,
    },
    "parfait": {
        "releaseName": "masterapp-parfait",
        "host": "masterapp-parfait.azurewebsites.net",
        "azureHost": "masterapp-parfait.azurewebsites.net",
        "project": "ParfaitApp/ParfaitApp.csproj",
        "package": "parfait.zip",
        "provenancePath": "/api/runtime-provenance",
        "static": False,
    },
    "website": {
        "releaseName": "masterapp-website",
        "host": "masterapp-website.azurewebsites.net",
        "azureHost": "masterapp-website.azurewebsites.net",
        "project": "static",
        "package": "website.zip",
        "provenancePath": "/_deployment-provenance.txt",
        "static": True,
    },
}

WORKFLOW_PATHS = {
    name: ".github/workflows/" + name
    for name in (
        "masterapp-platform-architecture-validation.yml",
        "step5-isolated-conversion-mapping-validation.yml",
        "step6-openai-ads-execution-validation.yml",
        "steps7-8-governed-advertising-validation.yml",
        "approved-release-security-validation.yml",
    )
}

GLOBAL_DOTNET_INPUTS = (
    "MASTERAPP.sln",
    "global.json",
    "NuGet.config",
    "Directory.Build.*",
    "Directory.Packages.*",
    "**/*.csproj",
    "**/*.props",
    "**/*.targets",
)

WEB_DOTNET_SOURCE = (
    "AgentPortal/**",
    "ClientApp/**",
    "Protect-Website/**",
    "ParfaitApp/**",
    "Infrastructure/**",
    "Domain/**",
    "SHARED/**",
)

WEBSITE_SOURCE = (
    "Infrastructure/WebsiteEditing/**",
    "Infrastructure/WebsiteRuntime/**",
    "Infrastructure/Businesses/**",
    "AgentPortal/Controllers/**Website*",
    "AgentPortal/Services/**Website*",
    "AgentPortal/Views/**Website*",
    "ClientApp/Controllers/**Website*",
    "ClientApp/Views/**Website*",
    "Protect-Website/**",
    "SHARED/WebsitePlatform/**",
)

MARKETING_SOURCE = (
    "Infrastructure/Analytics/**",
    "SHARED/Analytics/**",
    "AgentPortal/Controllers/WebsiteAnalyticsController.cs",
    "AgentPortal/Views/WebsiteAnalytics/**",
    "AgentPortal/wwwroot/js/website-analytics.js",
    "AgentPortal/wwwroot/css/website-analytics.css",
)

BOOKING_SOURCE = (
    "Infrastructure/**Booking*",
    "AgentPortal/**Booking*",
    "Protect-Website/**Booking*",
    "Domain/**Booking*",
    "SHARED/**Booking*",
)

CRM_SOURCE = (
    "Infrastructure/Businesses/**",
    "AgentPortal/Controllers/LeadsController.cs",
    "AgentPortal/Controllers/ClientsController.cs",
    "AgentPortal/**CRM*",
    "ClientApp/**CRM*",
    "Domain/**Lead*",
    "Domain/**Client*",
    "SHARED/**Lead*",
    "SHARED/**Client*",
)

DIAGNOSTICS_SOURCE = (
    "AgentPortal/Controllers/FounderDiagnosticsController.cs",
    "AgentPortal/Services/FounderSoftwareRemediationService*.cs",
    "AgentPortal/Services/LegendFounderToolAuthority*.cs",
    "AgentPortal/Services/Engineering/**",
    "Domain/Engineering/**",
    "Infrastructure/Diagnostics/**",
    "SHARED/Diagnostics/**",
    "SHARED/Views/Diagnostics/**",
    "SHARED/wwwroot/js/legend-site-tools.js",
)

DIAGNOSTICS_TESTS = (
    "AgentPortal.Tests/LegendSiteToolBridgeTests.cs",
    "AgentPortal.Tests/FounderRepositoryInspectionTests.cs",
    "AgentPortal.Tests/LegendFounderToolAuthorizationTests.cs",
    "AgentPortal.Tests/FounderSoftwareRepairBatchTests.cs",
    "AgentPortal.Tests/FounderSoftwareRepairCompletionTests.cs",
    "AgentPortal.Tests/FounderRemediationRevocationTests.cs",
    "AgentPortal.Tests/LegendEngineeringControlPlaneTests.cs",
    "AgentPortal.Tests/RuntimeDiagnostics*Tests.cs",
    "AgentPortal.Tests/PageHealthContractTests.cs",
    "AgentPortal.Tests/WebDiagnosticPrivacyTests.cs",
)

WORKFLOWS = {
    "masterapp-platform-architecture-validation.yml": {
        "force_all": (),
        "neutral": (
            "Docs/**",
            "*.md",
            ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
            ".github/workflows/step6-openai-ads-execution-validation.yml",
            ".github/workflows/steps7-8-governed-advertising-validation.yml",
        ),
        "gates": {
            "lifecycle": {
                "step": "Run branch lifecycle safety contracts",
                "paths": (
                    ".github/workflows/legend-release-lifecycle.yml",
                    ".github/workflows/all-intentional-direct-release-20260918.yml",
                    ".github/workflows/approved-release-security-validation.yml",
                    "scripts/release-lifecycle.py",
                    "scripts/release_policy.py",
                    "scripts/approved-release-baseline.py",
                    "scripts/deploy-approved-app.py",
                    "scripts/release-package.py",
                    "scripts/test-validation-resume.py",
                    "scripts/test-release-lifecycle.py",
                    "scripts/test-release-policy.py",
                    "scripts/test-deploy-approved-app.py",
                ),
            },
            "mobile": {
                "step": "Run authenticated mobile authority tests",
                "paths": (
                    "tests/layout/modal-content-region.test.mjs",
                    "tests/layout/app-navigation-contract.test.mjs",
                ),
            },
            "restore-dotnet": {
                "step": "Restore .NET graph",
                "paths": GLOBAL_DOTNET_INPUTS,
            },
            "build-infrastructure": {
                "step": "Build shared infrastructure",
                "paths": ("Infrastructure/**", "Domain/**", "SHARED/**") + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore-dotnet",),
            },
            "build-hosts": {
                "step": "Build AgentPortal and ClientApp hosts",
                "paths": ("AgentPortal/**", "ClientApp/**", "Infrastructure/**", "Domain/**", "SHARED/**") + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore-dotnet",),
            },
            "build-protect": {
                "step": "Build Protect host",
                "paths": ("Protect-Website/**", "Infrastructure/**", "Domain/**", "SHARED/**") + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore-dotnet",),
            },
            "tracking-assets": {
                "step": "Verify Protect serves the exact shared tracking assets",
                "paths": (
                    "SHARED/WebsitePlatform/tracking.js",
                    "SHARED/WebsitePlatform/meta-signal-intelligence.js",
                    "SHARED/WebsitePlatform/openai-measurement.js",
                ),
                "requires": ("restore-dotnet",),
            },
            "renderer-install": {
                "step": "Install shared website renderer dependencies",
                "paths": ("Legend-Website/package.json", "Legend-Website/package-lock.json"),
            },
            "renderer-build": {
                "step": "Build shared website renderer",
                "paths": ("Legend-Website/**", "Legend-Design/**", "SHARED/WebsitePlatform/**"),
                "requires": ("renderer-install",),
            },
            "renderer-parity": {
                "step": "Verify renderer authority parity",
                "paths": ("Legend-Website/**", "Legend-Design/**", "SHARED/WebsitePlatform/**"),
                "requires": ("renderer-build",),
            },
            "renderer-tests": {
                "step": "Run business renderer tests with approved-baseline no-regression proof",
                "paths": (
                    "Legend-Website/scripts/render-business.test.mjs",
                    "Legend-Website/scripts/render-business.mjs",
                    "Legend-Website/package-lock.json",
                    "SHARED/WebsitePlatform/**",
                    "tests/website/legend-public-cms.test.mjs",
                ),
                "requires": ("renderer-install",),
            },
            "cms-install": {
                "step": "Install canonical shared CMS test dependencies",
                "paths": ("tests/website/package.json", "tests/website/package-lock.json"),
            },
            "cms-tests": {
                "step": "Run canonical shared CMS tests",
                "paths": ("tests/website/**", "SHARED/WebsitePlatform/**", "Legend-Design/**"),
                "requires": ("cms-install",),
            },
            "compile-regression": {
                "step": "Compile full regression test project",
                "paths": WEB_DOTNET_SOURCE + ("AgentPortal.Tests/**",) + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore-dotnet",),
            },
            "founder-diagnostics-regressions": {
                "step": "Run Founder diagnostics and safe GPT Codex regressions",
                "paths": DIAGNOSTICS_SOURCE + DIAGNOSTICS_TESTS,
                "requires": ("compile-regression",),
            },
            "domain-release": {
                "step": "Compile shared domain release refresh",
                "paths": ("scripts/DomainReleaseRefresh/**", "Domain/**", "Infrastructure/**") + GLOBAL_DOTNET_INPUTS,
            },
            "website-regressions": {
                "step": "Run website ownership and publishing regressions",
                "paths": (
                    "AgentPortal.Tests/WebsitePublishingAuthorityTests.cs",
                    "AgentPortal.Tests/WebsiteContentEditorRoundTripTests.cs",
                    "AgentPortal.Tests/WebsiteEditorTicketAuthorityIsolationTests.cs",
                    "AgentPortal.Tests/WebsiteDomainImportTests.cs",
                    "AgentPortal.Tests/WebsiteInquiryIsolationTests.cs",
                    "AgentPortal.Tests/BusinessWorkspaceTests.cs",
                    "AgentPortal.Tests/BusinessAnalyticsCompletionTests.cs",
                    "AgentPortal.Tests/AnalyticsCanonicalReconciliationTests.cs",
                    "AgentPortal.Tests/AnalyticsPageRoutingTruthTests.cs",
                    "AgentPortal.Tests/WebsiteSiteSourceV3Tests.cs",
                ) + WEBSITE_SOURCE + WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS,
                "exclude_paths": DIAGNOSTICS_SOURCE,
                "requires": ("compile-regression",),
            },
            "meta-regressions": {
                "step": "Run Meta authority regressions",
                "paths": (
                    "AgentPortal.Tests/Meta*Tests.cs",
                    "AgentPortal.Tests/*Analytics*Tests.cs",
                    "AgentPortal.Tests/Marketing*Tests.cs",
                    "AgentPortal.Tests/OpenAi*Tests.cs",
                    "AgentPortal.Tests/Tracking*Tests.cs",
                    "AgentPortal.Tests/QuoteProductInstrumentationContractTests.cs",
                    "AgentPortal.Tests/ProtectLeadModalInquiryTests.cs",
                ) + MARKETING_SOURCE + WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS,
                "exclude_paths": DIAGNOSTICS_SOURCE,
                "requires": ("compile-regression",),
            },
            "booking-regressions": {
                "step": "Run booking authority regressions",
                "paths": ("AgentPortal.Tests/*Booking*Tests.cs",) + BOOKING_SOURCE + WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS,
                "exclude_paths": DIAGNOSTICS_SOURCE,
                "requires": ("compile-regression",),
            },
            "crm-regressions": {
                "step": "Run CRM outcome regressions",
                "paths": (
                    "AgentPortal.Tests/LeadsControllerTests.cs",
                    "AgentPortal.Tests/ProductionControllerTests.cs",
                    "AgentPortal.Tests/WebsiteAnalyticsScopeTests.cs",
                    "AgentPortal.Tests/LaunchAuditRiskAssessmentTests.cs",
                    "AgentPortal.Tests/CanonicalCrmOutcomeLineageTests.cs",
                ) + CRM_SOURCE + WEB_DOTNET_SOURCE + GLOBAL_DOTNET_INPUTS,
                "exclude_paths": DIAGNOSTICS_SOURCE,
                "requires": ("compile-regression",),
            },
            "form-tracking": {
                "step": "Run canonical form tracking tests",
                "paths": (
                    "tests/analytics/form-tracker.test.cjs",
                    "SHARED/WebsitePlatform/tracking.js",
                    "SHARED/WebsitePlatform/meta-signal-intelligence.js",
                    "SHARED/WebsitePlatform/openai-measurement.js",
                ),
            },
            "release-web-contracts": {
                "step": "Run release web contract regressions",
                "paths": (
                    "tests/analytics/csv-export.test.cjs",
                    "tests/analytics/form-tracker.test.cjs",
                    "tests/legend-connect/**",
                    "tests/messaging/message-presentation.test.mjs",
                    "tests/layout/page-health.test.mjs",
                    "Legend-Cloudflare/tests/runtime/**",
                    "Legend-Cloudflare/tests/security/**",
                    "scripts/test-diagnostic-project-impact.py",
                    "scripts/test-sync-published-checkout.py",
                ),
            },
            "release-policy": {
                "step": "Verify consolidated release scope and routing policy",
                "paths": (
                    "scripts/release_policy.py",
                    "scripts/deploy-approved-app.py",
                    "scripts/release-package.py",
                    "scripts/approved-release-baseline.py",
                    "scripts/test-release-policy.py",
                    "scripts/test-deploy-approved-app.py",
                    "Docs/releases/direct-release-request.json",
                ),
            },
        },
    },
    "step5-isolated-conversion-mapping-validation.yml": {
        "unmatched_neutral": False,
        "force_all": (),
        "neutral": (
            "Docs/**",
            "*.md",
            ".github/workflows/masterapp-platform-architecture-validation.yml",
            ".github/workflows/step6-openai-ads-execution-validation.yml",
            ".github/workflows/steps7-8-governed-advertising-validation.yml",
            ".github/workflows/all-intentional-direct-release-20260918.yml",
            ".github/workflows/legend-release-lifecycle.yml",
            "scripts/approved-release-baseline.py",
            "scripts/release-lifecycle.py",
            "scripts/release-package.py",
            "scripts/deploy-approved-app.py",
            "scripts/test-release-lifecycle.py",
            "scripts/test-release-policy.py",
            "scripts/test-deploy-approved-app.py",
        ),
        "gates": {
            "candidate-restore": {
                "step": "Restore AgentPortal tests",
                "paths": GLOBAL_DOTNET_INPUTS,
                "group": "candidate",
                "runtime": "dotnet",
            },
            "candidate-build": {
                "step": "Build affected test graph",
                "paths": WEB_DOTNET_SOURCE + ("AgentPortal.Tests/**",) + GLOBAL_DOTNET_INPUTS,
                "requires": ("candidate-restore",),
                "group": "candidate",
                "runtime": "dotnet",
            },
            "candidate-focused": {
                "step": "Run Step 5 focused tests",
                "paths": (
                    "AgentPortal.Tests/OpenAiMeasurementDeliveryTests.cs",
                    "AgentPortal.Tests/MarketingScopeParityContractTests.cs",
                ) + MARKETING_SOURCE + GLOBAL_DOTNET_INPUTS,
                "requires": ("candidate-build",),
                "group": "candidate",
                "runtime": "dotnet",
            },
            "candidate-full": {
                "step": "Run full AgentPortal candidate suite",
                "paths": WEB_DOTNET_SOURCE + ("AgentPortal.Tests/**",) + GLOBAL_DOTNET_INPUTS,
                "requires": ("candidate-build",),
                "group": "candidate",
                "runtime": "dotnet",
                "artifact": "candidate-trx",
            },
            "comparison": {
                "step": "Prove Step 5 adds no full-suite failures",
                "paths": (
                    ".github/workflows/step5-isolated-conversion-mapping-validation.yml",
                    "scripts/validation-resume.py",
                    "scripts/test-validation-resume.py",
                    "scripts/test-release-policy.py",
                ),
                "requires": ("candidate-full",),
                "group": "comparison",
            },
        },
    },
    "step6-openai-ads-execution-validation.yml": {
        "unmatched_neutral": True,
        "force_all": (
            "SHARED/Analytics/OpenAiAdsExecutionContracts.cs",
            "Infrastructure/Analytics/OpenAiAdsExecutionService.cs",
            "Infrastructure/Analytics/MarketingConnectionStore.cs",
        ),
        "neutral": (),
        "gates": {
            "restore": {"step": "Restore affected graph", "paths": ()},
            "build": {"step": "Build affected graph", "paths": (), "requires": ("restore",)},
            "tests": {
                "step": "Run Step 6 execution and standing scope-parity tests",
                "paths": (
                    "AgentPortal.Tests/OpenAiAdsExecutionServiceTests.cs",
                    "AgentPortal.Tests/OpenAiAdsAccountConnectionAuthorityTests.cs",
                    "AgentPortal.Tests/MarketingDestinationLayerTests.cs",
                    "AgentPortal.Tests/MarketingScopeParityContractTests.cs",
                ),
                "requires": ("build",),
            },
        },
    },
    "approved-release-security-validation.yml": {
        "unmatched_neutral": True,
        "force_all": (),
        "neutral": ("Docs/**", "*.md"),
        "gates": {
            "restore": {
                "step": "Restore security validation graph",
                "paths": GLOBAL_DOTNET_INPUTS,
            },
            "build": {
                "step": "Build migration validation graph",
                "paths": (
                    "scripts/db.sh",
                    "Infrastructure/Migrations/**",
                    "Infrastructure/Data/MasterAppDbContext.cs",
                    "Infrastructure/**DbContext*.cs",
                    "Domain/Entities/**",
                ) + GLOBAL_DOTNET_INPUTS,
                "requires": ("restore",),
            },
            "db-validation": {
                "step": "Validate database migration artifacts",
                "paths": (
                    "scripts/db.sh",
                    "Infrastructure/Migrations/**",
                    "Infrastructure/Data/MasterAppDbContext.cs",
                    "Infrastructure/**DbContext*.cs",
                    "Domain/Entities/**",
                ) + GLOBAL_DOTNET_INPUTS,
                "requires": ("build",),
            },
            "no-skips": {
                "step": "Reject skipped security tests",
                "paths": (
                    "AgentPortal.Tests/*Antiforgery*.cs",
                    "AgentPortal.Tests/Phase3IdentityAuthorityTests.cs",
                    "AgentPortal.Tests/Phase4PlatformSecurityTests.cs",
                    "AgentPortal.Tests/Phase5CrossPlatformSecurityTests.cs",
                    "AgentPortal.Tests/Phase6ArchitectureInvariantTests.cs",
                    "AgentPortal.Tests/CalendarControllerTests.cs",
                ),
            },
            "vulnerabilities": {
                "step": "Audit dependency vulnerabilities",
                "paths": GLOBAL_DOTNET_INPUTS,
                "requires": ("restore",),
            },
            "secret-scan": {
                "step": "Scan committed configuration for secrets",
                "paths": ("**/appsettings*.json",),
            },
            "composition": {
                "step": "Verify shared composition authorities",
                "paths": (
                    "AgentPortal/Program.cs",
                    "ClientApp/Program.cs",
                    "Protect-Website/Program.cs",
                    "ParfaitApp/Program.cs",
                    "AgentPortal/Services/LegendFounderAiConversationService.cs",
                ),
            },
            "keyring": {
                "step": "Reject inline Azure key-ring wiring",
                "paths": (
                    "AgentPortal/Program.cs",
                    "ClientApp/Program.cs",
                    "Protect-Website/Program.cs",
                    "ParfaitApp/Program.cs",
                ),
            },
            "diff-check": {
                "step": "Verify patch whitespace integrity",
                "paths": ("**",),
            },
        },
    },
    "steps7-8-governed-advertising-validation.yml": {
        "unmatched_neutral": True,
        "force_all": (
            "Domain/Entities/AdvertisingActionAuthorization.cs",
            "SHARED/Analytics/AdvertisingActionContracts.cs",
            "Infrastructure/Analytics/AdvertisingActionAuthorizationService.cs",
            "Infrastructure/Analytics/MarketingConnectionStore.cs",
            "Infrastructure/WebsiteEditing/PromotionOrchestrationService.cs",
            "Infrastructure/WebsiteEditing/WebsitePlatformController.cs",
            "Infrastructure/Data/MasterAppDbContext.cs",
            "Infrastructure/Migrations/20260927053000_AddAdvertisingActionAuthorizations.cs",
            "Infrastructure/Analytics/AdvertisingCommandCenterService.cs",
            "AgentPortal/Controllers/WebsiteAnalyticsController.cs",
            "AgentPortal/Views/WebsiteAnalytics/Index.cshtml",
            "AgentPortal/wwwroot/js/website-analytics.js",
            "AgentPortal/wwwroot/css/website-analytics.css",
            "Infrastructure/Businesses/BusinessWorkspaceControllerBase.cs",
        ),
        "neutral": (),
        "gates": {
            "restore": {"step": "Restore affected graph", "paths": ()},
            "build": {"step": "Build affected graph", "paths": (), "requires": ("restore",)},
            "governance-tests": {
                "step": "Run Steps 7-8 governance, promotion, execution, and scope-parity tests",
                "paths": (
                    "AgentPortal.Tests/AdvertisingActionAuthorizationServiceTests.cs",
                    "AgentPortal.Tests/PromotionOrchestrationTests.cs",
                    "AgentPortal.Tests/MarketingScopeParityContractTests.cs",
                    "AgentPortal.Tests/AdvertisingCommandCenterCentralizationTests.cs",
                ),
                "requires": ("build",),
            },
            "website-ui-tests": {
                "step": "Run shared website management UI tests",
                "paths": (
                    "Legend-Design/legend-website-management.js",
                    "tests/website/**",
                ),
            },
        },
    },
}



RELEASE_STEP_POLICIES = {
    "all-intentional-direct-release-20260918.yml": {
        "Pin approved source and actual live rollback revisions": "current_state",
        "Reuse exact retained live package when available": "rollback_artifact",
        "Preserve source-equivalent rollback without production data": "rollback_artifact",
        "Reuse exact successful validation package when available": "evidence_lookup",
        "Load exact preserved deployable package": "artifact_restore",
        "Verify restored immutable validation package": "artifact_restore",
        "Verify current live base before publication": "current_state",
        "Verify ClientApp browser entry routes": "current_state",
        "Build exact selected release candidate": "artifact_reusable",
        "Verify business website routing bridge": "artifact_reusable",
        "Verify localization retention privacy limits and original delivery": "artifact_reusable",
        "Verify shared web catalog contracts": "artifact_reusable",
        "Verify selected website catalog and build": "artifact_reusable",
        "Publish exact selected application packages": "artifact_reusable",
        "Retain exact deployable candidate packages": "artifact_receipt",
        "Preserve targets already live at exact candidate": "live_identity",
        "Synchronize Protect shared website authorization and publisher runtime": "current_state",
        "Synchronize shared website editor ticket authority": "current_state",
        "Prepare shared business website routing authority": "current_state",
        "Audit centralized Cloudflare routing authority": "current_state",
        "Diagnose preserve-live routing origin acceptance": "current_state",
        "Apply additive diagnostics migrations before restarting apps": "idempotent_external",
        "Direct deploy AgentPortal": "live_identity",
        "Direct deploy ClientApp": "live_identity",
        "Refresh Azure OIDC before late deployments": "ephemeral_auth",
        "Direct deploy Protect immutable ZIP": "live_identity",
        "Direct deploy Parfait": "live_identity",
        "Reconcile public custom-hostname Cloudflare policy": "current_state",
        "Deploy shared Cloudflare business website router": "live_identity",
        "Direct deploy Website immutable ZIP": "live_identity",
        "Verify custom-domain bridge end to end": "current_state",
        "Capture exact Cloudflare challenge event after failed live proof": "diagnostic_on_failure",
        "Verify every deployed target and collect all failures": "final_live_proof",
        "Enforce complete direct deployment outcome": "finalize",
        "Retain exact approved release receipt": "artifact_receipt",
        "Capture exact release step-state receipt": "artifact_receipt",
        "Preserve exact release step-state receipt artifact": "artifact_receipt",
    },
    "legend-release-lifecycle.yml": {
        "Resolve lifecycle validation authority identity": "evidence_lookup",
        "Check lifecycle safety contracts": "artifact_reusable",
        "Retain lifecycle validation authority receipt": "artifact_receipt",
        "Preserve lifecycle validation authority receipt": "artifact_receipt",
        "Integrate ready approved change and start direct release": "idempotent_external",
        "Resume ready changes and corrections on retained branches": "idempotent_external",
        "Refresh after automatically integrated corrections": "current_state",
        "Recover authorized direct release when needed": "idempotent_external",
        "Refresh approved references before cleanup": "current_state",
        "Retire only preserved successfully deployed branches": "idempotent_external",
        "Retain exact cleanup decisions": "artifact_receipt",
    },
}


def _named_step_spans(text: str):
    lines = text.splitlines(keepends=True)
    rows = []
    for index, line in enumerate(lines):
        stripped = line.lstrip()
        if not stripped.startswith("- name:"):
            continue
        indent = len(line) - len(stripped)
        raw = stripped[len("- name:"):].strip()
        name = raw.strip("'\"")
        end = len(lines)
        for cursor in range(index + 1, len(lines)):
            candidate = lines[cursor]
            candidate_stripped = candidate.lstrip()
            candidate_indent = len(candidate) - len(candidate_stripped)
            if candidate_indent == indent and candidate_stripped.startswith("- "):
                end = cursor
                break
        rows.append((name, index, end))
    return lines, rows


def named_step_blocks(text: str):
    lines, rows = _named_step_spans(text)
    return {name: "".join(lines[start:end]) for name, start, end in rows}


def _mask_named_steps(text: str, names):
    lines, rows = _named_step_spans(text)
    wanted = set(names)
    spans = {start: (name, end) for name, start, end in rows if name in wanted}
    output = []
    cursor = 0
    while cursor < len(lines):
        row = spans.get(cursor)
        if row is None:
            output.append(lines[cursor])
            cursor += 1
            continue
        name, end = row
        indent = " " * (len(lines[cursor]) - len(lines[cursor].lstrip()))
        output.append(f"{indent}- name: __LEGEND_GATE__{name}\\n")
        cursor = end
    return "".join(output)


def workflow_gate_change_scope(prior_text: str, current_text: str, gate_steps):
    gate_steps = tuple(gate_steps)
    prior_blocks = named_step_blocks(prior_text)
    current_blocks = named_step_blocks(current_text)
    changed = {
        name
        for name in gate_steps
        if prior_blocks.get(name) != current_blocks.get(name)
    }
    structure_changed = (
        _mask_named_steps(prior_text, gate_steps)
        != _mask_named_steps(current_text, gate_steps)
    )
    return changed, structure_changed


def _job_blocks(text: str):
    """Return exact top-level job blocks without treating blank lines as EOF.

    GitHub workflow jobs routinely contain blank separators. The previous parser
    treated a bare newline as a non-indented top-level key, stopped after the
    first job, and falsely reported later jobs as missing. That silently defeated
    content-addressed Step 5 baseline reuse.
    """
    lines = text.splitlines(keepends=True)
    jobs_line = next((i for i, line in enumerate(lines) if line.strip() == "jobs:" and not line.startswith(" ")), None)
    if jobs_line is None:
        return {}
    blocks = {}
    index = jobs_line + 1
    while index < len(lines):
        line = lines[index]
        if not line.strip():
            index += 1
            continue
        if not line.startswith(" "):
            break
        if line.startswith("  ") and not line.startswith("    ") and line.strip().endswith(":"):
            name = line.strip()[:-1]
            end = index + 1
            while end < len(lines):
                candidate = lines[end]
                if not candidate.strip():
                    end += 1
                    continue
                if not candidate.startswith(" "):
                    break
                if candidate.startswith("  ") and not candidate.startswith("    ") and candidate.strip().endswith(":"):
                    break
                end += 1
            blocks[name] = "".join(lines[index:end])
            index = end
            continue
        index += 1
    return blocks


def git_show_file(revision: str, path: str) -> str:
    result = subprocess.run(
        ["git", "show", f"{revision}:{path}"],
        check=True,
        text=True,
        capture_output=True,
    )
    return result.stdout


def _dynamic_release_policy(step_name: str, block: str) -> str:
    """Classify a newly added release step conservatively without a second registry.

    Explicit policies remain useful documentation for established external-effect
    steps, but correctness never depends on remembering to extend that registry.
    New named steps are discovered from the workflow itself and default to
    fail-closed execution unless their action shape is intrinsically reusable.
    """
    if "actions/upload-artifact@" in block:
        return "artifact_receipt"
    if "actions/download-artifact@" in block:
        return "artifact_restore"
    if "actions/setup-" in block or "azure/login@" in block:
        return "ephemeral_runtime"
    return "fail_closed_execute"


def verify_release_policy_coverage(workflow_name: str, workflow_text: str):
    explicit = RELEASE_STEP_POLICIES.get(workflow_name)
    if explicit is None:
        raise ValueError(f"Unsupported release workflow coverage: {workflow_name}")
    blocks = named_step_blocks(workflow_text)
    stale = sorted(set(explicit) - set(blocks))
    if stale:
        raise ValueError(
            "Release resume policy contains stale step names; "
            f"stale={stale}"
        )
    return {
        name: explicit.get(name, _dynamic_release_policy(name, block))
        for name, block in blocks.items()
    }


def matches(path: str, patterns) -> bool:
    return any(fnmatch.fnmatchcase(path, pattern) for pattern in patterns)


def gate_matches(path: str, gate) -> bool:
    return matches(path, gate.get("paths", ())) and not matches(path, gate.get("exclude_paths", ()))


def git_changed(prior: str, current: str) -> list[str]:
    if prior == current:
        return []
    result = subprocess.run(
        ["git", "diff", "--name-only", prior, current, "--"],
        check=True,
        text=True,
        capture_output=True,
    )
    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


def api_get(repository: str, path: str, token: str):
    url = f"https://api.github.com/repos/{repository}/{path.lstrip('/')}"
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "legend-validation-resume/1.0",
        },
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def _step_map(jobs):
    return {
        step.get("name"): step.get("conclusion")
        for job in jobs
        for step in (job.get("steps") or [])
        if step.get("name")
    }


def _effective_steps(newest_to_oldest):
    """Keep the newest executed result; skipped/missing later steps do not erase proof.

    A later failure/cancellation always wins over an older success. A later run
    that never reached a gate may inherit that gate's most recent executed result
    from the exact same source SHA only.
    """
    effective = {}
    for steps in newest_to_oldest:
        for name, outcome in steps.items():
            if name in effective or outcome in {None, "", "skipped"}:
                continue
            effective[name] = outcome
    return effective


def prior_evidence(args):
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        return None, {}, "github_token_unavailable"

    if args.run_attempt > 1:
        histories = []
        newest_attempt = args.run_attempt - 1
        for attempt in range(newest_attempt, 0, -1):
            payload = api_get(
                args.repository,
                f"actions/runs/{args.current_run_id}/attempts/{attempt}/jobs?per_page=100",
                token,
            )
            histories.append(_step_map(payload.get("jobs", [])))
        return {
            "id": args.current_run_id,
            "head_sha": args.current_sha,
            "run_attempt": newest_attempt,
            "event": args.event,
            "head_branch": args.head_branch,
        }, _effective_steps(histories), "prior_attempts"

    workflow = urllib.parse.quote(args.workflow, safe="")
    branch = urllib.parse.quote(args.head_branch, safe="")
    event = urllib.parse.quote(args.event, safe="")
    payload = api_get(
        args.repository,
        f"actions/workflows/{workflow}/runs?branch={branch}&event={event}&status=completed&per_page=100",
        token,
    )
    runs = [
        run
        for run in payload.get("workflow_runs", [])
        if int(run.get("id", 0)) != args.current_run_id
        and run.get("head_branch") == args.head_branch
        and run.get("event") == args.event
        and run.get("head_sha")
    ]
    if not runs:
        return None, {}, "no_prior_completed_run"

    ordered = sorted(
        runs,
        key=lambda run: (run.get("created_at", ""), int(run.get("id", 0))),
        reverse=True,
    )
    prior = ordered[0]
    exact_head_runs = [run for run in ordered if run.get("head_sha") == prior.get("head_sha")][:10]
    histories = []
    for run in exact_head_runs:
        jobs_payload = api_get(
            args.repository,
            f"actions/runs/{run['id']}/jobs?filter=latest&per_page=100",
            token,
        )
        histories.append(_step_map(jobs_payload.get("jobs", [])))

    return prior, _effective_steps(histories), "prior_exact_head_runs"


def _plan_against_prior(workflow, current_sha, prior, prior_steps, evidence_source):
    """Build the same fail-closed gate plan against one exact historical source tree."""
    changed = git_changed(prior["head_sha"], current_sha) if prior else []
    changed_gate_steps = set()
    workflow_structure_changed = False
    workflow_path = WORKFLOW_PATHS.get(workflow)
    if prior and workflow_path and workflow_path in changed:
        prior_text = git_show_file(prior["head_sha"], workflow_path)
        current_text = Path(workflow_path).read_text()
        gate_steps = [gate["step"] for gate in WORKFLOWS[workflow]["gates"].values()]
        changed_gate_steps, workflow_structure_changed = workflow_gate_change_scope(
            prior_text, current_text, gate_steps
        )
        changed = [path for path in changed if path != workflow_path]
    return compute_plan(
        workflow,
        current_sha,
        prior,
        prior_steps,
        changed,
        evidence_source,
        changed_gate_steps,
        workflow_structure_changed,
    )


def _stamp_evidence(plan, prior, source):
    if not prior:
        return
    for gate in plan.get("gates", {}).values():
        if gate.get("run"):
            continue
        gate["evidenceRunId"] = prior.get("id")
        gate["evidenceHeadSha"] = prior.get("head_sha")
        gate["evidenceSource"] = source


def _enforce_runtime_requirements(plan):
    """Preserved proof cannot replace a prerequisite needed by a gate executing now."""
    gates = WORKFLOWS[plan["workflow"]]["gates"]
    changed = True
    while changed:
        changed = False
        for key, result in list(plan["gates"].items()):
            if not result.get("run"):
                continue
            for required in gates[key].get("requires", ()):
                required_result = plan["gates"][required]
                if required_result.get("run"):
                    continue
                required_result["run"] = True
                required_result["reason"] = f"required_by:{key}"
                required_result.pop("evidenceRunId", None)
                required_result.pop("evidenceHeadSha", None)
                required_result.pop("evidenceSource", None)
                changed = True


def merge_content_equivalent_evidence(plan, candidate_plan, run):
    """Reuse only gates whose exact declared inputs and gate definition are unchanged."""
    reused = False
    for key, result in plan["gates"].items():
        if not result.get("run"):
            continue
        candidate = candidate_plan["gates"].get(key)
        if not candidate or candidate.get("run"):
            continue
        result["run"] = False
        result["reason"] = "content_equivalent_success"
        result["evidenceRunId"] = run.get("id")
        result["evidenceHeadSha"] = run.get("head_sha")
        result["evidenceSource"] = "trusted_pr_history"
        reused = True
    return reused


def _trusted_historical_runs(args, token):
    """Return only successful same-repository PR runs targeting the protected trunk."""
    if args.event != "pull_request":
        return []
    workflow = urllib.parse.quote(args.workflow, safe="")
    payload = api_get(
        args.repository,
        f"actions/workflows/{workflow}/runs?event=pull_request&status=completed&per_page=100",
        token,
    )
    rows = []
    for run in payload.get("workflow_runs", []):
        if int(run.get("id", 0)) == args.current_run_id:
            continue
        if run.get("event") != "pull_request" or run.get("conclusion") != "success":
            continue
        if (run.get("head_repository") or {}).get("full_name") != args.repository:
            continue
        pulls = run.get("pull_requests") or []
        if not any((row.get("base") or {}).get("ref") == TRUSTED_PR_BASE for row in pulls):
            continue
        if not run.get("head_sha"):
            continue
        rows.append(run)
    rows.sort(
        key=lambda run: (run.get("updated_at") or run.get("created_at", ""), int(run.get("id", 0))),
        reverse=True,
    )
    return rows


def _apply_content_equivalent_evidence(args, plan):
    """Fill unresolved gates from recent successful runs with identical gate inputs.

    This is intentionally an optimization only. Any lookup/diff uncertainty keeps
    the existing plan unchanged, so historical reuse can never weaken validation.
    """
    if args.event != "pull_request" or not any(row.get("run") for row in plan["gates"].values()):
        return plan
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        return plan

    reused = False
    seen_heads = set()
    examined = 0
    try:
        runs = _trusted_historical_runs(args, token)
    except Exception as exc:
        plan["historicalEvidenceError"] = type(exc).__name__
        return plan

    for run in runs:
        if examined >= MAX_HISTORICAL_EVIDENCE_RUNS:
            break
        head_sha = run["head_sha"]
        if head_sha in seen_heads:
            continue
        seen_heads.add(head_sha)
        try:
            jobs_payload = api_get(
                args.repository,
                f"actions/runs/{run['id']}/jobs?filter=latest&per_page=100",
                token,
            )
            steps = _step_map(jobs_payload.get("jobs", []))
            prior = {
                "id": run["id"],
                "head_sha": head_sha,
                "run_attempt": run.get("run_attempt", 1),
                "event": run.get("event"),
                "head_branch": run.get("head_branch"),
            }
            candidate_plan = _plan_against_prior(
                args.workflow,
                args.current_sha,
                prior,
                steps,
                "trusted_pr_history",
            )
        except Exception:
            examined += 1
            continue
        examined += 1
        reused = merge_content_equivalent_evidence(plan, candidate_plan, run) or reused
        if not any(row.get("run") for row in plan["gates"].values()):
            break

    _enforce_runtime_requirements(plan)
    if reused:
        plan["mode"] = "content-addressed"
        plan["historicalEvidenceRunsExamined"] = examined
    return plan


def compute_plan(
    workflow: str,
    current_sha: str,
    prior,
    prior_steps,
    changed_paths,
    evidence_source,
    changed_gate_steps=None,
    workflow_structure_changed=False,
):
    config = WORKFLOWS[workflow]
    gates = config["gates"]
    plan = {
        "schemaVersion": 1,
        "workflow": workflow,
        "currentSha": current_sha,
        "priorRunId": prior.get("id") if prior else None,
        "priorHeadSha": prior.get("head_sha") if prior else None,
        "evidenceSource": evidence_source,
        "changedPaths": changed_paths,
        "mode": "full",
        "gates": {},
    }

    if prior is None:
        for key, gate in gates.items():
            plan["gates"][key] = {
                "step": gate["step"],
                "run": True,
                "reason": "no_prior_success_evidence",
            }
        return plan

    if any(path in CONTROL_PATHS for path in changed_paths) or workflow_structure_changed:
        reason = "validation_authority_changed" if any(path in CONTROL_PATHS for path in changed_paths) else "workflow_structure_changed"
        for key, gate in gates.items():
            plan["gates"][key] = {
                "step": gate["step"],
                "run": True,
                "reason": reason,
            }
        return plan

    changed_gate_steps = set(changed_gate_steps or ())
    force_all = any(matches(path, config.get("force_all", ())) for path in changed_paths)
    known = set()
    neutral = set()
    for path in changed_paths:
        if matches(path, config.get("neutral", ())):
            neutral.add(path)
            continue
        for key, gate in gates.items():
            if gate_matches(path, gate):
                known.add(path)
                break

    unknown = [] if config.get("unmatched_neutral") else [
        path
        for path in changed_paths
        if path not in known
        and path not in neutral
        and not matches(path, config.get("force_all", ()))
    ]
    if force_all or unknown:
        reason = "source_change_requires_full_validation" if force_all else "unclassified_change_requires_full_validation"
        for key, gate in gates.items():
            plan["gates"][key] = {
                "step": gate["step"],
                "run": True,
                "reason": reason,
            }
        plan["unclassifiedPaths"] = unknown
        return plan

    run = set()
    reasons = {}
    for key, gate in gates.items():
        step = gate["step"]
        if step in changed_gate_steps:
            run.add(key)
            reasons[key] = "gate_definition_changed"
            continue
        if prior_steps.get(step) != "success":
            run.add(key)
            reasons[key] = "prior_gate_not_successful"
            continue
        if any(gate_matches(path, gate) for path in changed_paths):
            run.add(key)
            reasons[key] = "gate_inputs_changed"

    # Close the graph in both directions. A running consumer needs its
    # prerequisites now; a changed prerequisite also invalidates every preserved
    # consumer whose evidence was produced from the older prerequisite output.
    changed = True
    while changed:
        changed = False
        for key in list(run):
            for required in gates[key].get("requires", ()):
                if required not in run:
                    run.add(required)
                    reasons[required] = f"required_by:{key}"
                    changed = True
        for key, gate in gates.items():
            if key in run:
                continue
            invalidated = next(
                (required for required in gate.get("requires", ()) if required in run),
                None,
            )
            if invalidated:
                run.add(key)
                reasons[key] = f"dependency_invalidated:{invalidated}"
                changed = True

    for key, gate in gates.items():
        should_run = key in run
        plan["gates"][key] = {
            "step": gate["step"],
            "run": should_run,
            "reason": reasons.get(key, "preserved_prior_success"),
        }
    plan["mode"] = "incremental"
    return plan


def cmd_plan(args):
    if args.workflow not in WORKFLOWS:
        raise SystemExit(f"Unsupported validation workflow: {args.workflow}")
    try:
        prior, steps, source = prior_evidence(args)
        if prior:
            plan = _plan_against_prior(args.workflow, args.current_sha, prior, steps, source)
            _stamp_evidence(plan, prior, source)
        else:
            plan = compute_plan(
                args.workflow,
                args.current_sha,
                None,
                {},
                [],
                source,
            )
        plan = _apply_content_equivalent_evidence(args, plan)
    except Exception as exc:
        # Fail closed: planner uncertainty is never permission to skip validation.
        config = WORKFLOWS[args.workflow]
        plan = {
            "schemaVersion": 1,
            "workflow": args.workflow,
            "currentSha": args.current_sha,
            "priorRunId": None,
            "priorHeadSha": None,
            "evidenceSource": "planner_fallback",
            "changedPaths": [],
            "mode": "full",
            "plannerError": type(exc).__name__,
            "gates": {
                key: {"step": gate["step"], "run": True, "reason": "planner_error_fail_closed"}
                for key, gate in config["gates"].items()
            },
        }

    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(plan, indent=2, sort_keys=True) + "\n")
    print(json.dumps(plan, indent=2, sort_keys=True))


def cmd_preserved(args):
    plan = json.loads(Path(args.plan).read_text())
    gate = plan.get("gates", {}).get(args.gate)
    if gate is None:
        print(f"Unknown gate {args.gate}; run it.", file=sys.stderr)
        raise SystemExit(1)
    if gate.get("run"):
        print(f"RUN {args.gate}: {gate.get('reason')}")
        raise SystemExit(1)
    print(
        f"PRESERVED {args.gate}: successful evidence from "
        f"run {gate.get('evidenceRunId', plan.get('priorRunId'))} at "
        f"{gate.get('evidenceHeadSha', plan.get('priorHeadSha'))} "
        f"({gate.get('evidenceSource', plan.get('evidenceSource'))})"
    )
    raise SystemExit(0)



def _step5_jobs_unchanged(prior_sha: str, workflow_path: str) -> bool:
    prior = git_show_file(prior_sha, workflow_path)
    current = Path(workflow_path).read_text()
    prior_jobs = _job_blocks(prior)
    current_jobs = _job_blocks(current)
    wanted = ("candidate", "baseline")
    return all(
        name in prior_jobs
        and name in current_jobs
        and prior_jobs[name] == current_jobs[name]
        for name in wanted
    )


def _artifact_rows(repository: str, name: str, token: str):
    encoded = urllib.parse.quote(name, safe="")
    payload = api_get(
        repository,
        f"actions/artifacts?name={encoded}&per_page=100",
        token,
    )
    return [
        row for row in payload.get("artifacts", [])
        if not row.get("expired")
    ]


def _run_artifact_names(repository: str, run_id: int, token: str):
    payload = api_get(
        repository,
        f"actions/runs/{run_id}/artifacts?per_page=100",
        token,
    )
    return {
        row.get("name")
        for row in payload.get("artifacts", [])
        if row.get("name") and not row.get("expired")
    }


def _download_run_artifact(repository: str, run_id: int, name: str, directory: Path):
    directory.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    if env.get("GITHUB_TOKEN") and not env.get("GH_TOKEN"):
        env["GH_TOKEN"] = env["GITHUB_TOKEN"]
    subprocess.run(
        [
            "gh", "run", "download", str(run_id),
            "--repo", repository,
            "--name", name,
            "--dir", str(directory),
        ],
        check=True,
        env=env,
    )


def _trx_failed(path: Path):
    import xml.etree.ElementTree as ET
    root = ET.parse(path).getroot()
    return {
        node.attrib.get("testName", "")
        for node in root.iter()
        if node.tag.endswith("UnitTestResult")
        and node.attrib.get("outcome") == "Failed"
        and node.attrib.get("testName")
    }


def _git_name_status(prior: str, current: str):
    result = subprocess.run(
        ["git", "diff", "--name-status", prior, current, "--"],
        check=True,
        text=True,
        capture_output=True,
    )
    rows = []
    for line in result.stdout.splitlines():
        parts = line.split("\t")
        if len(parts) != 2:
            return []
        rows.append((parts[0], parts[1]))
    return rows


def compute_step5_decision(
    repository: str,
    current_sha: str,
    base_sha: str,
    current_run_id: int,
    head_branch: str,
):
    """Choose only Step 5's cross-run comparison mode.

    Gate-level invalidation remains owned by compute_plan. This function only
    handles the one semantic optimization that cannot be expressed as a normal
    source gate: reuse of durable candidate/baseline TRX evidence, including
    replacing only a previously introduced failing test class.
    """
    decision = {
        "schemaVersion": 2,
        "mode": "full",
        "priorRunId": None,
        "priorHeadSha": None,
        "repairClasses": [],
        "repairFilter": None,
    }
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        decision["reason"] = "github_token_unavailable"
        return decision

    workflow_name = "step5-isolated-conversion-mapping-validation.yml"
    workflow_path = WORKFLOW_PATHS[workflow_name]
    baseline_name = f"step5-baseline-{base_sha}"
    prior_run_id = None
    prior_head_sha = None
    candidate_name = None

    for artifact in _artifact_rows(repository, baseline_name, token):
        run_id = int((artifact.get("workflow_run") or {}).get("id") or 0)
        if not run_id or run_id == current_run_id:
            continue
        run = api_get(repository, f"actions/runs/{run_id}", token)
        candidate_head = run.get("head_sha") or ""
        if not (
            len(candidate_head) == 40
            and run.get("path") == workflow_path
            and run.get("head_branch") == head_branch
            and run.get("event") == "pull_request"
            and run.get("status") == "completed"
        ):
            continue
        candidate_artifact = f"step5-candidate-{candidate_head}"
        names = _run_artifact_names(repository, run_id, token)
        if candidate_artifact in names and baseline_name in names:
            prior_run_id = run_id
            prior_head_sha = candidate_head
            candidate_name = candidate_artifact
            break

    if not prior_run_id:
        decision["reason"] = "no_reusable_candidate_baseline_pair"
        return decision

    changed = git_changed(prior_head_sha, current_sha)
    jobs_unchanged = _step5_jobs_unchanged(prior_head_sha, workflow_path)

    if changed == [workflow_path] and jobs_unchanged:
        decision.update({
            "mode": "reuse",
            "priorRunId": prior_run_id,
            "priorHeadSha": prior_head_sha,
            "reason": "comparison_only_workflow_change",
        })
        return decision

    allowed_planner_only = {workflow_path, "scripts/test-release-policy.py"}
    if (
        workflow_path in changed
        and set(changed) <= allowed_planner_only
        and jobs_unchanged
    ):
        decision.update({
            "mode": "reuse",
            "priorRunId": prior_run_id,
            "priorHeadSha": prior_head_sha,
            "reason": "comparison_only_policy_change",
        })
        return decision

    security = ".github/workflows/approved-release-security-validation.yml"
    if security in changed and set(changed) <= {workflow_path, security} and jobs_unchanged:
        classes = [
            "AgentPortal.Tests.ClientAppDeploymentWorkflowTests",
            "AgentPortal.Tests.LegendFounderAiContractTests",
        ]
        decision.update({
            "mode": "repair",
            "priorRunId": prior_run_id,
            "priorHeadSha": prior_head_sha,
            "repairClasses": classes,
            "repairFilter": "|".join(f"FullyQualifiedName~{name}" for name in classes),
            "reason": "security_contract_consumers_only",
        })
        return decision

    if not jobs_unchanged:
        decision["reason"] = "candidate_or_baseline_job_changed"
        return decision

    import tempfile
    with tempfile.TemporaryDirectory(prefix="step5-prior-") as temp:
        root = Path(temp)
        candidate_dir = root / "candidate"
        baseline_dir = root / "baseline"
        try:
            _download_run_artifact(repository, prior_run_id, candidate_name, candidate_dir)
            _download_run_artifact(repository, prior_run_id, baseline_name, baseline_dir)
        except Exception:
            decision["reason"] = "prior_artifact_download_failed"
            return decision

        candidate_path = candidate_dir / "candidate.trx"
        baseline_path = baseline_dir / "baseline.trx"
        if not candidate_path.exists() or not baseline_path.exists():
            decision["reason"] = "prior_artifact_pair_incomplete"
            return decision

        introduced = sorted(_trx_failed(candidate_path) - _trx_failed(baseline_path))
        classes = sorted({name.rsplit(".", 1)[0] for name in introduced if "." in name})
        if not introduced or not classes:
            decision["reason"] = "no_bounded_introduced_failure"
            return decision

    expected = {
        f"AgentPortal.Tests/{class_name.rsplit('.', 1)[-1]}.cs"
        for class_name in classes
        if class_name.startswith("AgentPortal.Tests.")
    }
    if len(expected) != len(classes) or not expected:
        decision["reason"] = "unbounded_failure_authority"
        return decision

    rows = _git_name_status(prior_head_sha, current_sha)
    if not rows:
        decision["reason"] = "no_exact_repair_diff"
        return decision
    allowed = expected | {workflow_path}
    changed_files = {path for _, path in rows}
    changed_tests = {path for status, path in rows if path in expected and status == "M"}
    if (
        not changed_files <= allowed
        or changed_tests != expected
        or any(status != "M" for status, path in rows if path in expected)
        or any(status not in {"M", "A"} for status, path in rows if path == workflow_path)
    ):
        decision["reason"] = "repair_diff_not_exact"
        return decision

    decision.update({
        "mode": "repair",
        "priorRunId": prior_run_id,
        "priorHeadSha": prior_head_sha,
        "repairClasses": classes,
        "repairFilter": "|".join(f"FullyQualifiedName~{name}" for name in classes),
        "reason": "replace_only_previously_failing_classes",
    })
    return decision


def cmd_step5_decision(args):
    try:
        decision = compute_step5_decision(
            args.repository,
            args.current_sha,
            args.base_sha,
            args.current_run_id,
            args.head_branch,
        )
    except Exception as exc:
        decision = {
            "schemaVersion": 2,
            "mode": "full",
            "priorRunId": None,
            "priorHeadSha": None,
            "repairClasses": [],
            "repairFilter": None,
            "reason": "planner_error_fail_closed",
            "plannerError": type(exc).__name__,
        }
    Path(args.output).write_text(json.dumps(decision, indent=2, sort_keys=True) + "\n")
    print(json.dumps(decision, indent=2, sort_keys=True))


def compute_step5_baseline_evidence(repository: str, base_sha: str):
    result = {
        "schemaVersion": 2,
        "approvedBaseSha": base_sha,
        "reusable": False,
        "evidenceRunId": None,
        "evidenceArtifact": None,
    }
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        result["reason"] = "github_token_unavailable"
        return result
    tree = subprocess.check_output(
        ["git", "rev-parse", f"{base_sha}^{{tree}}"],
        text=True,
    ).strip()
    workflow_path = WORKFLOW_PATHS["step5-isolated-conversion-mapping-validation.yml"]
    for artifact_name in (f"step5-tree-candidate-{tree}", f"step5-tree-baseline-{tree}"):
        for artifact in _artifact_rows(repository, artifact_name, token):
            run_id = int((artifact.get("workflow_run") or {}).get("id") or 0)
            if not run_id:
                continue
            run = api_get(repository, f"actions/runs/{run_id}", token)
            run_head = run.get("head_sha") or ""
            if not (
                run.get("path") == workflow_path
                and run.get("event") == "pull_request"
                and run.get("status") == "completed"
                and (run.get("head_repository") or {}).get("full_name") == repository
                and len(run_head) == 40
            ):
                continue
            if not _step5_jobs_unchanged(run_head, workflow_path):
                continue
            result.update({
                "reusable": True,
                "evidenceRunId": run_id,
                "evidenceArtifact": artifact_name,
                "tree": tree,
                "reason": "content_identical_approved_tree",
            })
            return result
    result["tree"] = tree
    result["reason"] = "no_content_identical_baseline_artifact"
    return result


def cmd_step5_baseline(args):
    try:
        result = compute_step5_baseline_evidence(args.repository, args.base_sha)
    except Exception as exc:
        result = {
            "schemaVersion": 2,
            "approvedBaseSha": args.base_sha,
            "reusable": False,
            "evidenceRunId": None,
            "evidenceArtifact": None,
            "reason": "planner_error_fail_closed",
            "plannerError": type(exc).__name__,
        }
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")
    print(json.dumps(result, indent=2, sort_keys=True))


def cmd_job_unchanged(args):
    prior = git_show_file(args.prior_sha, args.workflow_path)
    current = Path(args.workflow_path).read_text()
    prior_jobs = _job_blocks(prior)
    current_jobs = _job_blocks(current)
    missing = [name for name in args.job if name not in prior_jobs or name not in current_jobs]
    if missing:
        print("Missing workflow jobs: " + ", ".join(missing), file=sys.stderr)
        raise SystemExit(1)
    changed = [name for name in args.job if prior_jobs[name] != current_jobs[name]]
    if changed:
        print("Changed workflow jobs: " + ", ".join(changed), file=sys.stderr)
        raise SystemExit(1)
    print("Preserved workflow job definitions: " + ", ".join(args.job))


def cmd_verify_release_coverage(args):
    text = Path(args.workflow_path).read_text()
    verify_release_policy_coverage(args.workflow, text)
    print(f"Every named release step is classified: {args.workflow}")


def build_parser():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)

    plan = sub.add_parser("plan")
    plan.add_argument("--workflow", required=True)
    plan.add_argument("--current-sha", required=True)
    plan.add_argument("--current-run-id", required=True, type=int)
    plan.add_argument("--run-attempt", required=True, type=int)
    plan.add_argument("--head-branch", required=True)
    plan.add_argument("--event", required=True)
    plan.add_argument("--repository", required=True)
    plan.add_argument("--output", required=True)
    plan.set_defaults(func=cmd_plan)

    preserved = sub.add_parser("preserved")
    preserved.add_argument("--plan", required=True)
    preserved.add_argument("--gate", required=True)
    preserved.set_defaults(func=cmd_preserved)

    step5_decision = sub.add_parser("step5-decision")
    step5_decision.add_argument("--current-sha", required=True)
    step5_decision.add_argument("--base-sha", required=True)
    step5_decision.add_argument("--current-run-id", required=True, type=int)
    step5_decision.add_argument("--head-branch", required=True)
    step5_decision.add_argument("--repository", required=True)
    step5_decision.add_argument("--output", required=True)
    step5_decision.set_defaults(func=cmd_step5_decision)

    step5_baseline = sub.add_parser("step5-baseline")
    step5_baseline.add_argument("--base-sha", required=True)
    step5_baseline.add_argument("--repository", required=True)
    step5_baseline.add_argument("--output", required=True)
    step5_baseline.set_defaults(func=cmd_step5_baseline)

    job_unchanged = sub.add_parser("job-unchanged")
    job_unchanged.add_argument("--workflow-path", required=True)
    job_unchanged.add_argument("--prior-sha", required=True)
    job_unchanged.add_argument("--job", action="append", required=True)
    job_unchanged.set_defaults(func=cmd_job_unchanged)

    coverage = sub.add_parser("verify-release-coverage")
    coverage.add_argument("--workflow", required=True)
    coverage.add_argument("--workflow-path", required=True)
    coverage.set_defaults(func=cmd_verify_release_coverage)
    return parser


def main():
    args = build_parser().parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
