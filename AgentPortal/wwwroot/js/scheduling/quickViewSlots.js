
async function loadQuickViewSlots(agentUserId, date) {
    const res = await fetch(`/api/calendaravailability/slots?agentUserId=${agentUserId}&date=${date}`);
    const slots = await res.json();

    const container = document.getElementById('quickViewSlots');
    container.innerHTML = '';

    slots.forEach(s => {
        const btn = document.createElement('button');
        btn.className = 'slot-btn';
        btn.innerText = new Date(s.startUtc).toLocaleTimeString([], {hour:'2-digit', minute:'2-digit'});

        btn.onclick = () => {
            window.selectedSlot = s;
        };

        container.appendChild(btn);
    });
}

window.loadQuickViewSlots = loadQuickViewSlots;
