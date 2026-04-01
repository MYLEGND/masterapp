// Production modal logic for Clients CRM
(function () {
    function loadProductionHistory(clientId) {
        const list = document.getElementById('prodHistoryList');
        if (!list) return;
        list.innerHTML = '<div class="text-muted">Loading…</div>';
        fetch(`/production/history/client?clientId=${encodeURIComponent(clientId)}`)
            .then(res => res.json())
            .then(data => {
                if (!data || !data.length) {
                    list.innerHTML = '<div class="text-muted">No production yet.</div>';
                    return;
                }
                list.innerHTML = '';
                data.forEach(item => {
                    const div = document.createElement('div');
                    div.className = 'prod-history-item';
                    div.innerHTML = `
                        <div><b>$${Number(item.amount).toLocaleString(undefined,{maximumFractionDigits:2})}</b> | Personal: $${Number(item.personalAmount||0).toLocaleString(undefined,{maximumFractionDigits:2})} | <span class="badge bg-secondary">${item.status}</span></div>
                        <div class="small text-muted">${item.notes||''}</div>
                        <div class="mt-1">
                            <button class="btn btn-sm btn-outline-primary prod-edit" data-id="${item.id}" data-amount="${item.amount}" data-personal="${item.personalAmount||''}" data-status="${item.status}" data-notes="${item.notes||''}">Edit</button>
                            <button class="btn btn-sm btn-outline-danger prod-delete" data-id="${item.id}">Delete</button>
                        </div>
                    `;
                    list.appendChild(div);
                });
                list.querySelectorAll('.prod-edit').forEach(btn => {
                    btn.addEventListener('click', function() {
                        document.getElementById('prodRecordId').value = btn.getAttribute('data-id');
                        document.querySelector('input[name="amount"]').value = btn.getAttribute('data-amount');
                        document.querySelector('input[name="personalAmount"]').value = btn.getAttribute('data-personal');
                        document.querySelector('select[name="status"]').value = btn.getAttribute('data-status');
                        document.querySelector('textarea[name="notes"]').value = btn.getAttribute('data-notes');
                    });
                });
                list.querySelectorAll('.prod-delete').forEach(btn => {
                    btn.addEventListener('click', function() {
                        if (!confirm('Delete this production entry?')) return;
                        fetch('/production/delete', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                            body: `id=${encodeURIComponent(btn.getAttribute('data-id'))}`
                        }).then(() => loadProductionHistory(clientId));
                    });
                });
            });
    }

    document.querySelectorAll('.view-production-btn').forEach(btn => {
        btn.addEventListener('click', function() {
            const clientId = btn.getAttribute('data-client-id');
            const clientName = btn.getAttribute('data-client-name');
            document.getElementById('prodClientId').value = clientId;
            document.getElementById('prodClientName').textContent = clientName;
            document.getElementById('prodRecordId').value = '';
            document.getElementById('productionForm').reset();
            loadProductionHistory(clientId);
            const modalEl = document.getElementById('productionModal');
            if (modalEl && window.bootstrap?.Modal) {
                window.bootstrap.Modal.getOrCreateInstance(modalEl).show();
            }
        });
    });

    document.getElementById('productionForm')?.addEventListener('submit', function(e) {
        e.preventDefault();
        const clientId = document.getElementById('prodClientId').value;
        const id = document.getElementById('prodRecordId').value;
        const amount = this.amount.value;
        const personalAmount = this.personalAmount.value;
        const status = this.status.value;
        const notes = this.notes.value;
        const url = id ? '/production/update' : '/production/add/client';
        const body = id
            ? `id=${encodeURIComponent(id)}&amount=${encodeURIComponent(amount)}&personalAmount=${encodeURIComponent(personalAmount)}&status=${encodeURIComponent(status)}&notes=${encodeURIComponent(notes)}`
            : `clientId=${encodeURIComponent(clientId)}&amount=${encodeURIComponent(amount)}&personalAmount=${encodeURIComponent(personalAmount)}&status=${encodeURIComponent(status)}&notes=${encodeURIComponent(notes)}`;
        fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
            body
        }).then(() => {
            loadProductionHistory(clientId);
            this.reset();
            document.getElementById('prodRecordId').value = '';
        });
    });

    // Add a hidden field for record id
    if (!document.getElementById('prodRecordId')) {
        const input = document.createElement('input');
        input.type = 'hidden';
        input.id = 'prodRecordId';
        input.name = 'id';
        document.getElementById('productionForm')?.appendChild(input);
    }
})();
