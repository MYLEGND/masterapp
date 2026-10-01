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
    ".github/workflows/masterapp-platform-architecture-validation.yml",
    ".github/workflows/step6-openai-ads-execution-validation.yml",
    ".github/workflows/steps7-8-governed-advertising-validation.yml",
    "scripts/test-validation-resume.py",
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

WORKFLOWS = {
    "masterapp-platform-architecture-validation.yml": {
        "force_all": GLOBAL_DOTNET_INPUTS,
        "neutral": ("Docs/**", "*.md"),
        "gates": {
            "lifecycle": {
                "step": "Run branch lifecycle safety contracts",
                "paths": (
                    "scripts/release-lifecycle.py",
                    "scripts/release_policy.py",
                    "scripts/deploy-approved-app.py",
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
                ) + WEBSITE_SOURCE + WEB_DOTNET_SOURCE,
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
                ) + MARKETING_SOURCE + WEB_DOTNET_SOURCE,
                "requires": ("compile-regression",),
            },
            "booking-regressions": {
                "step": "Run booking authority regressions",
                "paths": ("AgentPortal.Tests/*Booking*Tests.cs",) + BOOKING_SOURCE + WEB_DOTNET_SOURCE,
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
                ) + CRM_SOURCE + WEB_DOTNET_SOURCE,
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
            "release-policy": {
                "step": "Verify consolidated release scope and routing policy",
                "paths": (
                    "scripts/release_policy.py",
                    "scripts/deploy-approved-app.py",
                    "scripts/test-release-policy.py",
                    "scripts/test-deploy-approved-app.py",
                    "Docs/releases/direct-release-request.json",
                ),
            },
        },
    },
    "step6-openai-ads-execution-validation.yml": {
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
    "steps7-8-governed-advertising-validation.yml": {
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


def matches(path: str, patterns) -> bool:
    return any(fnmatch.fnmatchcase(path, pattern) for pattern in patterns)


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


def prior_evidence(args):
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        return None, {}, "github_token_unavailable"

    if args.run_attempt > 1:
        attempt = args.run_attempt - 1
        payload = api_get(
            args.repository,
            f"actions/runs/{args.current_run_id}/attempts/{attempt}/jobs?per_page=100",
            token,
        )
        jobs = payload.get("jobs", [])
        steps = {
            step.get("name"): step.get("conclusion")
            for job in jobs
            for step in (job.get("steps") or [])
            if step.get("name")
        }
        return {
            "id": args.current_run_id,
            "head_sha": args.current_sha,
            "run_attempt": attempt,
            "event": args.event,
            "head_branch": args.head_branch,
        }, steps, "prior_attempt"

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
    prior = sorted(
        runs,
        key=lambda run: (run.get("created_at", ""), int(run.get("id", 0))),
        reverse=True,
    )[0]
    jobs_payload = api_get(
        args.repository,
        f"actions/runs/{prior['id']}/jobs?filter=latest&per_page=100",
        token,
    )
    steps = {
        step.get("name"): step.get("conclusion")
        for job in jobs_payload.get("jobs", [])
        for step in (job.get("steps") or [])
        if step.get("name")
    }
    return prior, steps, "prior_run"


def compute_plan(workflow: str, current_sha: str, prior, prior_steps, changed_paths, evidence_source):
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

    if any(path in CONTROL_PATHS for path in changed_paths):
        for key, gate in gates.items():
            plan["gates"][key] = {
                "step": gate["step"],
                "run": True,
                "reason": "validation_authority_changed",
            }
        return plan

    force_all = any(matches(path, config.get("force_all", ())) for path in changed_paths)
    known = set()
    neutral = set()
    for path in changed_paths:
        if matches(path, config.get("neutral", ())):
            neutral.add(path)
            continue
        for key, gate in gates.items():
            if matches(path, gate.get("paths", ())):
                known.add(path)
                break

    unknown = [
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
        if prior_steps.get(step) != "success":
            run.add(key)
            reasons[key] = "prior_gate_not_successful"
            continue
        if any(matches(path, gate.get("paths", ())) for path in changed_paths):
            run.add(key)
            reasons[key] = "gate_inputs_changed"

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
        changed = git_changed(prior["head_sha"], args.current_sha) if prior else []
        plan = compute_plan(args.workflow, args.current_sha, prior, steps, changed, source)
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
        f"PRESERVED {args.gate}: prior successful evidence from "
        f"run {plan.get('priorRunId')} at {plan.get('priorHeadSha')}"
    )
    raise SystemExit(0)


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
    return parser


def main():
    args = build_parser().parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
