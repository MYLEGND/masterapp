#!/usr/bin/env python3
"""Generate durable target step identities from the sole canonical release inventory.

GitHub records top-level step execution independently, so a retry can distinguish
an untouched target from an entered target whose upload intent is unavailable.
This file owns formatting only; targets and deployment behavior belong to the
canonical validation and deployment authorities.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / '.github/workflows/all-intentional-direct-release-20260918.yml'
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
    # Target names remain inventory-derived even though execution is one bounded
    # batch step. The deploy authority re-resolves and validates the pending list.
    release_names = [row["releaseName"] for row in targets.values()]
    if len(release_names) != len(set(release_names)):
        raise ValueError("Duplicate canonical release target")
    return '''      - name: Publish canonical pending targets
        id: publish_targets
        if: ${{ !cancelled() && steps.transactionprepare.outcome == 'success' && env.REUSE_TRANSACTION_PLAN == 'true' && steps.azuredeploy.outcome == 'success' && (steps.sharedauth.outcome == 'success' || steps.sharedauth.outcome == 'skipped') && (steps.editorauth.outcome == 'success' || steps.editorauth.outcome == 'skipped') && (steps.migrate.outcome == 'success' || steps.migrate.outcome == 'skipped') }}
        continue-on-error: true
        shell: bash
        env:
          GH_TOKEN: ${{ github.token }}
          PACKAGE_PRODUCER_RUN: ${{ steps.reusevalidated.outputs.run_id }}
          PENDING_TARGETS: ${{ steps.resumestate.outputs.pending_targets }}
        run: |
          set -euo pipefail
          test -n "$PENDING_TARGETS"
          python3 scripts/deploy-approved-app.py \
            --targets-json "$PENDING_TARGETS" \
            --package-root /tmp/diagnostics-packages \
            --publish-selected \
            --publication-outcomes /tmp/release-publication-outcomes.json \
            --transaction-plan /tmp/release-transaction.json
'''


def render(text, targets):
    if text.count(START) != 1 or text.count(END) != 1:
        raise ValueError('Expected exactly one canonical target publication block')
    before, tail = text.split(START, 1)
    _, after = tail.split(END, 1)
    generated = before + START + target_steps(targets) + END + after
    if generated.count(OUTCOME_START) != 1 or generated.count(OUTCOME_END) != 1:
        raise ValueError('Expected exactly one canonical target outcome block')
    before, tail = generated.split(OUTCOME_START, 1)
    _, after = tail.split(OUTCOME_END, 1)
    outcomes = '          TARGET_OUTCOME_BATCH: ${{ steps.publish_targets.outcome }}\\n'
    return before + OUTCOME_START + outcomes + OUTCOME_END + after


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--verify-outcomes', action='store_true')
    parser.add_argument('--selected-targets')
    args = parser.parse_args()
    if args.verify_outcomes:
        authority().selected_release_target_keys(json.loads(args.selected_targets))
        if os.environ.get('TARGET_OUTCOME_BATCH') != 'success':
            raise SystemExit('Canonical pending-target publication batch did not succeed')
        return
    text = WORKFLOW.read_text()
    generated = render(text, authority().RELEASE_TARGETS)
    if args.check:
        if generated != text:
            raise SystemExit('Publication steps drifted from the canonical release inventory; run scripts/release-workflow.py')
        print('Canonical target publication steps match inventory.')
    else:
        WORKFLOW.write_text(generated)


if __name__ == '__main__':
    main()
