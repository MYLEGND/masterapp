(() => {
    /* ========= DATA ========= */
    const $  = (sel, root=document) => root.querySelector(sel);
    const $$ = (sel, root=document) => Array.from(root.querySelectorAll(sel));
    const leadsDataEl = document.getElementById("leads-data");
    const rawLeads = leadsDataEl ? JSON.parse(leadsDataEl.textContent || "[]") : [];
    let leads = rawLeads;
    const liveSync = window.liveSync;

    const buckets = [
        { id: "MortgageProtection", title: "Mortgage Protection", tone: "stage-newlead" },
        { id: "FinalExpense", title: "Final Expense", tone: "stage-contacted" },
        { id: "LifeInsurance", title: "Life Insurance", tone: "stage-qualified" },
        { id: "Medicare", title: "Medicare", tone: "stage-meetingscheduled" },
        { id: "DisabilityInsurance", title: "Disability", tone: "stage-opportunities" }
    ];

    const shell = document.getElementById("leadsBoard");
    if (!shell) return;

    const renderBuckets = (data) => {
        shell.innerHTML = "";
        buckets.forEach(b => {
            const items = data.filter(x => x.bucket === b.id);
            const col = document.createElement("div");
            col.className = `stage ${b.tone}`;
            col.innerHTML = `
                <div class="stage-head">
                    <div class="stage-title">${b.title}</div>
                    <div class="stage-note">Script leads aligned to this play.</div>
                    <span class="stage-count">${items.length} contacts</span>
                </div>
                <div class="stage-body">${items.length ? items.map(renderCard).join("") : '<div class=\"stage-empty\">No contacts in this bucket right now.</div>'}</div>
            `;
            shell.appendChild(col);
        });
        updateMyDay(data);
    };

    function updateCallCountEverywhere(leadId, next){
        if (!leadId) return;
        leads = leads.map(l => l.leadId === leadId ? { ...l, callCount: next } : l);
        document.querySelectorAll(`[data-call-count-for="${leadId}"]`).forEach(el => {
            el.dataset.count = String(next);
            el.textContent = next;
        });
        const drawerCount = document.getElementById("qvCallCount");
        if (drawerCount && drawerCount.textContent.toLowerCase().includes("calls")){
            drawerCount.textContent = `Calls: ${next}`;
        }
        updateMyDay(leads);
    }

    if (liveSync){
        liveSync.onCall((leadId, callCount) => {
            updateCallCountEverywhere(leadId, callCount);
        });
    }

    const renderCard = (lead) => {
        const name = `${lead.firstName ?? ""} ${lead.lastName ?? ""}`.trim() || "(No name)";
        const phone = lead.phone || "";
        const phone2 = lead.phone2 || "";
        const callCount = lead.callCount ?? 0;
        const notes = (lead.crmNotes || "").slice(0,120);
        return `
            <article class="card contact-card" data-id="${lead.leadId}">
                <div class="card-top">
                    <div class="name">${name}</div>
                    <div class="meta">Calls: <span class="pill pill-small call-count" data-count="${callCount}" data-call-count-for="${lead.leadId}" style="color:#b91c1c;font-weight:900;font-size:14px;">${callCount}</span></div>
                </div>
                <div class="card-body">
                    <div class="row"><strong>Phone:</strong> ${phone}</div>
                    ${phone2 ? `<div class="row"><strong>Alt:</strong> ${phone2}</div>` : ""}
                    ${lead.email ? `<div class="row"><strong>Email:</strong> ${lead.email}</div>` : ""}
                    ${notes ? `<div class="row muted">${notes}</div>` : ""}
                </div>
                <div class="card-actions">
                    <a class="btn btn-gold" href="tel:${phone}" data-lead="${lead.leadId}" data-action="call">Call</a>
                    <a class="btn btn-ghost" href="sms:${phone}" data-action="text">Text</a>
                    <button class="btn btn-ghost" type="button" data-action="qv" data-id="${lead.leadId}">Quick View</button>
                </div>
            </article>
        `;
    };

    /* ========= FILTERS & SEARCH ========= */
    function applyFilters(){
        const bucket = (document.getElementById("bucketFilter")?.value || "").trim();
        const sortBy = document.getElementById("sortBy")?.value || "calls_asc";
        const q = (document.getElementById("leadSearchInput")?.value || "").toLowerCase();
        let list = [...leads];
        if (bucket) list = list.filter(x => (x.bucket || "").toLowerCase() === bucket.toLowerCase());
        if (q){
            list = list.filter(x => `${x.firstName||""} ${x.lastName||""} ${x.email||""} ${x.phone||""}`.toLowerCase().includes(q));
        }
        switch(sortBy){
            case "name_desc": list.sort((a,b)=>`${b.lastName} ${b.firstName}`.localeCompare(`${a.lastName} ${a.firstName}`)); break;
            case "calls_desc": list.sort((a,b)=>(b.callCount||0)-(a.callCount||0)); break;
            case "calls_asc": list.sort((a,b)=>(a.callCount||0)-(b.callCount||0)); break;
            default: list.sort((a,b)=>`${a.lastName} ${a.firstName}`.localeCompare(`${b.lastName} ${b.firstName}`)); break;
        }
        renderBuckets(list);
    }

    const searchInput = document.getElementById("leadSearchInput");
    searchInput?.addEventListener("input", () => applyFilters());
    document.getElementById("bucketFilter")?.addEventListener("change", applyFilters);
    document.getElementById("sortBy")?.addEventListener("change", applyFilters);

    // Stage picker (mirrors Clients page UX)
    const stageSelect = document.getElementById("stagePickerSelect");
    const stageDetail = document.getElementById("stagePickerDetail");
    const stageOpen = document.getElementById("btnStagePickerOpen");
    const bucketMap = Object.fromEntries(buckets.map(b => [b.id, b]));

    function setStage(value){
        const bucketFilter = document.getElementById("bucketFilter");
        if (bucketFilter){
            bucketFilter.value = value || "";
        }
        const meta = document.getElementById("stagePickerSelect")?.querySelector(`option[value='${value}']`);
        const nameEl = document.getElementById("stagePickerName");
        const noteEl = document.getElementById("stagePickerNote");
        const countEl = document.getElementById("stagePickerCount");
        if (meta){
            nameEl && (nameEl.textContent = meta.textContent || "");
            noteEl && (noteEl.textContent = meta.dataset.note || "");
            countEl && (countEl.textContent = meta.dataset.count || "0");
        }
        applyFilters();
    }

    stageSelect?.addEventListener("change", (e) => setStage(e.target.value));
    stageDetail?.addEventListener("click", () => setStage(stageSelect?.value));
    stageOpen?.addEventListener("click", () => setStage(stageSelect?.value));

    const fetchLeads = async () => {
        const res = await fetch("/ScriptsCrm/Leads");
        const data = await res.json();
        renderBuckets(data);
    };

    document.addEventListener("click", async (e) => {
        const callBtn = e.target.closest("a[data-lead][data-action=\"call\"]");
        if (callBtn) {
            e.preventDefault();
            const id = callBtn.dataset.lead;
            const href = callBtn.getAttribute("href");
            const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? "";
            const proceed = window.confirm("Start call and log this attempt?");
            if (!proceed) return;
            try {
                const res = await fetch("/ScriptsCrm/IncrementCall", {
                    method: "POST",
                    headers: {
                        "Content-Type": "application/x-www-form-urlencoded",
                        "RequestVerificationToken": token
                    },
                    body: new URLSearchParams({ id })
                });
                if (res.ok){
                    const json = await res.json().catch(() => ({}));
                    const newCount = json.callCount ?? undefined;
                    const next = newCount ?? ((leads.find(l => l.leadId === id)?.callCount || 0) + 1);
                    updateCallCountEverywhere(id, next);
                    liveSync?.sendCall(id, next);
                }
            } catch {}
            if (href) window.location.href = href;
            return; // allow tel link navigation
        }

        const qvBtn = e.target.closest("[data-action=\"qv\"]");
        const card = qvBtn ? qvBtn.closest(".contact-card") : e.target.closest(".contact-card");
        if (!card || (!qvBtn && e.target.closest("a"))) return;
        const id = qvBtn ? qvBtn.dataset.id : card.dataset.id;
        const res = await fetch(`/ScriptsCrm/Lead?id=${encodeURIComponent(id)}`);
        if (!res.ok) return;
        const lead = await res.json();
        const qv = document.getElementById("leadQuickView");
        if (!qv) return;
        qv.style.display = "block";
        const fmt = (v) => v ?? "";
        document.getElementById("qvName").textContent = `${fmt(lead.firstName)} ${fmt(lead.lastName)}`.trim() || "Lead";
        document.getElementById("qvBucket").textContent = lead.bucket;
        document.getElementById("qvCallCount").textContent = `Calls: ${lead.callCount ?? 0}`;
        document.getElementById("qvPhone").textContent = fmt(lead.phone);
        document.getElementById("qvPhone2").textContent = fmt(lead.phone2);
        document.getElementById("qvEmail").textContent = fmt(lead.email);
        document.getElementById("qvAddress").textContent = [lead.addressLine, lead.city, lead.state, lead.zipCode].filter(x=>x).join(", ");
        document.getElementById("qvLender").textContent = fmt(lead.mortgageLender);
        document.getElementById("qvLoan").textContent = fmt(lead.loanAmount);
        document.getElementById("qvDob").textContent = lead.dob ? lead.dob.substring(0,10) : "";
        document.getElementById("qvGender").textContent = fmt(lead.gender);
        document.getElementById("qvNotes").textContent = fmt(lead.crmNotes);
        document.getElementById("qvStage").textContent = fmt(lead.crmStage);
        document.getElementById("qvStatus").textContent = fmt(lead.crmStatus);
    });

    // Stats / MyDay
    function updateMyDay(list){
        const total = list.length;
        const called = list.filter(x => (x.callCount||0) > 0).length;
        const bucketsUsed = new Set(list.map(l => l.bucket).filter(Boolean)).size;
        const kTotal = document.getElementById("kTotal");
        const kCalls = document.getElementById("kCalls");
        const kBuckets = document.getElementById("kBuckets");
        if (kTotal) {
            kTotal.setAttribute("data-value", String(total));
            kTotal.textContent = String(total);
        }
        if (kCalls) {
            kCalls.setAttribute("data-value", String(called));
            kCalls.textContent = String(called);
        }
        if (kBuckets) {
            kBuckets.setAttribute("data-value", String(bucketsUsed || buckets.length));
            kBuckets.textContent = String(bucketsUsed || buckets.length);
        }
    }

    // initial render
    applyFilters();
})();
