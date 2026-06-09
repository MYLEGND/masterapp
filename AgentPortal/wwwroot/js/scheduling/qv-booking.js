document.addEventListener("click", async (e) => {

    if (!e.target || e.target.id !== "btnBookAppointment") return;

    const date = document.getElementById("qvBookDate")?.value;
    const time = document.getElementById("qvBookTime")?.value;
    const duration = parseInt(document.getElementById("qvBookDuration")?.value || "30");

    if (!date || !time) {
        alert("Select date/time");
        return;
    }

    const start = new Date(date + "T" + time);
    const end = new Date(start.getTime() + duration * 60000);

    const payload = {
        ownerAgentUserId: window.currentClient?.ownerAgentUserId,
        scheduledStartUtc: start.toISOString(),
        scheduledEndUtc: end.toISOString()
    };

    const res = await fetch("/calendar/book", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
    });

    const status = document.getElementById("qvBookStatus");

    if (status) {
        status.innerText = res.ok ? "BOOKED ✔" : "FAILED ❌";
    }
});
