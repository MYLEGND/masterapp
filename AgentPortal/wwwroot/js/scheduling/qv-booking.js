(() => {
    let selectedSlotTime = "";

    const $ = (id) => document.getElementById(id);
    const LegendModalApi = window.LegendModal || {};
    const ensureModalInBody = LegendModalApi.ensureInBody?.bind(LegendModalApi) || (() => null);
    const bindBootstrapModalStability = LegendModalApi.bind?.bind(LegendModalApi) || (() => null);
    const hideBootstrapModalById = LegendModalApi.hide?.bind(LegendModalApi) || (() => null);
    const closeLegacyOverlayModals = LegendModalApi.closeLegacyExecutionOverlays?.bind(LegendModalApi) || (() => {});
    const reconcileBootstrapModalState = LegendModalApi.reconcile?.bind(LegendModalApi) || (() => {});
    const BOOKING_MODAL_ID = "qvBookingModal";
    const BOOKING_MODAL_MODAL_Z = 1115;
    const BOOKING_MODAL_BACKDROP_Z = 1110;

    function bookingModalElement() {
        return ensureModalInBody(BOOKING_MODAL_ID) || $(BOOKING_MODAL_ID);
    }

    function bookingModalInstance() {
        const modalEl = bookingModalElement();
        if (!modalEl || !window.bootstrap?.Modal) return null;
        bindBootstrapModalStability(BOOKING_MODAL_ID, {
            modalZ: BOOKING_MODAL_MODAL_Z,
            backdropZ: BOOKING_MODAL_BACKDROP_Z
        });
        return window.bootstrap.Modal.getOrCreateInstance(modalEl);
    }

    function syncBookingContext() {
        const name = ($("dName")?.textContent || "").trim() || "Current contact";
        const email = ($("dEmail")?.textContent || "").trim().replace(/^[-—]\s*$/, "");
        const phone = ($("dPhone")?.textContent || "").trim().replace(/^[-—]\s*$/, "");
        const recordLabel = $("qvBookingClientLabel");
        const recordSub = $("qvBookingClientSub");

        if (recordLabel) recordLabel.textContent = name;
        if (recordSub) {
            const parts = [email, phone].filter(Boolean);
            recordSub.textContent = parts.length
                ? parts.join(" • ")
                : "Pick a date, choose a free slot, and create the appointment without leaving Quick View.";
        }

        const dateInput = $("qvBookDate");
        const durationInput = $("qvBookDuration");
        const nextDate = $("dNextDate")?.value || "";
        const meetingDuration = $("dMeetingDuration")?.value || "";

        if (dateInput && !dateInput.value && nextDate) {
            dateInput.value = nextDate;
        }
        if (durationInput && meetingDuration) {
            durationInput.value = meetingDuration;
        }
    }

    function openBookingModal() {
        closeLegacyOverlayModals();
        reconcileBootstrapModalState();
        syncBookingContext();
        const modal = bookingModalInstance();
        if (!modal) return;
        modal.show();
    }

    function formatSlotLabel(date) {
        return date.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
    }

    function parseSlotDate(value) {
        if (!value) return null;
        const d = new Date(value);
        return Number.isNaN(d.getTime()) ? null : d;
    }

    function getSlotStart(slot) {
        return parseSlotDate(slot.startIso);
    }

    function getSlotEnd(slot) {
        return parseSlotDate(slot.endIso);
    }

    function toTimeValue(date) {
        return `${String(date.getHours()).padStart(2, "0")}:${String(date.getMinutes()).padStart(2, "0")}`;
    }

    function setStatus(message, tone = "") {
        const status = $("qvBookStatus");
        if (status) {
            status.innerText = message || "";
            if (tone) status.dataset.state = tone;
            else status.removeAttribute("data-state");
        }
    }

    function clearSlots(message, tone = "") {
        const container = $("qvBookSlots");
        if (container) {
            container.innerHTML = message
                ? `<span class="qv-slot-empty">${message}</span>`
                : "";
        }
        selectedSlotTime = "";
        const timeInput = $("qvBookTime");
        if (timeInput) timeInput.value = "";
        if (message) setStatus(message, tone);
        else setStatus("");
    }

    function renderSlots(freeSlots, durationMinutes, slotIntervalMinutes) {
        const container = $("qvBookSlots");
        if (!container) return;

        container.innerHTML = "";
        selectedSlotTime = "";
        const timeInput = $("qvBookTime");
        if (timeInput) timeInput.value = "";

        const generated = [];

        for (const slot of freeSlots || []) {
            const start = getSlotStart(slot);
            const end = getSlotEnd(slot);
            if (!start || !end || end <= start) continue;

            let cursor = new Date(start);
            while (cursor.getTime() + durationMinutes * 60000 <= end.getTime()) {
                generated.push(new Date(cursor));
                cursor = new Date(cursor.getTime() + (slotIntervalMinutes || 30) * 60000);
            }
        }

        if (!generated.length) {
            clearSlots("No open slots", "warning");
            return;
        }

        setStatus("");

        for (const start of generated) {
            const btn = document.createElement("button");
            btn.type = "button";
            btn.className = "btn btn-ghost qv-slot-btn";
            btn.textContent = formatSlotLabel(start);
            btn.dataset.time = toTimeValue(start);

            btn.addEventListener("click", () => {
                selectedSlotTime = btn.dataset.time;
                if (timeInput) timeInput.value = selectedSlotTime;

                container.querySelectorAll(".qv-slot-btn").forEach(x => x.classList.remove("selected"));
                btn.classList.add("selected");
                setStatus(`Selected ${btn.textContent}`, "selected");
            });

            container.appendChild(btn);
        }
    }

    async function loadSlots() {
        const date = $("qvBookDate")?.value || "";
        const duration = parseInt($("qvBookDuration")?.value || "30", 10) || 30;

        if (!date) {
            clearSlots("Select a date to see open slots.", "warning");
            return;
        }

        clearSlots("Loading slots…", "loading");

        try {
            const res = await fetch(`/calendar/day-availability?date=${encodeURIComponent(date)}`, {
                credentials: "include"
            });

            if (!res.ok) {
                const text = await res.text().catch(() => "");
                throw new Error(text || "Availability failed");
            }

            const data = await res.json();
            const freeSlots = Array.isArray(data.freeSlots) ? data.freeSlots : [];
            const slotInterval = parseInt(data.slotIntervalMinutes || "30", 10) || 30;
            renderSlots(freeSlots, duration, slotInterval);
        } catch (err) {
            console.error(err);
            clearSlots("Could not load slots", "error");
        }
    }


    document.addEventListener("focusin", (e) => {
        if (!e.target) return;
        if (e.target.id === "qvBookDate" && e.target.value) {
            loadSlots();
        }
    });

    document.addEventListener("click", (e) => {
        if (!e.target) return;
        if (e.target.id === "qvBookDate" && e.target.value) {
            loadSlots();
        }
    });

    document.addEventListener("change", (e) => {
        if (!e.target) return;
        if (e.target.id === "qvBookDate" || e.target.id === "qvBookDuration") {
            loadSlots();
        }
    });

    document.addEventListener("click", (e) => {
        const trigger = e.target?.closest?.("[data-qv-booking-open]");
        if (!trigger) return;
        e.preventDefault();
        openBookingModal();
    });

    document.addEventListener("shown.bs.modal", (e) => {
        if (e.target?.id !== BOOKING_MODAL_ID) return;
        syncBookingContext();
        const dateInput = $("qvBookDate");
        if (dateInput?.value) {
            loadSlots();
        } else {
            clearSlots("Select a date to see open slots.", "warning");
        }
        dateInput?.focus?.({ preventScroll: true });
    });

    document.addEventListener("hidden.bs.modal", (e) => {
        if (e.target?.id !== BOOKING_MODAL_ID) return;
        selectedSlotTime = "";
        const timeInput = $("qvBookTime");
        if (timeInput) timeInput.value = "";
        setStatus("");
    });

    document.addEventListener("click", async (e) => {
        if (!e.target || e.target.id !== "btnBookAppointment") return;

        const date = $("qvBookDate")?.value || "";
        const time = $("qvBookTime")?.value || selectedSlotTime;
        const duration = $("qvBookDuration")?.value || "30";

        if (!date) return setStatus("Select date");
        if (!time) return setStatus("Select a slot");

        const nextDate = $("dNextDate");
        const nextText = $("dNextText");
        const meetingTime = $("dMeetingTime");
        const meetingDuration = $("dMeetingDuration");

        if (nextDate) nextDate.value = date;
        if (meetingTime) meetingTime.value = time;
        if (meetingDuration) meetingDuration.value = duration;
        if (nextText && !nextText.value.trim()) {
            nextText.value = "Appointment booked from Quick View";
        }

        if (typeof createCalendarEventFromDrawer !== "function") {
            console.error("createCalendarEventFromDrawer is unavailable on this page.");
            return setStatus("Calendar flow unavailable", "error");
        }

        setStatus("Booking…", "loading");

        try {
            const booked = await createCalendarEventFromDrawer();
            if (!booked) {
                setStatus("Booking failed", "error");
                return;
            }
            setStatus("Booked successfully", "success");
            loadSlots();
        } catch (err) {
            console.error(err);
            setStatus("Booking failed", "error");
        }
    });

    window.closeQuickViewBookingModal = () => hideBootstrapModalById(BOOKING_MODAL_ID);
    window.refreshQuickViewBookingSlots = loadSlots;
})();
