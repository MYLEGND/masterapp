
let selectedAgent = null;
window.selectedSlot = null;

window.openAppointmentModal = function(agentUserId) {
    selectedAgent = agentUserId;

    const modal = document.getElementById('appointmentModal');
    const dateInput = document.getElementById('apptDate');

    if (!modal || !dateInput) {
        console.warn('Appointment modal markup not found on this page.');
        return;
    }

    modal.classList.remove('hidden');

    const date = new Date().toISOString().split('T')[0];
    dateInput.value = date;

    loadQuickViewSlots(agentUserId, date);
};

document.addEventListener('DOMContentLoaded', () => {
    const apptDate = document.getElementById('apptDate');
    const confirmBookingBtn = document.getElementById('confirmBookingBtn');

    if (!apptDate || !confirmBookingBtn) {
        return;
    }

    apptDate.addEventListener('change', e => {
        loadQuickViewSlots(selectedAgent, e.target.value);
    });

    confirmBookingBtn.addEventListener('click', async () => {
        if (!window.selectedSlot) {
            alert('Select a slot');
            return;
        }

        const res = await fetch('/api/scheduling/book', {
            method: 'POST',
            headers: {'Content-Type':'application/json'},
            body: JSON.stringify({
                agentUserId: selectedAgent,
                startUtc: window.selectedSlot.startUtc,
                endUtc: window.selectedSlot.endUtc
            })
        });

        if (res.ok) {
            location.reload();
        } else {
            alert('Booking failed');
        }
    });
});
