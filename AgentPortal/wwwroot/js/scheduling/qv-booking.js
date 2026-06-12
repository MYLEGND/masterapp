document.addEventListener("click", async (e) => {
    if (!e.target || e.target.id !== "btnBookAppointment") return;

    const date = document.getElementById("qvBookDate")?.value || "";
    const time = document.getElementById("qvBookTime")?.value || "";
    const duration = document.getElementById("qvBookDuration")?.value || "30";
    const status = document.getElementById("qvBookStatus");

    if (!date || !time) {
        if (status) status.innerText = "Select date/time";
        else alert("Select date/time");
        return;
    }

    const nextDate = document.getElementById("dNextDate");
    const nextText = document.getElementById("dNextText");
    const meetingTime = document.getElementById("dMeetingTime");
    const meetingDuration = document.getElementById("dMeetingDuration");

    if (nextDate) nextDate.value = date;
    if (meetingTime) meetingTime.value = time;
    if (meetingDuration) meetingDuration.value = duration;
    if (nextText && !nextText.value.trim()) {
        nextText.value = "Appointment booked from Quick View";
    }

    if (typeof createCalendarEventFromDrawer !== "function") {
        console.error("createCalendarEventFromDrawer is unavailable on this page.");
        if (status) status.innerText = "Calendar flow unavailable";
        return;
    }

    if (status) status.innerText = "Booking…";

    try {
        await createCalendarEventFromDrawer();
        if (status) status.innerText = "BOOKED ✔";
    } catch (err) {
        console.error(err);
        if (status) status.innerText = "FAILED ❌";
    }
});
