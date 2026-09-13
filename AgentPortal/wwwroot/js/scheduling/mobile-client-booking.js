(() => {
    "use strict";
    const context = document.getElementById("mobileBookingContext");
    const ticket = context.dataset.ticket;
    const root = "/mobile/agent/booking";
    let pending = false;
    const notice = message => { document.getElementById("mobileBookingNotice").textContent = message; };
    async function transport(path, options = {}) {
        const response = await fetch(root + path, {
            ...options, cache: "no-store", credentials: "omit",
            headers: { ...options.headers, "X-Legend-Booking-Ticket": ticket }
        });
        if (response.status === 401 || response.status === 403) {
            // The native host renews an expired ticket through the signed-in API.
            location.reload();
            throw new Error("Your booking session needs to be refreshed.");
        }
        return response;
    }
    async function request(path, payload) {
        const routes = { "/calendar/create-event": "/create", "/calendar/update-appointment": "/update", "/calendar/cancel-appointment": "/cancel" };
        if (!routes[path]) throw new Error("Unsupported booking action.");
        const response = await transport(routes[path], { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
        if (!response.ok) {
            const error = await response.text();
            void window.refreshQuickViewBookingSlots();
            throw new Error(error || "Booking could not be saved. Refresh available times and try again.");
        }
        return response.json();
    }
    function calendarDate(value) {
        if (!value) return null;
        const parts = new Intl.DateTimeFormat("en-US", {
            timeZone: context.dataset.timeZone, year: "numeric", month: "2-digit", day: "2-digit",
            hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23"
        }).formatToParts(new Date(value));
        const values = Object.fromEntries(parts.map(part => [part.type, part.value]));
        return new Date(Number(values.year), Number(values.month) - 1, Number(values.day),
            Number(values.hour), Number(values.minute), Number(values.second));
    }
    window.quickViewCalendarAdapter = {
        calendarNow: () => calendarDate(new Date()),
        appointmentDate: calendarDate,
        fetchAvailability: (path, options) => transport("/availability" + new URL(path, location.origin).search, options),
        getContext: () => {
            const nextDate = document.getElementById("qvBookDate").value;
            return {
                recordId: context.dataset.clientUserId, clientProfileId: context.dataset.profileId,
                row: context, nextDate, nextText: "Client appointment", ...window.qvBookingBuildEventTimes(nextDate),
                fullName: document.getElementById("dName").textContent,
                email: document.getElementById("dEmail").textContent,
                phone: document.getElementById("dPhone").textContent
            };
        },
        request,
        applyResult: async () => { await refreshAppointments().catch(error => notice(error.message)); },
        toast: notice
    };
    async function refreshAppointments() {
        const response = await transport("/appointments");
        if (!response.ok) throw new Error("Could not refresh this client's appointments.");
        const appointments = await response.json();
        const list = document.getElementById("mobileAppointmentList");
        list.replaceChildren();
        if (!appointments.length) { list.textContent = "No upcoming appointments."; return; }
        for (const appointment of appointments) {
            const row = document.createElement("div"); row.className = "mobile-appointment";
            const label = document.createElement("span");
            label.textContent = new Date(appointment.scheduledStartUtc).toLocaleString([], { timeZone: context.dataset.timeZone }) + " · " + appointment.status;
            row.append(label);
            const reschedule = document.createElement("button"); reschedule.type = "button"; reschedule.className = "btn qv-booking-launch"; reschedule.textContent = "Reschedule";
            reschedule.onclick = () => window.openQuickViewBookingModal({ mode: "reschedule", appointment });
            const cancel = document.createElement("button"); cancel.type = "button"; cancel.className = "btn qv-slot-btn"; cancel.textContent = "Cancel appointment";
            cancel.onclick = async () => {
                if (pending) return;
                if (cancel.dataset.confirm !== "yes") {
                    cancel.dataset.confirm = "yes";
                    cancel.textContent = "Confirm cancellation";
                    notice("Tap Confirm cancellation to cancel this appointment and release its time.");
                    return;
                }
                pending = true; cancel.disabled = true; reschedule.disabled = true;
                try {
                    await request("/calendar/cancel-appointment", { appointmentId: appointment.id });
                    notice("Appointment cancelled. Available times are refreshing.");
                    window.openQuickViewBookingModal();
                    await refreshAppointments();
                    await window.refreshQuickViewBookingSlots({ clearReservations: true });
                } catch (error) { notice(error.message); }
                finally { pending = false; cancel.disabled = false; reschedule.disabled = false; }
            };
            row.append(reschedule, cancel); list.append(row);
        }
    }
    const panel = document.getElementById("mobileClientAppointments");
    document.querySelector(".qv-booking-body").prepend(panel); panel.hidden = false;
    document.getElementById("mobileBookingTimeZone").textContent = "Calendar time zone: " + context.dataset.timeZone;
    document.getElementById("mobileNewBooking").onclick = () => window.openQuickViewBookingModal();
    window.openQuickViewBookingModal();
    const refresh = () => { if (!document.hidden && !pending) void refreshAppointments().catch(error => notice(error.message)); };
    refresh();
    window.addEventListener("focus", refresh);
    document.addEventListener("visibilitychange", refresh);
    window.setInterval(refresh, 30000);
})();
