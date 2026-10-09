#!/usr/bin/env python3
"""Generate durable target step identities from the sole canonical release inventory.

GitHub records target-specific first-pass receipt evidence while the canonical
deployment worker may publish prepared, disjoint app targets concurrently. Durable
operation journals distinguish untouched targets from entered writes, so retries
preserve successful siblings. This file owns formatting only; targets and deployment
behavior belong to the canonical validation and deployment authorities.
"""
import argparse
import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / '.github/workflows/all-intentional-direct-release-20260918.yml'
PACKAGE_WORKFLOW = ROOT / '.github/workflows/masterapp-platform-architecture-validation.yml'
COMPONENT_START = '      # BEGIN GENERATED CANONICAL COMPONENT DOWNLOADS\n'
COMPONENT_END = '      # END GENERATED CANONICAL COMPONENT DOWNLOADS\n'
START = '      # BEGIN GENERATED CANONICAL TARGET PUBLICATIONS\n'
END = '      # END GENERATED CANONICAL TARGET PUBLICATIONS\n'
OUTCOME_START = '          # BEGIN GENERATED CANONICAL TARGET OUTCOMES\n'
OUTCOME_END = '          # END GENERATED CANONICAL TARGET OUTCOMES\n'

def authority():
    spec = importlib.util.spec_from_file_location('release_inventory', ROOT / 'scripts/validation-resume.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def target_steps(targets):
    condition = (
        "!cancelled() && steps.transactionprepare.outcome == 'success' && "
        "env.REUSE_TRANSACTION_PLAN == 'true' && steps.azuredeploy.outcome == 'success' && "
        "(steps.prepublication.outcome == 'success' || steps.prepublication.outcome == 'skipped')"
    )
    blocks = [f'''      - name: Submit canonical selected targets in parallel
        id: publish_targets
        if: ${{{{ {condition} }}}}
        continue-on-error: true
        shell: bash
        env:
          GH_TOKEN: ${{{{ github.token }}}}
          PACKAGE_PRODUCER_RUN: ${{{{ steps.reusevalidated.outputs.run_id }}}}
        run: |
          set -euo pipefail
          python3 scripts/deploy-approved-app.py \\
            --targets-json "$SELECTED_TARGETS" \\
            --package-root /tmp/diagnostics-packages \\
            --transaction-plan /tmp/release-transaction.json \\
            --publish-prepared-parallel \\
            --target-results-dir /tmp/release-target-results
''']
    for key in targets:
        blocks.append(f'''      - name: Confirm first-pass durable publication receipt ({key})
        id: publication_receipt_{key}
        if: ${{{{ {condition} && contains(fromJSON(env.SELECTED_TARGETS), '{targets[key]["releaseName"]}') }}}}
        continue-on-error: true
        shell: bash
        run: |
          set -euo pipefail
          python3 - /tmp/release-target-results/{key}.json {key} <<'PYTARGET'
          import importlib.util, json, os, pathlib, sys
          spec=importlib.util.spec_from_file_location('deployment_owner','scripts/deploy-approved-app.py')
          owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
          plan=owner.read_transaction_plan(pathlib.Path('/tmp/release-transaction.json'),os.environ['APPLICATION_RELEASE_SHA'])
          path=pathlib.Path(sys.argv[1])
          key=sys.argv[2]
          if not path.is_file():
              raise SystemExit(f'Missing parallel publication result for {{key}}')
          result=json.loads(path.read_text())
          row=next(row for row in plan['targets'] if row['app']==key)
          material=owner.transaction_target_material(plan,row,os.environ['APPLICATION_RELEASE_SHA'])
          if (
              result.get('schemaVersion') != 2
              or result.get('phase') != 'publication'
              or result.get('target') != key
              or result.get('candidateRevision') != os.environ['APPLICATION_RELEASE_SHA']
              or result.get('targetMaterial') != material
              or result.get('liveProven') is not True
              or result.get('durableReceiptProven') is not True
          ):
              raise SystemExit(
                  f'First-pass target publication is not durably proven: {{key}}; '
                  'final transaction reconciliation and live proof remain authoritative'
              )
          print(json.dumps(result,sort_keys=True))
          PYTARGET
''')
    return '\n'.join(blocks)


def render(text, targets):
    if text.count(START) != 1 or text.count(END) != 1:
        raise ValueError('Expected exactly one canonical target publication block')
    before, tail = text.split(START, 1)
    _, after = tail.split(END, 1)
    generated = before + START + target_steps(targets) + END + after
    if generated.count(OUTCOME_START) != 1 or generated.count(OUTCOME_END) != 1:
        raise ValueError('Expected exactly one bounded target diagnostics block')
    before, tail = generated.split(OUTCOME_START, 1)
    _, after = tail.split(OUTCOME_END, 1)
    return before + OUTCOME_START + OUTCOME_END + after


def component_downloads(targets):
    blocks = []
    for key in targets:
        blocks.append(f'''      - name: Load authenticated {key} component bytes
        if: needs.validated-release-package-plan.outputs.needed == 'true' && needs.validated-release-package-plan.outputs.component_protocol == 'true'
        uses: actions/download-artifact@v4
        with:
          github-token: ${{{{ github.token }}}}
          run-id: ${{{{ steps.componentsources.outputs.{key}_run_id }}}}
          artifact-ids: ${{{{ steps.componentsources.outputs.{key}_artifact_id }}}}
          path: ${{{{ runner.temp }}}}/validated-release-package
          merge-multiple: true

''')
    return ''.join(blocks)


def render_components(text, targets):
    if text.count(COMPONENT_START) != 1 or text.count(COMPONENT_END) != 1:
        raise ValueError('Expected one canonical component download block')
    before, tail = text.split(COMPONENT_START, 1)
    _, after = tail.split(COMPONENT_END, 1)
    return before + COMPONENT_START + component_downloads(targets) + COMPONENT_END + after


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    text = WORKFLOW.read_text()
    targets = authority().RELEASE_TARGETS
    generated = render(text, targets)
    package_text = PACKAGE_WORKFLOW.read_text()
    package_generated = render_components(package_text, targets)
    # The protected renderer owns writes to the release workflow only.
    # Component downloads are checked against the same inventory without
    # expanding this renderer's file-write authority.
    if package_generated != package_text:
        raise SystemExit('Component downloads drifted from canonical inventory; update the reviewed architecture block')
    if args.check:
        if generated != text:
            raise SystemExit('Publication steps drifted from the canonical release inventory; run scripts/release-workflow.py')
        print('Canonical target publication steps match inventory.')
    else:
        WORKFLOW.write_text(generated)


if __name__ == '__main__':
    main()
