(() => {
    let selectedSlotTime = "";

    const $ = (id) => document.getElementById(id);

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

    function setStatus(message) {
        const status = $("qvBookStatus");
        if (status) status.innerText = message || "";
    }

    function clearSlots(message) {
        const container = $("qvBookSlots");
        if (container) container.innerHTML = "";
        selectedSlotTime = "";
        const timeInput = $("qvBookTime");
        if (timeInput) timeInput.value = "";
        if (message) setStatus(message);
    }

    function renderSlots(freeSlots, durationMinutes) {
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
                cursor = new Date(cursor.getTime() + 15 * 60000);
            }
        }

        if (!generated.length) {
            setStatus("No open slots");
            return;
        }

        setStatus("");

        for (const start of generated) {
            const btn = document.createElement("button");
            btn.type = "button";
            btn.className = "btn btn-ghost qv-slot-btn";
            btn.textContent = formatSlotLabel(start);
            btn.dataset.time = slot.startTimeValue || toTimeValue(start);

            btn.addEventListener("click", () => {
                selectedSlotTime = btn.dataset.time;
                if (timeInput) timeInput.value = selectedSlotTime;

                container.querySelectorAll(".qv-slot-btn").forEach(x => x.classList.remove("selected"));
                btn.classList.add("selected");
                setStatus(`Selected ${btn.textContent}`);
            });

            container.appendChild(btn);
        }
    }

    async function loadSlots() {
        const date = $("qvBookDate")?.value || "";
        const duration = parseInt($("qvBookDuration")?.value || "30", 10) || 30;

        if (!date) {
            clearSlots("Select date");
            return;
        }

        clearSlots("Loading slots…");

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
            renderSlots(freeSlots, duration);
        } catch (err) {
            console.error(err);
            clearSlots("Could not load slots");
        }
    }

    document.addEventListener("change", (e) => {
        if (!e.target) return;
        if (e.target.id === "qvBookDate" || e.target.id === "qvBookDuration") {
            loadSlots();
        }
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
            return setStatus("Calendar flow unavailable");
        }

        setStatus("Booking…");

        try {
            await createCalendarEventFromDrawer();
            setStatus("BOOKED ✔");
            loadSlots();
        } catch (err) {
            console.error(err);
            setStatus("FAILED ❌");
        }
    });

    window.refreshQuickViewBookingSlots = loadSlots;
})();
