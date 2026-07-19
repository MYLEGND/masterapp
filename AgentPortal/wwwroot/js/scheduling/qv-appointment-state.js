/*
 * Legend shared CRM appointment state and pipeline presentation authority.
 *
 * Used by both Leads CRM and Clients CRM.
 * Do not recreate these functions inside page-specific bundles.
 */
(() => {
    "use strict";

    const ACTIVE_MEETING_APPOINTMENT_STATUSES =
        new Set(["booked", "confirmed", "rescheduled"]);

    const rowSnapshots = new WeakMap();

    function appointmentText(value) {
        return value == null ? "" : String(value).trim();
    }

    function firstAppointmentText(...values) {
        for (const value of values) {
            const normalized = appointmentText(value);
            if (normalized) return normalized;
        }

        return "";
    }

    function parseAppointmentDate(value) {
        const raw = appointmentText(value);
        if (!raw) return null;

        const parsed = new Date(raw);
        return Number.isNaN(parsed.getTime()) ? null : parsed;
    }

    function normalizeAppointmentSnapshot(snapshot) {
        if (!snapshot || typeof snapshot !== "object") return null;

        const normalized = {
            ...snapshot,
            status: appointmentText(snapshot.status),
            statusLabel: appointmentText(snapshot.statusLabel),
            confirmationStateLabel: appointmentText(
                snapshot.confirmationStateLabel
            ),
            scheduledStartUtc: appointmentText(snapshot.scheduledStartUtc),
            scheduledEndUtc: appointmentText(snapshot.scheduledEndUtc),
            lastSyncStatus: appointmentText(snapshot.lastSyncStatus),
            calendarEventWebLink: appointmentText(
                snapshot.calendarEventWebLink
            )
        };

        const hasAppointmentState = [
            normalized.status,
            normalized.statusLabel,
            normalized.confirmationStateLabel,
            normalized.scheduledStartUtc,
            normalized.scheduledEndUtc,
            normalized.calendarEventWebLink
        ].some(Boolean);

        return hasAppointmentState ? normalized : null;
    }

    function rowLatestAppointment(row) {
        if (!row) return null;

        const stored = rowSnapshots.get(row);
        if (stored) return stored;

        const dataset = row.dataset || {};

        return normalizeAppointmentSnapshot({
            status: firstAppointmentText(
                dataset.sAppointmentStatus,
                dataset.crmAppointmentStatus
            ),
            statusLabel: firstAppointmentText(
                dataset.sAppointmentStatusLabel,
                dataset.crmAppointmentStatusLabel
            ),
            confirmationStateLabel: firstAppointmentText(
                dataset.sAppointmentConfirmationLabel,
                dataset.crmAppointmentConfirmationLabel
            ),
            scheduledStartUtc: firstAppointmentText(
                dataset.sAppointmentStart,
                dataset.crmAppointmentStart
            ),
            scheduledEndUtc: firstAppointmentText(
                dataset.sAppointmentEnd,
                dataset.crmAppointmentEnd
            ),
            lastSyncStatus: firstAppointmentText(
                dataset.sAppointmentSyncStatus,
                dataset.crmAppointmentSyncStatus
            ),
            calendarEventWebLink: firstAppointmentText(
                dataset.sAppointmentCalendarLink,
                dataset.crmAppointmentCalendarLink
            )
        });
    }

    function storeRowLatestAppointment(row, snapshot) {
        if (!row) return null;

        const normalized = normalizeAppointmentSnapshot(snapshot);

        if (normalized) {
            rowSnapshots.set(row, normalized);
        } else {
            rowSnapshots.delete(row);
        }

        const dataset = row.dataset || {};

        dataset.sAppointmentStatus = normalized?.status || "";
        dataset.sAppointmentStatusLabel = normalized?.statusLabel || "";
        dataset.sAppointmentConfirmationLabel =
            normalized?.confirmationStateLabel || "";
        dataset.sAppointmentStart = normalized?.scheduledStartUtc || "";
        dataset.sAppointmentEnd = normalized?.scheduledEndUtc || "";
        dataset.sAppointmentSyncStatus = normalized?.lastSyncStatus || "";
        dataset.sAppointmentCalendarLink =
            normalized?.calendarEventWebLink || "";

        return normalized;
    }

    function humanizeAppointmentStatus(value) {
        const raw = appointmentText(value);

        if (!raw) return "No appointment recorded";
        if (raw.toLowerCase() === "noshow") return "No Show";

        return raw.replace(/([a-z])([A-Z])/g, "$1 $2");
    }

    function formatAppointmentDateTimeRange(snapshot) {
        const start = parseAppointmentDate(snapshot?.scheduledStartUtc);
        const end = parseAppointmentDate(snapshot?.scheduledEndUtc);

        if (!start) return "No appointment scheduled";

        const zoneParts = new Intl.DateTimeFormat(undefined, {
            timeZoneName: "short"
        }).formatToParts(start);

        const zone =
            zoneParts.find(part => part.type === "timeZoneName")?.value || "";

        if (!end) {
            const label = start.toLocaleString([], {
                dateStyle: "medium",
                timeStyle: "short"
            });

            return zone ? `${label} ${zone}` : label;
        }

        const sameLocalDate =
            start.getFullYear() === end.getFullYear() &&
            start.getMonth() === end.getMonth() &&
            start.getDate() === end.getDate();

        if (sameLocalDate) {
            const label =
                `${start.toLocaleDateString([], { dateStyle: "medium" })}` +
                ` • ${start.toLocaleTimeString([], { timeStyle: "short" })}` +
                ` - ${end.toLocaleTimeString([], { timeStyle: "short" })}`;

            return zone ? `${label} ${zone}` : label;
        }

        const label =
            `${start.toLocaleString([], {
                dateStyle: "medium",
                timeStyle: "short"
            })} - ${end.toLocaleString([], {
                dateStyle: "medium",
                timeStyle: "short"
            })}`;

        return zone ? `${label} ${zone}` : label;
    }

    function summarizeLeadAppointmentStatus(snapshot) {
        return (
            appointmentText(snapshot?.statusLabel) ||
            humanizeAppointmentStatus(snapshot?.status)
        );
    }

    function summarizeLeadAppointmentTime(snapshot) {
        return snapshot
            ? formatAppointmentDateTimeRange(snapshot)
            : "No appointment scheduled";
    }

    function hasBookedAppointment(row) {
        const snapshot = rowLatestAppointment(row);
        if (!snapshot) return false;

        const status = appointmentText(snapshot.status).toLowerCase();

        return (
            ACTIVE_MEETING_APPOINTMENT_STATUSES.has(status) &&
            !!parseAppointmentDate(snapshot.scheduledStartUtc)
        );
    }

    function appointmentPipelineModel(row) {
        const appointment = rowLatestAppointment(row);

        const status = appointment
            ? summarizeLeadAppointmentStatus(appointment)
            : "Not Set";

        const statusKey = appointmentText(
            appointment?.status || "NotSet"
        )
            .replace(/[^a-z0-9_-]/gi, "")
            .toLowerCase() || "notset";

        const time = appointment
            ? summarizeLeadAppointmentTime(appointment)
            : "";

        return {
            appointment,
            status,
            statusKey,
            time
        };
    }

    function renderPipelineAppointmentFooter(
        row,
        escapeHtml,
        actionsHtml = ""
    ) {
        const escape =
            typeof escapeHtml === "function"
                ? escapeHtml
                : value =>
                    String(value ?? "")
                        .replaceAll("&", "&amp;")
                        .replaceAll("<", "&lt;")
                        .replaceAll(">", "&gt;")
                        .replaceAll('"', "&quot;")
                        .replaceAll("'", "&#039;");

        const model = appointmentPipelineModel(row);

        const timeHtml =
            model.time &&
            model.time !== "No appointment scheduled"
                ? `<div class="pipeline-card-warning">${escape(model.time)}</div>`
                : "";

        return `
          <div class="pipeline-card-footer">
            <div class="client-card-actions actions pipeline-card-actions-row">
              <div class="pipeline-card-chips">
                <span class="meta-chip pipeline-appointment-chip pipeline-appointment-chip-${escape(model.statusKey)}">Appointment: ${escape(model.status)}</span>
              </div>
              ${actionsHtml || ""}
            </div>
            ${timeHtml}
          </div>
        `;
    }

    const api = Object.freeze({
        activeStatuses: ACTIVE_MEETING_APPOINTMENT_STATUSES,
        read: rowLatestAppointment,
        store: storeRowLatestAppointment,
        hasBooked: hasBookedAppointment,
        humanizeStatus: humanizeAppointmentStatus,
        formatDateTimeRange: formatAppointmentDateTimeRange,
        summarizeStatus: summarizeLeadAppointmentStatus,
        summarizeTime: summarizeLeadAppointmentTime,
        pipelineModel: appointmentPipelineModel,
        renderPipelineFooter: renderPipelineAppointmentFooter
    });

    window.LegendAppointmentState = api;

    // Compatibility names used by the existing CRM page bundles.
    window.rowLatestAppointment = rowLatestAppointment;
    window.storeRowLatestAppointment = storeRowLatestAppointment;
    window.hasBookedAppointment = hasBookedAppointment;
    window.humanizeAppointmentStatus = humanizeAppointmentStatus;
    window.formatAppointmentDateTimeRange =
        formatAppointmentDateTimeRange;
    window.summarizeLeadAppointmentStatus =
        summarizeLeadAppointmentStatus;
    window.summarizeLeadAppointmentTime =
        summarizeLeadAppointmentTime;
    window.renderPipelineAppointmentFooter =
        renderPipelineAppointmentFooter;
})();
