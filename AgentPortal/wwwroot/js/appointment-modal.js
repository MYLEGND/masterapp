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

        window.bootstrap?.Modal?.getOrCreateInstance(modal).show();
    };

    window.closeAppointmentModal = function () {
        const modal = document.getElementById("appointmentModal");
        if (modal) window.bootstrap?.Modal?.getOrCreateInstance(modal).hide();
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

        if (typeof window.qvCalendarCreateEvent !== "function") {
            console.error(
                "Shared calendar create-event dispatcher is unavailable."
            );
            alert("Appointment scheduling is unavailable.");
            return;
        }

        const res = await window.qvCalendarCreateEvent(
            payload,
            async (url, requestPayload) => {
                return await fetch(url, {
                    method: "POST",
                    credentials: "include",
                    headers: {
                        "Content-Type": "application/json"
                    },
                    body: JSON.stringify(requestPayload)
                });
            }
        );

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

    document.getElementById("apptCreate")?.addEventListener("click", () => {
        window.submitAppointment();
    });

})();
