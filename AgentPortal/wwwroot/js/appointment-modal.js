(function () {

    let currentContextId = null;
    let currentContextType = null;

    window.openAppointmentModalFromDrawer = function (id, type = "lead") {
        currentContextId = id;
        currentContextType = type;

        const modal = document.getElementById("appointmentModal");
        if (!modal) {
            console.error("Appointment modal missing");
            return;
        }

        modal.style.display = "block";
    };

    window.closeAppointmentModal = function () {
        const modal = document.getElementById("appointmentModal");
        if (modal) modal.style.display = "none";
    };

    window.submitAppointment = async function () {

        const payload = {
            entityId: currentContextId,
            entityType: currentContextType,
            title: document.getElementById("apptTitle").value,
            startUtc: document.getElementById("apptStart").value,
            durationMinutes: parseInt(document.getElementById("apptDuration").value || "30"),
            notes: document.getElementById("apptNotes").value
        };

        const res = await fetch("/calendar/create-event", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(payload)
        });

        if (!res.ok) {
            alert("Failed to create appointment");
            return;
        }

        closeAppointmentModal();

        // refresh CRM state
        window.dispatchEvent(new CustomEvent("crm:appointment-created", {
            detail: payload
        }));
    };

})();
