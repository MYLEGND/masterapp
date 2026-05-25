from pathlib import Path
import shutil
from datetime import datetime

stamp = datetime.now().strftime("%Y%m%d-%H%M%S")

def backup(path):
    p = Path(path)
    b = p.with_suffix(p.suffix + f".bak-parity2-{stamp}")
    shutil.copy2(p, b)
    print(f"BACKUP {p} -> {b}")
    return p

# ============================================================
# HEALTH + DISABILITY
# Canonicalize submit telemetry + remove duplicate success
# ============================================================

for file_path, label in [
    ("Protect-Website/Views/Quote/Health.cshtml", "Health"),
    ("Protect-Website/Views/Quote/Disability.cshtml", "Disability")
]:
    p = backup(file_path)
    text = p.read_text()

    # --------------------------------------------------------
    # REMOVE duplicate lead_form_submit_success in thank-you
    # helper. Main success handler already emits it.
    # --------------------------------------------------------

    dup = """            track({ EventType: 'lead_form_submit_success', PageKey: pageKey, FormKey: formKey });
"""

    if dup in text:
        text = text.replace(dup, "", 1)
        print(f"REMOVED duplicate lead_form_submit_success from {label}")

    # --------------------------------------------------------
    # REPLACE submit success custom event
    # --------------------------------------------------------

    if label.lower() == "health":
        old_success = """            track({
                EventType: 'health_quote_submit_success',
                PageKey: pageKey,
                FormKey: formKey,
                MetadataJson: JSON.stringify({ leadId: asTrimmed(responseBody && responseBody.leadId) })
            });
"""

    else:
        old_success = """            track({
                EventType: 'disability_quote_submit_success',
                PageKey: pageKey,
                FormKey: formKey,
                MetadataJson: JSON.stringify({ leadId: asTrimmed(responseBody && responseBody.leadId) })
            });
"""

    new_success = """            track({
                EventType: 'form_submit',
                SubmitOutcome: 'success',
                PageKey: pageKey,
                FormKey: formKey,
                MetadataJson: JSON.stringify({
                    leadId: asTrimmed(responseBody && responseBody.leadId)
                })
            });
"""

    if old_success not in text:
        raise SystemExit(f"{label} success block not found exactly")

    text = text.replace(old_success, new_success, 1)

    # --------------------------------------------------------
    # REPLACE submit attempt custom event
    # --------------------------------------------------------

    if label.lower() == "health":
        old_attempt = """            track({
                EventType: 'health_quote_submit_attempt',
                PageKey: pageKey,
                FormKey: formKey
            });
"""

    else:
        old_attempt = """            track({
                EventType: 'disability_quote_submit_attempt',
                PageKey: pageKey,
                FormKey: formKey
            });
"""

    new_attempt = """            track({
                EventType: 'form_submit',
                SubmitOutcome: 'attempt',
                PageKey: pageKey,
                FormKey: formKey
            });
"""

    if old_attempt not in text:
        raise SystemExit(f"{label} submit-attempt block not found")

    text = text.replace(old_attempt, new_attempt, 1)

    # --------------------------------------------------------
    # REPLACE submit failure custom event
    # --------------------------------------------------------

    if label.lower() == "health":
        old_failure = """                track({
                    EventType: 'health_quote_submit_failure',
                    PageKey: pageKey,
                    FormKey: formKey,
                    MetadataJson: JSON.stringify({ correlationId: responseBody && responseBody.correlationId ? responseBody.correlationId : '' })
                });
"""

    else:
        old_failure = """                track({
                    EventType: 'disability_quote_submit_failure',
                    PageKey: pageKey,
                    FormKey: formKey,
                    MetadataJson: JSON.stringify({ correlationId: responseBody && responseBody.correlationId ? responseBody.correlationId : '' })
                });
"""

    new_failure = """                track({
                    EventType: 'form_submit',
                    SubmitOutcome: 'failure',
                    PageKey: pageKey,
                    FormKey: formKey,
                    MetadataJson: JSON.stringify({
                        correlationId: responseBody && responseBody.correlationId ? responseBody.correlationId : ''
                    })
                });
"""

    if old_failure not in text:
        raise SystemExit(f"{label} submit-failure block not found")

    text = text.replace(old_failure, new_failure, 1)

    p.write_text(text)
    print(f"PATCHED canonical submit telemetry for {label}")

# ============================================================
# DEVICE INTELLIGENCE
# Include canonical form_submit outcomes correctly
# ============================================================

p = backup("AgentPortal/Services/Analytics/AnalyticsQueryService.cs")
text = p.read_text()

old = """                    var submits = events.Count(e => e.EventType == "form_submit");
                    var leads = events.Any(e =>
                        e.EventType == "form_submit" &&
                        (e.SubmitOutcome ?? "").ToLower() == "success") ? 1 : 0;
"""

new = """                    var submits = events.Count(e =>
                        e.EventType == "form_submit" &&
                        (e.SubmitOutcome ?? "").ToLower() == "attempt");

                    var leads =
                        events.Any(e =>
                            e.EventType == "form_submit" &&
                            (e.SubmitOutcome ?? "").ToLower() == "success")
                        ||
                        events.Any(e => e.EventType == "lead_form_submit_success")
                            ? 1
                            : 0;
"""

if old not in text:
    raise SystemExit("Device Intelligence submit/lead block not found exactly")

text = text.replace(old, new, 1)

old2 = """            ConfirmedLeads = rows.Count(e => e.EventType == "form_submit" && (e.SubmitOutcome ?? "").ToLower() == "success"),
"""

new2 = """            ConfirmedLeads = rows.Count(e =>
                (e.EventType == "form_submit" && (e.SubmitOutcome ?? "").ToLower() == "success")
                || e.EventType == "lead_form_submit_success"),
"""

if old2 not in text:
    raise SystemExit("Top-level ConfirmedLeads block not found")

text = text.replace(old2, new2, 1)

p.write_text(text)

print("PATCHED Device Intelligence canonical submit handling")
print("PATCH COMPLETE")
