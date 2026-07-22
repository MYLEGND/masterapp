/*
 * Shared quick-view appointment rendering and management authority.
 *
 * Used by Clients CRM, Leads CRM, and the workstation lead bridge.
 * Keep appointment detail rendering and manage/cancel actions here so
 * every page stays on the same source of truth.
 */
(() => {
    "use strict";

    const ACTIVE_APPOINTMENT_STATUSES =
        new Set(["booked", "confirmed", "rescheduled"]);

    let cancelInFlight = false;

    function text(value) {
        return value == null ? "" : String(value).trim();
    }

    function parseUtc(value) {
        if (typeof window.crmParseUtcDate === "function") {
            return window.crmParseUtcDate(value);
        }

        const raw = text(value);
        if (!raw) return null;

        const parsed = new Date(raw);
        return Number.isNaN(parsed.getTime()) ? null : parsed;
    }

    function humanizeStatus(value) {
        if (typeof window.humanizeAppointmentStatus === "function") {
            return window.humanizeAppointmentStatus(value);
        }

        const raw = text(value);
        if (!raw) return "No appointment recorded";
        if (raw.toLowerCase() === "noshow") return "No Show";
        return raw.replace(/([a-z])([A-Z])/g, "$1 $2");
    }

    function formatRange(snapshot) {
        if (typeof window.formatAppointmentDateTimeRange === "function") {
            return window.formatAppointmentDateTimeRange(snapshot);
        }

        const start = parseUtc(snapshot?.scheduledStartUtc);
        if (!start) return "No appointment scheduled";
        return start.toLocaleString([], {
            dateStyle: "medium",
            timeStyle: "short"
        });
    }

    function getNode(id) {
        return document.getElementById(id);
    }

    function getQuickViewLoadedState() {
        if (typeof window.isQuickViewAppointmentLoaded === "function") {
            try {
                return !!window.isQuickViewAppointmentLoaded();
            } catch {
                return false;
            }
        }

        return false;
    }

    function getQuickViewSnapshot() {
        if (typeof window.getQuickViewAppointmentSnapshot === "function") {
            try {
                return window.getQuickViewAppointmentSnapshot();
            } catch {
                return null;
            }
        }

        return null;
    }

    function summarizeSourceLabel(source) {
        switch (text(source).toLowerCase()) {
            case "internal_manual":
                return "Internal manual";
            case "internal_calendar":
                return "Internal calendar";
            case "website_embed":
                return "Website embed";
            case "website_modal":
                return "Website modal";
            case "external_redirect_fallback":
                return "External redirect fallback";
            case "microsoft_graph_confirmation":
                return "Microsoft Graph confirmation";
            case "microsoft_graph_webhook":
                return "Microsoft Graph webhook";
            case "microsoft_graph_fallback_match":
                return "Microsoft Graph fallback match";
            case "manual_verified":
                return "Manual verified";
            default:
                return text(source) || "Not tracked yet";
        }
    }

    function summarizeAppointmentStatus(snapshot) {
        return text(snapshot?.statusLabel) ||
            humanizeStatus(snapshot?.status);
    }

    function summarizeAppointmentSource(snapshot) {
        return text(snapshot?.bookingSourceLabel) ||
            summarizeSourceLabel(snapshot?.bookingSource);
    }

    function summarizeRequestedSource(snapshot) {
        return text(snapshot?.requestedBookingSourceLabel) ||
            text(snapshot?.requestedBookingSource) ||
            text(snapshot?.bookingSourceLabel) ||
            summarizeSourceLabel(snapshot?.bookingSource);
    }

    function summarizeConfirmation(snapshot) {
        const label = text(snapshot?.confirmationStateLabel);
        if (label) return label;

        if (snapshot?.confirmationVerified) {
            return "Booked / verified";
        }

        return "No confirmation recorded";
    }

    function summarizeBookingConfig(snapshot) {
        const label = text(snapshot?.bookingConfigurationLabel);
        if (label) return label;

        const sourceLabel = text(snapshot?.bookingConfigurationSourceLabel);
        if (sourceLabel) return sourceLabel;

        if (text(snapshot?.bookingSource).toLowerCase() === "internal_calendar") {
            return "Internal calendar path";
        }

        return "No public booking config used";
    }

    function formatStatusTimestamp(snapshot) {
        const statusLabel =
            summarizeAppointmentStatus(snapshot);
        const candidates = [
            snapshot?.statusTimestampUtc,
            snapshot?.lastStatusChangedUtc,
            snapshot?.rescheduledUtc,
            snapshot?.cancelledUtc,
            snapshot?.completedUtc,
            snapshot?.confirmedUtc,
            snapshot?.bookedUtc,
            snapshot?.requestedUtc,
            snapshot?.createdUtc
        ];

        for (const candidate of candidates) {
            const parsed = parseUtc(candidate);
            if (!parsed) continue;

            return `${statusLabel} • ${parsed.toLocaleString([], {
                dateStyle: "medium",
                timeStyle: "short"
            })}`;
        }

        return "No appointment status updates yet";
    }

    function renderLink(node, url, label) {
        if (!node) return;

        const cleanUrl = text(url);
        if (!cleanUrl) {
            node.textContent = "—";
            return;
        }

        const link = document.createElement("a");
        link.className = "link";
        link.href = cleanUrl;
        link.target = "_blank";
        link.rel = "noopener";
        link.textContent = label;

        node.innerHTML = "";
        node.appendChild(link);
    }

    function hasLiveCalendarEvent(snapshot) {
        return !!text(snapshot?.id) &&
            !!text(snapshot?.calendarEventId) &&
            ACTIVE_APPOINTMENT_STATUSES.has(text(snapshot?.status).toLowerCase());
    }

    function updateManageButtons(loaded, snapshot) {
        const manageButton = getNode("btnManageAppointment");
        const cancelButton = getNode("btnCancelAppointment");
        const saveStatusButton = getNode("btnSaveAppointmentStatus");

        const canManage = loaded && hasLiveCalendarEvent(snapshot);
        const canCancel = loaded && hasLiveCalendarEvent(snapshot);

        if (manageButton) {
            manageButton.disabled = !canManage;
        }

        if (cancelButton) {
            cancelButton.disabled = !canCancel || cancelInFlight;
        }

        if (saveStatusButton && !loaded) {
            saveStatusButton.disabled = true;
        }
    }

    function renderAppointmentSnapshot(snapshot, options = {}) {
        const loaded =
            Object.prototype.hasOwnProperty.call(options, "loaded")
                ? !!options.loaded
                : getQuickViewLoadedState();
        const sync =
            typeof options.sync === "function"
                ? options.sync
                : null;

        const section = getNode("dAppointmentSection");
        const statusNode = getNode("dAppointmentStatus");
        const timeNode = getNode("dAppointmentTime");
        const sourceNode = getNode("dAppointmentSource");
        const requestedSourceNode = getNode("dAppointmentRequestedSource");
        const confirmationNode = getNode("dAppointmentConfirmation");
        const bookingConfigNode = getNode("dAppointmentBookingConfig");
        const timelineNode = getNode("dAppointmentTimeline");
        const meetingLinkNode = getNode("dAppointmentMeetingLink");
        const calendarLinkNode = getNode("dAppointmentCalendarLink");
        const statusSelect = getNode("dAppointmentStatusSelect");

        if (section) {
            section.hidden = !loaded;
        }

        if (!loaded) {
            [
                statusNode,
                timeNode,
                sourceNode,
                requestedSourceNode,
                confirmationNode,
                bookingConfigNode,
                timelineNode,
                meetingLinkNode,
                calendarLinkNode
            ].forEach(node => {
                if (node) node.textContent = "—";
            });

            if (statusSelect) {
                statusSelect.value = "Requested";
            }

            updateManageButtons(false, null);

            if (sync) sync();
            return;
        }

        if (!snapshot) {
            if (statusNode) statusNode.textContent = "No appointment recorded";
            if (timeNode) timeNode.textContent = "No appointment scheduled";
            if (sourceNode) sourceNode.textContent = "Not tracked yet";
            if (requestedSourceNode) requestedSourceNode.textContent = "Not tracked yet";
            if (confirmationNode) confirmationNode.textContent = "No confirmation recorded";
            if (bookingConfigNode) bookingConfigNode.textContent = "No public booking config used";
            if (timelineNode) timelineNode.textContent = "No appointment status updates yet";
            if (meetingLinkNode) meetingLinkNode.textContent = "—";
            if (calendarLinkNode) calendarLinkNode.textContent = "—";
            if (statusSelect) statusSelect.value = "Requested";

            updateManageButtons(true, null);

            if (sync) sync();
            return;
        }

        if (statusNode) statusNode.textContent = summarizeAppointmentStatus(snapshot);
        if (timeNode) timeNode.textContent = formatRange(snapshot);
        if (sourceNode) sourceNode.textContent = summarizeAppointmentSource(snapshot);
        if (requestedSourceNode) requestedSourceNode.textContent = summarizeRequestedSource(snapshot);
        if (confirmationNode) confirmationNode.textContent = summarizeConfirmation(snapshot);
        if (bookingConfigNode) bookingConfigNode.textContent = summarizeBookingConfig(snapshot);
        if (timelineNode) timelineNode.textContent = formatStatusTimestamp(snapshot);
        renderLink(meetingLinkNode, snapshot.meetingUrl, "Open meeting link");
        renderLink(calendarLinkNode, snapshot.calendarEventWebLink, "Open Outlook event");

        if (statusSelect) {
            statusSelect.value = text(snapshot.status) || "Requested";
        }

        updateManageButtons(true, snapshot);

        if (sync) sync();
    }

    async function cancelAppointment() {
        const adapter = window.quickViewCalendarAdapter;
        const snapshot = getQuickViewSnapshot();

        if (!getQuickViewLoadedState()) {
            adapter?.toast?.("Open a lead or client first.");
            return;
        }

        if (!snapshot?.id || !hasLiveCalendarEvent(snapshot)) {
            adapter?.toast?.("There is no live appointment to cancel.");
            return;
        }

        if (!adapter ||
            typeof adapter.getContext !== "function" ||
            typeof adapter.request !== "function" ||
            typeof adapter.applyResult !== "function") {
            window.alert("Appointment management is unavailable on this page.");
            return;
        }

        if (cancelInFlight) {
            return;
        }

        const confirmed = window.confirm(
            "Cancel this appointment? This removes it from Microsoft Bookings and the linked calendar."
        );

        if (!confirmed) {
            return;
        }

        try {
            cancelInFlight = true;
            updateManageButtons(true, snapshot);

            const context = adapter.getContext();
            const response = await adapter.request(
                "/calendar/cancel-appointment",
                {
                    appointmentId: snapshot.id,
                    clientUserId:
                        context?.clientUserId ||
                        context?.recordId ||
                        "",
                    clientProfileId:
                        context?.clientProfileId || null,
                    workstationLeadId:
                        context?.workstationLeadId ||
                        snapshot?.workstationLeadId ||
                        null
                }
            );

            const data = response?.payload || response || {};

            await adapter.applyResult(data, context);
            adapter.toast?.("Appointment cancelled");
        } catch (error) {
            console.error(error);
            adapter?.toast?.(
                error?.message || "Appointment cancellation failed."
            );
        } finally {
            cancelInFlight = false;
            renderAppointmentSnapshot(getQuickViewSnapshot(), {
                loaded: getQuickViewLoadedState()
            });
        }
    }

    function openRescheduleFlow() {
        const snapshot = getQuickViewSnapshot();
        const adapter = window.quickViewCalendarAdapter;

        if (!getQuickViewLoadedState()) {
            adapter?.toast?.("Open a lead or client first.");
            return;
        }

        if (!snapshot?.id || !hasLiveCalendarEvent(snapshot)) {
            adapter?.toast?.("There is no live appointment to reschedule.");
            return;
        }

        if (typeof window.openQuickViewBookingModal !== "function") {
            adapter?.toast?.("Appointment scheduler is unavailable.");
            return;
        }

        window.openQuickViewBookingModal({
            mode: "reschedule",
            appointment: snapshot
        });
    }

    document.addEventListener("click", event => {
        const manageButton =
            event.target?.closest?.("#btnManageAppointment");
        if (manageButton) {
            event.preventDefault();
            if (manageButton.disabled) return;
            openRescheduleFlow();
            return;
        }

        const cancelButton =
            event.target?.closest?.("#btnCancelAppointment");
        if (cancelButton) {
            event.preventDefault();
            if (cancelButton.disabled) return;
            void cancelAppointment();
        }
    });

    window.renderQuickViewAppointmentSnapshot = renderAppointmentSnapshot;
    window.LegendQuickViewAppointment = Object.freeze({
        render: renderAppointmentSnapshot,
        cancel: cancelAppointment,
        openRescheduleFlow
    });
})();
