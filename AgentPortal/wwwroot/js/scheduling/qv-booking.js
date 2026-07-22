(() => {
    if (window.__legendQuickViewBookingInitialized) {
        return;
    }

    window.__legendQuickViewBookingInitialized = true;

    let selectedSlotTime = "";
    const calendarState = { visibleMonth: null };
    const optimisticBookedSlots = new Map();
    const BOOKED_SLOT_HIDE_MS = 8000;
    const LIVE_REFRESH_INTERVAL_MS = 30000;
    let availabilityRequestSequence = 0;
    let availabilityRefreshIntervalId = 0;
    let scheduledAvailabilityRefreshIds = [];
    let createEventInFlight = false;

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
        bindBookingModalLifecycle(modalEl);
        return window.bootstrap.Modal.getOrCreateInstance(modalEl);
    }

    function bookingModalIsOpen() {
        const modalEl = bookingModalElement();
        return Boolean(modalEl && modalEl.classList.contains("show"));
    }

    function todayLocal() {
        const today = new Date();
        return new Date(today.getFullYear(), today.getMonth(), today.getDate());
    }

    function startOfMonth(date) {
        return new Date(date.getFullYear(), date.getMonth(), 1);
    }

    function addMonths(date, amount) {
        return new Date(date.getFullYear(), date.getMonth() + amount, 1);
    }

    function parseDateInputValue(value) {
        if (!value || typeof value !== "string") return null;
        const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value.trim());
        if (!match) return null;

        const year = Number(match[1]);
        const month = Number(match[2]) - 1;
        const day = Number(match[3]);
        const date = new Date(year, month, day);

        if (Number.isNaN(date.getTime())) return null;
        if (date.getFullYear() !== year || date.getMonth() !== month || date.getDate() !== day) return null;
        return date;
    }

    function formatDateInputValue(date) {
        const year = date.getFullYear();
        const month = String(date.getMonth() + 1).padStart(2, "0");
        const day = String(date.getDate()).padStart(2, "0");
        return `${year}-${month}-${day}`;
    }

    function isSameDate(a, b) {
        return a.getFullYear() === b.getFullYear()
            && a.getMonth() === b.getMonth()
            && a.getDate() === b.getDate();
    }

    function isBeforeDate(a, b) {
        return a.getTime() < b.getTime();
    }

    function formatHumanDate(date) {
        return date.toLocaleDateString([], {
            weekday: "long",
            month: "long",
            day: "numeric"
        });
    }

    function formatHumanMonth(date) {
        return date.toLocaleDateString([], {
            month: "long",
            year: "numeric"
        });
    }

    function slotReservationKey(dateValue, timeValue) {
        return `${dateValue || ""}::${timeValue || ""}`;
    }

    function pruneOptimisticBookedSlots() {
        const now = Date.now();
        for (const [key, expiresAt] of optimisticBookedSlots.entries()) {
            if (expiresAt <= now) {
                optimisticBookedSlots.delete(key);
            }
        }
    }

    function rememberOptimisticBookedSlot(dateValue, timeValue) {
        if (!dateValue || !timeValue) return;
        pruneOptimisticBookedSlots();
        optimisticBookedSlots.set(slotReservationKey(dateValue, timeValue), Date.now() + BOOKED_SLOT_HIDE_MS);
    }

    function isOptimisticallyBookedSlot(dateValue, timeValue) {
        pruneOptimisticBookedSlots();
        return optimisticBookedSlots.has(slotReservationKey(dateValue, timeValue));
    }

    function clearOptimisticBookedSlots() {
        optimisticBookedSlots.clear();
    }

    function selectedBookingService() {
        const select = $("qvBookDuration");
        const option = select?.selectedOptions?.[0] || null;
        const duration = Number.parseInt(option?.value || select?.value || "", 10);

        return {
            duration: Number.isInteger(duration) && duration > 0 ? duration : null,
            serviceId: option?.dataset?.serviceId || "",
            serviceName: (option?.textContent || "").trim()
        };
    }

    function formatDurationLabel(durationMinutes) {
        const selected = selectedBookingService();

        if (
            selected.serviceName &&
            !selected.serviceName.toLowerCase().includes("loading")
        ) {
            return selected.serviceName;
        }

        const duration = Number.parseInt(durationMinutes || "", 10);

        return Number.isInteger(duration) && duration > 0
            ? `${duration} min meeting`
            : "Select a meeting service";
    }

    function syncBookingServiceOptions(rawServices) {
        const select = $("qvBookDuration");
        if (!select) return false;

        const services = (Array.isArray(rawServices) ? rawServices : [])
            .map(service => {
                const duration = Number.parseInt(
                    service.durationMinutes || service.DurationMinutes || "",
                    10
                );

                return {
                    serviceId: service.serviceId || service.id || "",
                    serviceName: service.serviceName || service.name || "",
                    durationMinutes:
                        Number.isInteger(duration) && duration > 0
                            ? duration
                            : null
                };
            })
            .filter(service => service.serviceId && service.serviceName && service.durationMinutes > 0)
            .sort((a, b) => a.durationMinutes - b.durationMinutes || a.serviceName.localeCompare(b.serviceName));

        if (!services.length) return false;

        const previousServiceId =
            select.selectedOptions?.[0]?.dataset?.serviceId || "";

        const previousDuration = select.value || "";

        select.innerHTML = "";

        for (const service of services) {
            const option = document.createElement("option");
            option.value = String(service.durationMinutes);
            option.dataset.serviceId = service.serviceId;
            option.textContent = service.serviceName;

            if (
                service.serviceId === previousServiceId ||
                (!previousServiceId && String(service.durationMinutes) === String(previousDuration))
            ) {
                option.selected = true;
            }

            select.appendChild(option);
        }

        if (!select.value && select.options.length) {
            select.options[0].selected = true;
        }

        updateSelectionSummary();
        return true;
    }

    function formatSlotLabel(date) {
        return date.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
    }

    function parseLocalSlotDate(value) {
        if (!value) return null;

        const match = String(value).match(/^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?/);
        if (!match) {
            const fallback = new Date(value);
            return Number.isNaN(fallback.getTime()) ? null : fallback;
        }

        const [, y, mo, d, h, mi, se] = match;
        const date = new Date(
            Number(y),
            Number(mo) - 1,
            Number(d),
            Number(h),
            Number(mi),
            Number(se || 0)
        );

        return Number.isNaN(date.getTime()) ? null : date;
    }

    function parseSlotDate(value) {
        return parseLocalSlotDate(value);
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
        if (!status) return;

        if (tone === "selected") {
            status.innerHTML = message || "";
        } else {
            status.innerText = message || "";
        }

        if (tone) status.dataset.state = tone;
        else status.removeAttribute("data-state");
    }

    function setBookingSubmitBusy(isBusy) {
        const submitButton = $("btnBookAppointment");
        createEventInFlight = isBusy;

        if (!submitButton) return;

        submitButton.disabled = isBusy;
        submitButton.setAttribute("aria-busy", isBusy ? "true" : "false");
    }

    function clearScheduledAvailabilityRefreshes() {
        scheduledAvailabilityRefreshIds.forEach((refreshId) => window.clearTimeout(refreshId));
        scheduledAvailabilityRefreshIds = [];
    }

    function scheduleAvailabilityRefresh(delayMs = 0, options = {}) {
        clearScheduledAvailabilityRefreshes();
        const refreshId = window.setTimeout(() => {
            scheduledAvailabilityRefreshIds = scheduledAvailabilityRefreshIds.filter((candidate) => candidate !== refreshId);
            if (!bookingModalIsOpen() || document.hidden) return;
            void loadSlots({
                background: true,
                preferredTime: options.preferredTime,
                preserveStatus: Boolean(options.preserveStatus)
            });
        }, Math.max(0, delayMs));
        scheduledAvailabilityRefreshIds.push(refreshId);
    }

    function queueAvailabilityRefreshes(delays, options = {}) {
        clearScheduledAvailabilityRefreshes();
        for (const delayMs of Array.isArray(delays) ? delays : []) {
            const refreshId = window.setTimeout(() => {
                scheduledAvailabilityRefreshIds = scheduledAvailabilityRefreshIds.filter((candidate) => candidate !== refreshId);
                if (!bookingModalIsOpen() || document.hidden) return;
                void loadSlots({
                    background: true,
                    preferredTime: options.preferredTime,
                    preserveStatus: Boolean(options.preserveStatus)
                });
            }, Math.max(0, delayMs));
            scheduledAvailabilityRefreshIds.push(refreshId);
        }
    }

    function stopLiveAvailabilityRefresh() {
        if (availabilityRefreshIntervalId) {
            window.clearInterval(availabilityRefreshIntervalId);
            availabilityRefreshIntervalId = 0;
        }
        clearScheduledAvailabilityRefreshes();
    }

    function startLiveAvailabilityRefresh() {
        stopLiveAvailabilityRefresh();
        availabilityRefreshIntervalId = window.setInterval(() => {
            if (!bookingModalIsOpen() || document.hidden) return;
            void loadSlots({
                background: true,
                preferredTime: selectedSlotTime || $("qvBookTime")?.value || ""
            });
        }, LIVE_REFRESH_INTERVAL_MS);
    }

    function updateSelectionSummary() {
        const selectedDate = parseDateInputValue($("qvBookDate")?.value || "");
        const duration = selectedBookingService().duration;
        const selectedDateLabel = $("qvBookingSelectedDateLabel");
        const selectedMeta = $("qvBookingSelectedMeta");
        const slotFocus = $("qvBookingSlotFocus");
        const slotNote = $("qvBookingSlotNote");

        if (!selectedDate) {
            if (selectedDateLabel) selectedDateLabel.textContent = "Choose a date";
            if (selectedMeta) selectedMeta.textContent = "Available times will update as soon as you switch dates or duration.";
            if (slotFocus) slotFocus.textContent = "Pick a date";
            if (slotNote) slotNote.textContent = "Open Outlook slots for the selected day appear here automatically.";
            return;
        }

        const humanDate = formatHumanDate(selectedDate);
        if (selectedDateLabel) selectedDateLabel.textContent = humanDate;
        if (selectedMeta) selectedMeta.textContent = `${formatDurationLabel(duration)}. Pick any open slot on the right to lock in the appointment.`;
        if (slotFocus) slotFocus.textContent = humanDate;
        if (slotNote) slotNote.textContent = `Open Outlook slots for ${humanDate} appear here automatically.`;
    }

    function setSelectedDate(valueOrDate) {
        const date = valueOrDate instanceof Date
            ? new Date(valueOrDate.getFullYear(), valueOrDate.getMonth(), valueOrDate.getDate())
            : parseDateInputValue(valueOrDate);
        const dateInput = $("qvBookDate");
        if (!date || !dateInput) return false;

        dateInput.value = formatDateInputValue(date);
        calendarState.visibleMonth = startOfMonth(date);
        updateSelectionSummary();
        renderCalendar();
        return true;
    }

    function syncBookingContext() {
        const name = ($("dName")?.textContent || "").trim() || "Current contact";
        const email = ($("dEmail")?.textContent || "").trim().replace(/^[-—]\s*$/, "");
        const phone = ($("dPhone")?.textContent || "").trim().replace(/^[-—]\s*$/, "");
        const recordLabel = $("qvBookingClientLabel");
        const recordSub = $("qvBookingClientSub");

        if (recordLabel) {
            recordLabel.textContent = name;

            const initials = name
                .split(/\s+/)
                .filter(Boolean)
                .slice(0, 2)
                .map(part => part.charAt(0).toUpperCase())
                .join("") || "•";

            recordLabel.closest(".qv-booking-record")?.setAttribute("data-initials", initials);
        }
        if (recordSub) {
            const parts = [email, phone].filter(Boolean);
            recordSub.textContent = parts.length
                ? parts.join(" • ")
                : "Pick a date, choose a free slot, and create the appointment without leaving Quick View.";
        }

        const durationInput = $("qvBookDuration");

        if (durationInput && !durationInput.value) {
            durationInput.selectedIndex = -1;
        }
    }

    function ensureDefaultBookingDate() {
        const dateInput = $("qvBookDate");
        if (!dateInput) return null;

        const nextDate = $("dNextDate")?.value || "";
        const existing = parseDateInputValue(dateInput.value || nextDate);
        const today = todayLocal();
        const resolved = existing && !isBeforeDate(existing, today) ? existing : today;

        setSelectedDate(resolved);
        return resolved;
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

    function removeBookedSlotFromUi(dateValue, timeValue) {
        if (!dateValue || !timeValue) return "";

        rememberOptimisticBookedSlot(dateValue, timeValue);

        const container = $("qvBookSlots");
        const timeInput = $("qvBookTime");
        let removedLabel = timeValue;

        if (container) {
            const slotButtons = Array.from(container.querySelectorAll(".qv-slot-btn"));
            const bookedButton = slotButtons.find((button) => button.dataset.time === timeValue) || null;
            if (bookedButton) {
                removedLabel = (bookedButton.textContent || "").trim() || removedLabel;
                bookedButton.remove();
            }

            if (!container.querySelector(".qv-slot-btn")) {
                container.innerHTML = '<span class="qv-slot-empty">That time is no longer open. Checking the next available openings now…</span>';
            }
        }

        if (selectedSlotTime === timeValue) {
            selectedSlotTime = "";
        }
        if (timeInput && timeInput.value === timeValue) {
            timeInput.value = "";
        }

        return removedLabel;
    }

    function calendarHasRenderedDays() {
        return Boolean(document.querySelector("#qvBookingCalendar [data-qv-booking-date]"));
    }

    function renderCalendar() {
        const grid = $("qvBookingCalendar");
        const monthLabel = $("qvBookingMonthLabel");
        const prevButton = $("qvBookPrevMonth");
        if (!grid) return;

        const selectedDate = parseDateInputValue($("qvBookDate")?.value || "") || todayLocal();
        const today = todayLocal();
        const currentMonth = startOfMonth(today);

        if (!(calendarState.visibleMonth instanceof Date) || Number.isNaN(calendarState.visibleMonth.getTime())) {
            calendarState.visibleMonth = startOfMonth(selectedDate);
        }

        const visibleMonth = startOfMonth(calendarState.visibleMonth);
        if (monthLabel) monthLabel.textContent = formatHumanMonth(visibleMonth);
        if (prevButton) prevButton.disabled = visibleMonth.getTime() <= currentMonth.getTime();

        const firstGridDay = new Date(visibleMonth.getFullYear(), visibleMonth.getMonth(), 1 - visibleMonth.getDay());
        const cells = [];

        for (let index = 0; index < 42; index += 1) {
            const day = new Date(firstGridDay.getFullYear(), firstGridDay.getMonth(), firstGridDay.getDate() + index);
            const iso = formatDateInputValue(day);
            const isOutside = day.getMonth() !== visibleMonth.getMonth();
            const isDisabled = isBeforeDate(day, today);
            const isToday = isSameDate(day, today);
            const isSelected = isSameDate(day, selectedDate);

            const classNames = [
                "qv-cal-day",
                isOutside ? "is-outside" : "",
                isDisabled ? "is-disabled" : "",
                isToday ? "is-today" : "",
                isSelected ? "is-selected" : ""
            ].filter(Boolean).join(" ");

            cells.push(`
                <button
                    type="button"
                    class="${classNames}"
                    data-qv-booking-date="${iso}"
                    aria-pressed="${isSelected ? "true" : "false"}"
                    ${isDisabled ? "disabled" : ""}>
                    <span>${day.getDate()}</span>
                </button>
            `);
        }

        grid.innerHTML = cells.join("");
    }

    function renderSlots(freeSlots, durationMinutes, slotIntervalMinutes, options = {}) {
        const container = $("qvBookSlots");
        const selectedDate = parseDateInputValue($("qvBookDate")?.value || "");
        const selectedDateValue = $("qvBookDate")?.value || "";
        const requestedSelection = (options.preferredTime || selectedSlotTime || $("qvBookTime")?.value || "").trim();
        const preserveStatus = Boolean(options.preserveStatus);
        if (!container) return;

        container.innerHTML = "";
        const timeInput = $("qvBookTime");
        let restoredSelection = "";

        const generated = [];
        for (const slot of freeSlots || []) {
            const start = getSlotStart(slot);
            const end = getSlotEnd(slot);
            if (!start || !end || end <= start) continue;

            let cursor = new Date(start);
            while (cursor.getTime() + durationMinutes * 60000 <= end.getTime()) {
                const candidate = new Date(cursor);
                const timeValue = toTimeValue(candidate);
                const now = new Date();
                const isPastCandidate = candidate.getTime() <= now.getTime();

                if (!isPastCandidate && !isOptimisticallyBookedSlot(selectedDateValue, timeValue)) {
                    generated.push(candidate);
                }
                cursor = new Date(cursor.getTime() + (slotIntervalMinutes || 30) * 60000);
            }
        }

        if (!generated.length) {
            const dateLabel = selectedDate ? formatHumanDate(selectedDate) : "the selected day";
            if (preserveStatus) {
                container.innerHTML = `<span class="qv-slot-empty">No open ${durationMinutes}-minute slots on ${dateLabel}. Try another day in the calendar.</span>`;
                selectedSlotTime = "";
                if (timeInput) timeInput.value = "";
            } else {
                clearSlots(`No open ${durationMinutes}-minute slots on ${dateLabel}. Try another day in the calendar.`, "warning");
            }
            return;
        }

        const dateLabel = selectedDate ? formatHumanDate(selectedDate) : "the selected day";

        for (const start of generated) {
            const button = document.createElement("button");
            button.type = "button";
            button.className = "btn btn-ghost qv-slot-btn";
            button.textContent = formatSlotLabel(start);
            button.dataset.time = toTimeValue(start);

            button.addEventListener("click", () => {
                selectedSlotTime = button.dataset.time;
                if (timeInput) timeInput.value = selectedSlotTime;

                container.querySelectorAll(".qv-slot-btn").forEach((item) => item.classList.remove("selected"));
                button.classList.add("selected");
                setStatus(`Selected <span class="qv-status-highlight">${button.textContent}</span> on <span class="qv-status-highlight">${dateLabel}</span>.`, "selected");
            });

            if (button.dataset.time === requestedSelection) {
                button.classList.add("selected");
                restoredSelection = button.dataset.time;
            }

            container.appendChild(button);
        }

        if (restoredSelection) {
            selectedSlotTime = restoredSelection;
            if (timeInput) timeInput.value = restoredSelection;

            if (!preserveStatus) {
                const selectedButton = Array.from(container.querySelectorAll(".qv-slot-btn"))
                    .find((button) => button.dataset.time === restoredSelection);
                if (selectedButton) {
                    setStatus(`Selected <span class="qv-status-highlight">${selectedButton.textContent}</span> on <span class="qv-status-highlight">${dateLabel}</span>.`, "selected");
                }
            }
            return;
        }

        selectedSlotTime = "";
        if (timeInput) timeInput.value = "";

        if (!preserveStatus) {
            setStatus(`${generated.length} open start ${generated.length === 1 ? "time" : "times"} found for ${dateLabel}.`, "selected");
        }
    }

    async function loadSlots(options = {}) {
        const date = $("qvBookDate")?.value || "";
        const selectedDate = parseDateInputValue(date);
        let duration = selectedBookingService().duration;
        const background = Boolean(options.background);
        const preferredTime = (options.preferredTime || selectedSlotTime || $("qvBookTime")?.value || "").trim();
        const preserveStatus = Boolean(options.preserveStatus);
        const requestSequence = ++availabilityRequestSequence;

        updateSelectionSummary();

        if (!date || !selectedDate) {
            clearSlots("Choose a day from the calendar to see open slots.", "warning");
            return;
        }

        if (!background) {
            clearSlots(`Loading ${duration}-minute slots for ${formatHumanDate(selectedDate)}…`, "loading");
        } else if (!preserveStatus) {
            setStatus(`Refreshing ${duration}-minute slots for ${formatHumanDate(selectedDate)}…`, "loading");
        }

        try {
            const response = await fetch(`/calendar/day-availability?date=${encodeURIComponent(date)}&v=${Date.now()}`, {
                credentials: "include"
            });

            if (!response.ok) {
                const text = await response.text().catch(() => "");
                throw new Error(text || "Availability failed");
            }

            const data = await response.json();
            if (requestSequence !== availabilityRequestSequence) {
                return;
            }

            syncBookingServiceOptions(Array.isArray(data.buffers) ? data.buffers : data.services);
            duration = selectedBookingService().duration;

            const freeSlots = Array.isArray(data.freeSlots) ? data.freeSlots : [];
            const slotInterval = Number.parseInt(
                data.slotIntervalMinutes || "",
                10
            );

            if (!Number.isInteger(slotInterval) || slotInterval <= 0) {
                clearSlots(
                    "Microsoft Bookings did not return a valid availability interval.",
                    "error"
                );
                return;
            }

            renderSlots(freeSlots, duration, slotInterval, {
                preferredTime,
                preserveStatus
            });
        } catch (error) {
            if (requestSequence !== availabilityRequestSequence) {
                return;
            }
            console.error(error);
            if (background) {
                setStatus("Live availability refresh failed. Keeping the current times on screen.", "warning");
            } else {
                clearSlots("Could not load slots for that day right now.", "error");
            }
        }
    }

    function initializeBookingModal() {
        syncBookingContext();
        ensureDefaultBookingDate();
        renderCalendar();
        loadSlots();
    }

    function resetBookingModalState() {
        selectedSlotTime = "";
        stopLiveAvailabilityRefresh();
        clearOptimisticBookedSlots();
        availabilityRequestSequence += 1;
        setBookingSubmitBusy(false);
        const timeInput = $("qvBookTime");
        if (timeInput) timeInput.value = "";
        setStatus("");
    }

    function bindBookingModalLifecycle(modalEl) {
        const resolvedModal = modalEl || bookingModalElement();
        if (!resolvedModal || resolvedModal.dataset.qvBookingLifecycleBound === "1") {
            return resolvedModal;
        }

        resolvedModal.dataset.qvBookingLifecycleBound = "1";

        resolvedModal.addEventListener("shown.bs.modal", () => {
            if (!calendarHasRenderedDays()) {
                initializeBookingModal();
            }
            startLiveAvailabilityRefresh();
            window.requestAnimationFrame(() => {
                $("btnBookAppointment")?.blur?.();
            });
        });

        resolvedModal.addEventListener("hidden.bs.modal", () => {
            resetBookingModalState();
        });

        return resolvedModal;
    }

    function openBookingModal() {
        closeLegacyOverlayModals();
        reconcileBootstrapModalState();
        const modal = bookingModalInstance();
        if (!modal) return;
        initializeBookingModal();
        modal.show();
        window.setTimeout(() => {
            if (!calendarHasRenderedDays()) {
                initializeBookingModal();
            }
        }, 32);
    }

    document.addEventListener("change", (event) => {
        if (!event.target) return;

        if (event.target.id === "qvBookDuration") {
            loadSlots();
        }
    });

    document.addEventListener("click", (event) => {
        const trigger = event.target?.closest?.("[data-qv-booking-open]");
        if (!trigger) return;

        event.preventDefault();
        openBookingModal();
    });

    document.addEventListener("click", (event) => {
        const prev = event.target?.closest?.("#qvBookPrevMonth");
        if (prev) {
            event.preventDefault();
            const todayMonth = startOfMonth(todayLocal());
            const nextVisible = addMonths(calendarState.visibleMonth || todayMonth, -1);
            if (nextVisible.getTime() >= todayMonth.getTime()) {
                calendarState.visibleMonth = nextVisible;
                renderCalendar();
            }
            return;
        }

        const next = event.target?.closest?.("#qvBookNextMonth");
        if (next) {
            event.preventDefault();
            calendarState.visibleMonth = addMonths(calendarState.visibleMonth || todayLocal(), 1);
            renderCalendar();
            return;
        }

        const dayButton = event.target?.closest?.("[data-qv-booking-date]");
        if (!dayButton) return;

        event.preventDefault();
        const iso = dayButton.getAttribute("data-qv-booking-date") || "";
        if (setSelectedDate(iso)) {
            void loadSlots();
        }
    });



    /*
     * Single authority for converting a selected Microsoft Bookings slot
     * and service duration into the controller's local ISO contract.
     */

    /*
     * Sole HTTP route authority for calendar appointment creation.
     * Page adapters may provide their own transport so authentication and
     * anti-forgery behavior remain page-specific.
     */
    window.qvCalendarCreateEvent = async function qvCalendarCreateEvent(
        payload,
        request
    ) {
        if (typeof request !== "function") {
            throw new Error(
                "Calendar create-event request transport is unavailable."
            );
        }

        return await request(
            "/calendar/create-event",
            payload
        );
    };

    /*
     * Sole authority for Outlook status, availability loading, conflict
     * detection, formatting and busy/free-slot rendering.
     */
    window.createQuickViewBusyCalendar =
        function createQuickViewBusyCalendar(options) {
            if (!options || typeof options !== "object") {
                throw new Error(
                    "Quick View busy-calendar options are required."
                );
            }

            const getElements =
                typeof options.getElements === "function"
                    ? options.getElements
                    : () => ({});

            const getSelectedDate =
                typeof options.getSelectedDate === "function"
                    ? options.getSelectedDate
                    : () => "";

            const normalize =
                typeof options.normalize === "function"
                    ? options.normalize
                    : value => String(value || "").trim();

            const escapeHtml =
                typeof options.escapeHtml === "function"
                    ? options.escapeHtml
                    : value => String(value || "");

            const wrapRequest =
                typeof options.wrapRequest === "function"
                    ? options.wrapRequest
                    : init => init;

            const statusCacheTtlMs =
                Number.isFinite(options.statusCacheTtlMs)
                    ? Math.max(0, options.statusCacheTtlMs)
                    : 0;

            let statusCache = null;
            let statusCacheAt = 0;
            let activeAbortController = null;

            function parseLocalIso(value) {
                if (!value) return null;

                const match = String(value).match(
                    /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?/
                );

                if (!match) {
                    const fallback = new Date(value);

                    return Number.isNaN(fallback.getTime())
                        ? null
                        : fallback;
                }

                const [, year, month, day, hour, minute, second] =
                    match;

                const parsed = new Date(
                    Number(year),
                    Number(month) - 1,
                    Number(day),
                    Number(hour),
                    Number(minute),
                    Number(second || 0)
                );

                return Number.isNaN(parsed.getTime())
                    ? null
                    : parsed;
            }

            function formatBusyRange(item) {
                if (item?.isAllDay) {
                    return "All day";
                }

                return `${
                    item?.startLabel || ""
                } - ${
                    item?.endLabel || ""
                }`.trim();
            }

            function isBusyConflict(
                item,
                selectedStart,
                selectedEnd
            ) {
                if (!selectedStart || !selectedEnd) {
                    return false;
                }

                if (item?.isAllDay) {
                    return true;
                }

                const start = parseLocalIso(item?.startIso);
                const end = parseLocalIso(item?.endIso);

                if (
                    !start ||
                    !end ||
                    Number.isNaN(start.getTime()) ||
                    Number.isNaN(end.getTime())
                ) {
                    return false;
                }

                return (
                    start < selectedEnd &&
                    end > selectedStart
                );
            }

            async function getCalendarStatus() {
                const now = Date.now();

                if (
                    statusCacheTtlMs > 0 &&
                    statusCache &&
                    now - statusCacheAt < statusCacheTtlMs
                ) {
                    return statusCache;
                }

                try {
                    const response = await fetch(
                        "/calendar/status",
                        wrapRequest({
                            credentials: "include"
                        })
                    );

                    statusCache = response.ok
                        ? await response.json()
                        : { connected: false };

                    statusCacheAt = now;
                    return statusCache;
                } catch {
                    statusCache = { connected: false };
                    statusCacheAt = now;
                    return statusCache;
                }
            }

            function renderState(
                message,
                tone = "neutral"
            ) {
                const {
                    busyList,
                    busyNote,
                    freeList,
                    workHours
                } = getElements();

                if (
                    !busyList ||
                    !busyNote ||
                    !freeList ||
                    !workHours
                ) {
                    return;
                }

                busyList.innerHTML = "";
                freeList.innerHTML = "";

                workHours.textContent =
                    "Uses Outlook work hours or your default " +
                    "7:00 AM - 7:00 PM";

                busyNote.textContent = message;

                busyNote.style.color =
                    tone === "error"
                        ? "#b91c1c"
                        : tone === "good"
                            ? "#166534"
                            : "#7f1d1d";
            }

            function render(
                items,
                freeSlots = [],
                calendarWorkHours = null
            ) {
                const {
                    busyList,
                    busyNote,
                    freeList,
                    workHours
                } = getElements();

                if (
                    !busyList ||
                    !busyNote ||
                    !freeList ||
                    !workHours
                ) {
                    return;
                }

                const nextDate =
                    normalize(getSelectedDate());

                const selected = nextDate
                    ? window.qvBookingBuildEventTimes(nextDate)
                    : null;

                const selectedStart =
                    selected?.startISO
                        ? new Date(selected.startISO)
                        : null;

                const selectedEnd =
                    selected?.endISO
                        ? new Date(selected.endISO)
                        : null;

                const normalizedItems =
                    Array.isArray(items) ? items : [];

                const conflicts = normalizedItems.filter(
                    item => isBusyConflict(
                        item,
                        selectedStart,
                        selectedEnd
                    )
                );

                if (calendarWorkHours?.enabled) {
                    workHours.textContent =
                        `Work hours: ${
                            calendarWorkHours.startLabel
                        } - ${
                            calendarWorkHours.endLabel
                        }${
                            calendarWorkHours.source === "outlook"
                                ? ""
                                : " (default)"
                        }`;
                } else {
                    workHours.textContent =
                        "This day is outside your Outlook work week";
                }

                if (freeSlots.length) {
                    freeList.innerHTML = freeSlots.map(slot => `
                        <button
                            type="button"
                            class="calendar-free-slot"
                            data-free-slot-time="${
                                escapeHtml(
                                    slot.startTimeValue || ""
                                )
                            }"
                            data-free-slot-start="${
                                escapeHtml(
                                    slot.startLabel || ""
                                )
                            }">
                            ${
                                escapeHtml(
                                    `${
                                        slot.startLabel || ""
                                    } - ${
                                        slot.endLabel || ""
                                    }`.trim()
                                )
                            }
                        </button>
                    `).join("");
                } else {
                    freeList.innerHTML =
                        `<div class="calendar-free-empty">${
                            calendarWorkHours?.enabled
                                ? "No open booking windows inside " +
                                  "your work hours for this day."
                                : "No booking windows because this " +
                                  "day is outside your work hours."
                        }</div>`;
                }

                if (!normalizedItems.length) {
                    busyNote.textContent =
                        "No busy blocks found on your Outlook " +
                        "calendar for this day.";

                    busyNote.style.color = "#166534";

                    busyList.innerHTML =
                        '<div class="calendar-busy-empty">' +
                        "Open day. No busy blocks found." +
                        "</div>";

                    return;
                }

                busyNote.textContent = conflicts.length
                    ? `${conflicts.length} conflict${
                        conflicts.length === 1 ? "" : "s"
                    } with the currently selected meeting time.`
                    : "Busy blocks for the selected day. Your " +
                      "current time does not overlap any of them.";

                busyNote.style.color =
                    conflicts.length
                        ? "#b91c1c"
                        : "#166534";

                busyList.innerHTML =
                    normalizedItems.map(item => {
                        const conflict = isBusyConflict(
                            item,
                            selectedStart,
                            selectedEnd
                        );

                        const showAs =
                            normalize(item?.showAs) || "busy";

                        const label =
                            showAs.charAt(0).toUpperCase() +
                            showAs.slice(1);

                        return `
                            <div class="calendar-busy-item ${
                                conflict ? "conflict" : ""
                            }">
                                <div class="calendar-busy-main">
                                    <div class="calendar-busy-subject">${
                                        escapeHtml(
                                            item?.subject || "Busy"
                                        )
                                    }</div>
                                    <div class="calendar-busy-meta">${
                                        escapeHtml(label)
                                    }</div>
                                    ${
                                        conflict
                                            ? '<div class="calendar-busy-flag">' +
                                              "Conflicts with selected time" +
                                              "</div>"
                                            : ""
                                    }
                                </div>
                                <div class="calendar-busy-time">${
                                    escapeHtml(
                                        formatBusyRange(item)
                                    )
                                }</div>
                            </div>
                        `;
                    }).join("");
            }

            async function refresh() {
                const {
                    busyDate
                } = getElements();

                if (!busyDate) {
                    return;
                }

                const nextDate =
                    normalize(getSelectedDate());

                busyDate.textContent =
                    nextDate || "Select a date";

                if (!nextDate) {
                    renderState(
                        "Choose a Next Action Date to see your " +
                        "Outlook busy blocks for that day."
                    );
                    return;
                }

                const status =
                    await getCalendarStatus();

                if (!status.connected) {
                    renderState(
                        "Connect your Outlook calendar to load " +
                        "busy times for this day.",
                        "error"
                    );
                    return;
                }

                if (activeAbortController) {
                    activeAbortController.abort();
                }

                activeAbortController =
                    new AbortController();

                renderState(
                    "Loading busy times from Outlook..."
                );

                try {
                    const response = await fetch(
                        `/calendar/day-availability?date=${
                            encodeURIComponent(nextDate)
                        }`,
                        wrapRequest({
                            credentials: "include",
                            signal:
                                activeAbortController.signal
                        })
                    );

                    if (!response.ok) {
                        const text =
                            await response
                                .text()
                                .catch(() => "");

                        throw new Error(
                            text ||
                            "Calendar availability failed."
                        );
                    }

                    const data =
                        await response.json();

                    if (!data.connected) {
                        renderState(
                            "Calendar availability is unavailable " +
                            "right now. You can still create the " +
                            "meeting manually.",
                            "error"
                        );
                        return;
                    }

                    render(
                        Array.isArray(data.items)
                            ? data.items
                            : [],
                        Array.isArray(data.freeSlots)
                            ? data.freeSlots
                            : [],
                        data.workHours || null
                    );
                } catch (error) {
                    if (error?.name === "AbortError") {
                        return;
                    }

                    console.error(error);

                    renderState(
                        "Could not load Outlook busy times for " +
                        "this day.",
                        "error"
                    );
                }
            }

            return Object.freeze({
                refresh
            });
        };

    window.qvBookingBuildEventTimes = function qvBookingBuildEventTimes(dateISO) {
        const bookingTime =
            document.getElementById("qvBookTime")?.value?.trim() || "";

        const bookingDurationValue =
            document.getElementById("qvBookDuration")?.value?.trim() || "";

        const duration =
            Number.parseInt(bookingDurationValue, 10);

        if (
            !dateISO ||
            !bookingTime ||
            !Number.isInteger(duration) ||
            duration <= 0
        ) {
            return {
                startISO: "",
                endISO: ""
            };
        }

        const base =
            new Date(`${dateISO}T${bookingTime}:00`);

        if (Number.isNaN(base.getTime())) {
            return {
                startISO: "",
                endISO: ""
            };
        }

        const end =
            new Date(base.getTime() + duration * 60 * 1000);

        const toLocalIsoNoZ = date => {
            const localDate =
                new Date(
                    date.getTime() -
                    date.getTimezoneOffset() * 60000
                );

            return localDate
                .toISOString()
                .slice(0, 19);
        };

        return {
            startISO: toLocalIsoNoZ(base),
            endISO: toLocalIsoNoZ(end)
        };
    };

    async function createQuickViewCalendarEvent() {
        window.__lastBookingStopReason = "";

        if (createEventInFlight) {
            window.__lastBookingStopReason =
                "booking already in progress";
            return false;
        }

        const adapter = window.quickViewCalendarAdapter;

        if (
            !adapter ||
            typeof adapter.getContext !== "function" ||
            typeof adapter.request !== "function" ||
            typeof adapter.applyResult !== "function" ||
            typeof adapter.toast !== "function"
        ) {
            window.__lastBookingStopReason =
                "calendar page adapter unavailable";

            console.error(
                "Quick View calendar adapter is unavailable."
            );

            return false;
        }

        let context;

        try {
            context = adapter.getContext();
        } catch (error) {
            console.error(error);

            window.__lastBookingStopReason =
                error?.message || "calendar context failed";

            adapter.toast(
                error?.message ||
                "Calendar booking context could not be prepared."
            );

            return false;
        }

        const recordId = context?.recordId || "";
        const row = context?.row || null;
        const nextDate = context?.nextDate || "";
        const nextText = context?.nextText || "";

        if (!recordId) {
            adapter.toast("Open a lead or client first.");
            window.__lastBookingStopReason = "missing recordId";
            return false;
        }

        if (!row) {
            adapter.toast("Lead or client record not found.");
            window.__lastBookingStopReason =
                `record row not found for ${recordId}`;
            return false;
        }

        if (!nextDate) {
            adapter.toast("Set a Next Action Date first.");
            window.__lastBookingStopReason = "missing nextDate";
            return false;
        }

        if (!nextText) {
            adapter.toast("Add Next Action text first.");
            window.__lastBookingStopReason = "missing nextText";
            return false;
        }

        if (!context.startISO || !context.endISO) {
            adapter.toast("Calendar event time could not be prepared.");
            window.__lastBookingStopReason =
                "missing calendar event times";
            return false;
        }

        const payload = {
            clientUserId: recordId,
            clientProfileId: context.clientProfileId || null,
            workstationLeadId: context.workstationLeadId || null,
            subject: `Client Follow-up: ${context.fullName || ""}`,
            startISO: context.startISO,
            endISO: context.endISO,
            body:
                `Next Action: ${nextText}\n\n` +
                `Pipeline Stage: ${context.pipelineStage || ""}\n` +
                `Client: ${context.fullName || ""}\n` +
                `Email: ${context.email || ""}\n` +
                `Phone: ${context.phone || ""}`,
            location: context.location || "",
            zoomJoinUrl: context.zoomJoinUrl || "",
            activityNote: `Calendar event created: ${nextText}`
        };

        try {
            setBookingSubmitBusy(true);

            const data = await window.qvCalendarCreateEvent(
                payload,
                adapter.request.bind(adapter)
            );

            await adapter.applyResult(data, context);

            if (data?.warning) {
                console.warn(data.warning);
                adapter.toast(data.warning);
            } else {
                adapter.toast("Calendar event created");
            }
            return true;
        } catch (error) {
            console.error(error);

            window.__lastBookingStopReason =
                error?.message || "calendar create failed";

            adapter.toast(
                error?.message || "Calendar create failed."
            );

            return false;
        } finally {
            setBookingSubmitBusy(false);
        }
    }

    window.createQuickViewCalendarEvent =
        createQuickViewCalendarEvent;

    document.addEventListener("click", async event => {
        const trigger =
            event.target?.closest?.("#btnCreateCalendarEvent");

        if (!trigger) return;

        event.preventDefault();

        if (trigger.disabled) return;

        trigger.disabled = true;

        try {
            await createQuickViewCalendarEvent();
        } finally {
            trigger.disabled = false;
        }
    });

    document.addEventListener("click", async (event) => {
        const trigger =
            event.target?.closest?.("#btnBookAppointment");

        if (!trigger) return;

        event.preventDefault();

        if (createEventInFlight || trigger.disabled) {
            return;
        }

        const date = $("qvBookDate")?.value || "";
        const time = $("qvBookTime")?.value || selectedSlotTime;
        const durationValue = $("qvBookDuration")?.value || "";
        const duration = Number.parseInt(durationValue, 10);

        if (!date) {
            setStatus("Choose a day from the calendar first.", "warning");
            return;
        }

        if (!time) {
            setStatus("Pick an open time on the right before booking.", "warning");
            return;
        }

        if (!Number.isInteger(duration) || duration <= 0) {
            setStatus(
                "Select a valid service duration from your connected Microsoft Bookings services.",
                "error"
            );
            return;
        }

        const nextDate = $("dNextDate");
        const nextText = $("dNextText");

        if (nextDate) nextDate.value = date;
        if (nextText && !nextText.value.trim()) {
            nextText.value = "Appointment booked from Quick View";
        }

        setStatus("Booking appointment…", "loading");

        try {
            const booked = await createQuickViewCalendarEvent();
            if (!booked) {
                setStatus(`Booking stopped: ${window.__lastBookingStopReason || "unknown frontend guard"}`, "error");
                return;
            }

            const selectedDateValue = parseDateInputValue(date);
            const selectedDateLabel = selectedDateValue ? formatHumanDate(selectedDateValue) : date;
            const bookedLabel = removeBookedSlotFromUi(date, time);
            setStatus(`Booked ${bookedLabel} on ${selectedDateLabel}. Refreshing live availability…`, "success");

            void loadSlots({
                background: true,
                preferredTime: "",
                preserveStatus: true
            });
            queueAvailabilityRefreshes([1200, 4500, 9000], {
                preferredTime: "",
                preserveStatus: true
            });
        } catch (error) {
            console.error(error);
            setStatus("Booking failed", "error");
        }
    });

    window.addEventListener("focus", () => {
        if (!bookingModalIsOpen()) return;
        scheduleAvailabilityRefresh(180, {
            preferredTime: selectedSlotTime || $("qvBookTime")?.value || ""
        });
    });

    document.addEventListener("visibilitychange", () => {
        if (document.hidden || !bookingModalIsOpen()) return;
        scheduleAvailabilityRefresh(220, {
            preferredTime: selectedSlotTime || $("qvBookTime")?.value || ""
        });
    });

    window.closeQuickViewBookingModal = () => hideBootstrapModalById(BOOKING_MODAL_ID);
    window.refreshQuickViewBookingSlots = loadSlots;
})();
