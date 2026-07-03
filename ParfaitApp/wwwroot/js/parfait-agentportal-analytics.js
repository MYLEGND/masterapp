(() => {
    const pageRoot = document.getElementById("parfaitAnalyticsPage");
    if (!pageRoot) {
        return;
    }

    function ensureBrowserTimezoneInUrl() {
        let url;
        try {
            url = new URL(window.location.href);
        } catch {
            return false;
        }

        if (url.searchParams.has("timezoneId") && url.searchParams.has("timezoneOffsetMinutes")) {
            return false;
        }

        const timezoneId = Intl?.DateTimeFormat?.().resolvedOptions?.().timeZone || "";
        const timezoneOffsetMinutes = String(new Date().getTimezoneOffset());
        if (!timezoneId && !timezoneOffsetMinutes) {
            return false;
        }

        if (timezoneId) {
            url.searchParams.set("timezoneId", timezoneId);
        }

        url.searchParams.set("timezoneOffsetMinutes", timezoneOffsetMinutes);
        window.location.replace(url.toString());
        return true;
    }

    if (ensureBrowserTimezoneInUrl()) {
        return;
    }

    const endpoints = {
        metaConnect: "/internal/analytics/meta-connect",
        metaConnectionStatus: "/internal/analytics/meta-connection-status",
        metaCampaigns: "/internal/analytics/meta-campaigns",
        metaDisconnect: "/internal/analytics/meta-disconnect"
    };

    const connectBtn = document.getElementById("meta-connect-btn");
    const disconnectBtn = document.getElementById("meta-disconnect-btn");
    const statusEl = document.getElementById("meta-connection-status");
    const campaignsBtn = document.getElementById("meta-campaigns-open");
    const campaignsModal = document.getElementById("pfMetaCampaignsModal");
    const trafficQualityModeSelect = document.getElementById("traffic-quality-mode");
    const trafficQualityEmptyState = document.getElementById("traffic-quality-empty-state");
    const disconnectForm = document.getElementById("meta-disconnect-form");
    const openAdsLink = document.getElementById("openMetaAdsManagerLink");
    const accountChip = document.getElementById("meta-campaigns-account-chip");
    const accountValue = document.getElementById("pf-meta-campaign-account-value");
    const businessValue = document.getElementById("pf-meta-campaign-business-value");
    const syncValue = document.getElementById("pf-meta-campaign-sync-value");
    const rangeValue = document.getElementById("meta-campaigns-range-label");
    const noteValue = document.getElementById("meta-campaigns-note");
    const campaignsBody = document.getElementById("meta-campaigns-body");
    const statusBaseClass = "wa-kpi-meta-status";

    if (!connectBtn || !statusEl) {
        return;
    }

    function buildUrlWithParams(baseUrl, params) {
        const url = new URL(baseUrl, window.location.origin);
        Object.entries(params).forEach(([key, value]) => {
            if (value === null || value === undefined || value === "") {
                return;
            }

            url.searchParams.set(key, value);
        });

        return `${url.pathname}${url.search}`;
    }

    function normalizeQualityMode(value) {
        switch (String(value || "").trim().toLowerCase()) {
            case "real_human_traffic":
            case "real_human":
                return "real_human_traffic";
            case "likely_human":
                return "likely_human";
            case "reviewed_needed":
            case "review":
                return "reviewed_needed";
            case "suspicious_activity":
            case "suspicious":
                return "suspicious_activity";
            case "likely_bots_automation":
            case "likely_bot":
                return "likely_bots_automation";
            case "internal_qa":
            case "internal":
                return "internal_qa";
            case "all_traffic":
            case "all":
            default:
                return "all_traffic";
        }
    }

    function formatShortDate(iso) {
        if (!iso) {
            return "Not synced yet";
        }

        try {
            return new Date(iso).toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
        } catch {
            return "Not synced yet";
        }
    }

    function formatMoney(value) {
        const amount = Number(value) || 0;
        return new Intl.NumberFormat("en-US", {
            style: "currency",
            currency: "USD",
            minimumFractionDigits: 2,
            maximumFractionDigits: 2
        }).format(amount);
    }

    function formatInt(value) {
        return new Intl.NumberFormat("en-US").format(Number(value) || 0);
    }

    function formatPercent(value) {
        return `${(Number(value) || 0).toFixed(2)}%`;
    }

    function escapeHtml(value) {
        return String(value ?? "")
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll("\"", "&quot;")
            .replaceAll("'", "&#39;");
    }

    function setText(element, value) {
        if (element) {
            element.textContent = value;
        }
    }

    function setMetaCampaignsBodyMessage(message, tone = "muted") {
        if (!campaignsBody) {
            return;
        }

        const className = tone === "danger"
            ? "text-danger"
            : tone === "warning"
                ? "text-warning"
                : "fa-empty";
        campaignsBody.innerHTML = `<tr><td colspan="20" class="${className}">${escapeHtml(message)}</td></tr>`;
    }

    function pill(text, cls) {
        return `<span class="meta-pill ${cls || "meta-neutral"}">${escapeHtml(text ?? "—")}</span>`;
    }

    function toNumber(value) {
        const parsed = Number(value);
        return Number.isFinite(parsed) ? parsed : 0;
    }

    function metaStatusClass(status) {
        const normalized = String(status || "").toUpperCase();
        if (normalized === "ACTIVE") return "meta-good";
        if (normalized === "PAUSED" || normalized === "LIMITED") return "meta-warn";
        if (normalized === "ARCHIVED" || normalized === "DELETED" || normalized === "DISAPPROVED") return "meta-bad";
        return "meta-neutral";
    }

    function metaCampaignNameClass(row) {
        const statusClass = metaStatusClass(row?.status);
        const leads = toNumber(row?.leads);
        const ctr = toNumber(row?.ctr);

        if (statusClass === "meta-bad") return "meta-bad";
        if (statusClass === "meta-warn") return "meta-warn";
        if (statusClass === "meta-good") {
            return leads > 0 || ctr >= 1.5 ? "meta-good" : "meta-warn";
        }

        return leads > 0 ? "meta-good" : "meta-neutral";
    }

    function metaObjectiveClass(objective) {
        const normalized = String(objective || "").toUpperCase();
        if (normalized.includes("LEAD") || normalized.includes("CONVERSION") || normalized.includes("OUTCOME")) return "meta-good";
        if (normalized.includes("TRAFFIC") || normalized.includes("AWARENESS") || normalized.includes("ENGAGEMENT")) return "meta-warn";
        return "meta-neutral";
    }

    function metaImpressionsClass(value) {
        const total = toNumber(value);
        if (total <= 0) return "meta-bad";
        if (total < 1000) return "meta-warn";
        return "meta-good";
    }

    function metaReachClass(value) {
        const total = toNumber(value);
        if (total <= 0) return "meta-bad";
        if (total < 500) return "meta-warn";
        return "meta-good";
    }

    function metaClicksClass(value) {
        const total = toNumber(value);
        if (total <= 0) return "meta-bad";
        if (total < 10) return "meta-warn";
        return "meta-good";
    }

    function metaLeadsClass(value) {
        const total = toNumber(value);
        if (total <= 0) return "meta-bad";
        if (total < 3) return "meta-warn";
        return "meta-good";
    }

    function metaLeadGapClass(value) {
        const gap = Math.abs(toNumber(value));
        if (gap === 0) return "meta-good";
        if (gap <= 2) return "meta-warn";
        return "meta-bad";
    }

    function metaSpendClass(row) {
        const spend = toNumber(row?.spend);
        const leads = toNumber(row?.leads);

        if (spend <= 0) return "meta-neutral";
        if (leads <= 0) return "meta-bad";

        const cpl = spend / leads;
        if (cpl <= 5) return "meta-good";
        if (cpl <= 15) return "meta-warn";
        return "meta-bad";
    }

    function metaCtrClass(value) {
        const ctr = toNumber(value);
        if (ctr < 1) return "meta-bad";
        if (ctr < 2.5) return "meta-warn";
        return "meta-good";
    }

    function metaCpcClass(value) {
        const cpc = toNumber(value);
        if (cpc <= 0) return "meta-warn";
        if (cpc <= 2) return "meta-good";
        if (cpc <= 5) return "meta-warn";
        return "meta-bad";
    }

    function metaCpmClass(value) {
        const cpm = toNumber(value);
        if (cpm <= 0) return "meta-warn";
        if (cpm <= 15) return "meta-good";
        if (cpm <= 30) return "meta-warn";
        return "meta-bad";
    }

    function renderCampaignRow(row) {
        return `
            <tr>
                <td>${pill(row.campaignName || "—", `${metaCampaignNameClass(row)} meta-campaign-name-pill`)}<div class="fa-muted small mt-1">${escapeHtml(row.campaignId || "")}</div></td>
                <td>${pill(row.status || "—", metaStatusClass(row.status))}</td>
                <td>${pill(row.objective || "—", metaObjectiveClass(row.objective))}</td>
                <td class="text-end">${pill(formatMoney(row.spend), metaSpendClass(row))}</td>
                <td class="text-end">${pill(formatInt(row.impressions), metaImpressionsClass(row.impressions))}</td>
                <td class="text-end">${pill(formatInt(row.reach), metaReachClass(row.reach))}</td>
                <td class="text-end">${pill(formatInt(row.clicks), metaClicksClass(row.clicks))}</td>
                <td class="text-end">${pill(formatPercent(row.ctr), metaCtrClass(row.ctr))}</td>
                <td class="text-end">${pill(formatMoney(row.cpc), metaCpcClass(row.cpc))}</td>
                <td class="text-end">${pill(formatMoney(row.cpm), metaCpmClass(row.cpm))}</td>
                <td class="text-end">${pill(formatInt(row.leads), metaLeadsClass(row.leads))}</td>
                <td class="text-end">${pill(formatInt(row.websiteLeads), metaLeadsClass(row.websiteLeads))}</td>
                <td class="text-end">${pill(formatInt(row.websiteLeadGap), metaLeadGapClass(row.websiteLeadGap))}</td>
                <td class="text-end">${pill(formatInt(row.qualifiedLeads), metaLeadsClass(row.qualifiedLeads))}</td>
                <td class="text-end">${pill(formatInt(row.appointments), metaLeadsClass(row.appointments))}</td>
                <td class="text-end">${pill(formatInt(row.applications), metaLeadsClass(row.applications))}</td>
                <td class="text-end">${pill(formatInt(row.policiesIssued), metaLeadsClass(row.policiesIssued))}</td>
                <td class="text-end">${pill(formatInt(row.policiesPaid), metaLeadsClass(row.policiesPaid))}</td>
                <td class="text-end">${pill(formatMoney(row.paidPremium), row.paidPremium > 0 ? "meta-good" : "meta-neutral")}</td>
                <td class="text-end">${pill(`${toNumber(row.premiumRoas).toFixed(2)}x`, row.premiumRoas > 0 ? "meta-good" : "meta-neutral")}</td>
            </tr>`;
    }

    function renderMetaCampaigns(data) {
        setText(rangeValue, data?.rangeLabel || pageRoot.dataset.preset || "30d");
        setText(accountValue, data?.accountName || data?.accountId || "Not connected");
        setText(syncValue, formatShortDate(data?.syncedUtc));

        if (accountChip) {
            accountChip.classList.remove("d-none");
            accountChip.textContent = data?.accountName || data?.accountId || "Connected";
            accountChip.style.opacity = "1";
        }

        if (noteValue) {
            noteValue.textContent = data?.comparisonNote
                || "Meta Leads = Meta Ads API lead reporting. Website Leads = tracked website conversions captured in Parfait analytics.";
        }

        if (!campaignsBody) {
            return;
        }

        const rows = Array.isArray(data?.rows) ? data.rows : [];
        if (!rows.length) {
            setMetaCampaignsBodyMessage("No Meta campaign rows were returned for the selected analytics range.");
            return;
        }

        campaignsBody.innerHTML = rows.map(renderCampaignRow).join("");
    }

    function currentAnalyticsParams() {
        const url = new URL(window.location.href);
        return {
            preset: url.searchParams.get("preset") || pageRoot.dataset.preset || "30d",
            fromUtc: url.searchParams.get("fromUtc") || pageRoot.dataset.fromUtc || "",
            toUtc: url.searchParams.get("toUtc") || pageRoot.dataset.toUtc || "",
            qualityMode: normalizeQualityMode(url.searchParams.get("qualityMode") || pageRoot.dataset.qualityMode || "all_traffic"),
            timezoneId: url.searchParams.get("timezoneId") || pageRoot.dataset.timezoneId || "",
            timezoneOffsetMinutes: url.searchParams.get("timezoneOffsetMinutes") || pageRoot.dataset.timezoneOffsetMinutes || ""
        };
    }

    function updateTrafficQualityEmptyState() {
        if (!trafficQualityEmptyState) {
            return;
        }

        const hasRows =
            Number(pageRoot.dataset.summaryPageViews || 0) > 0 ||
            Number(pageRoot.dataset.summarySessions || 0) > 0 ||
            Number(pageRoot.dataset.summaryVisitors || 0) > 0 ||
            Number(pageRoot.dataset.summaryVerifiedLeads || 0) > 0;

        if (hasRows) {
            trafficQualityEmptyState.hidden = true;
            trafficQualityEmptyState.textContent = "";
            return;
        }

        const messages = {
            real_human_traffic: "No real human traffic detected",
            likely_human: "No likely human traffic detected",
            reviewed_needed: "No reviewed-needed traffic detected",
            suspicious_activity: "No suspicious activity detected",
            likely_bots_automation: "No likely bot or automation traffic detected",
            internal_qa: "No internal / QA traffic detected",
            all_traffic: "No traffic detected"
        };

        const mode = normalizeQualityMode(
            trafficQualityModeSelect?.value ||
            currentAnalyticsParams().qualityMode ||
            pageRoot.dataset.qualityMode ||
            "all_traffic");

        trafficQualityEmptyState.textContent = messages[mode] || "No traffic detected";
        trafficQualityEmptyState.hidden = false;
    }

    function navigateWithQualityMode(qualityMode) {
        let url;
        try {
            url = new URL(window.location.href);
        } catch {
            return;
        }

        url.searchParams.set("qualityMode", normalizeQualityMode(qualityMode));
        window.location.assign(url.toString());
    }

    function updateMetaConnectHref() {
        const params = {
            returnUrl: `${window.location.pathname}${window.location.search}`
        };

        connectBtn.href = buildUrlWithParams(endpoints.metaConnect, params);
    }

    function setMetaConnectState(enabled, label, title = "") {
        connectBtn.textContent = label;
        connectBtn.classList.toggle("disabled", !enabled);
        connectBtn.setAttribute("aria-disabled", enabled ? "false" : "true");
        connectBtn.tabIndex = enabled ? 0 : -1;
        connectBtn.title = title;

        if (enabled) {
            updateMetaConnectHref();
        } else {
            connectBtn.href = "#";
        }
    }

    function setMetaCampaignsEnabled(enabled) {
        if (!(campaignsBtn instanceof HTMLButtonElement)) {
            return;
        }

        campaignsBtn.disabled = !enabled;
        campaignsBtn.classList.toggle("disabled", !enabled);
        campaignsBtn.setAttribute("aria-disabled", enabled ? "false" : "true");
        campaignsBtn.title = enabled ? "View Meta campaigns" : "Connect Meta Ads to view campaigns";
    }

    function setMetaAccountChip(text, connected = true) {
        if (!accountChip) {
            return;
        }

        accountChip.classList.remove("d-none");
        accountChip.textContent = text || (connected ? "Connected" : "Not connected");
        accountChip.style.opacity = connected ? "1" : ".75";
    }

    function setMetaCampaignFields(data) {
        setText(accountValue, data?.accountName || data?.accountId || "Not connected");
        setText(businessValue, data?.businessName || data?.businessId || "—");
        setText(syncValue, formatShortDate(data?.connectedUtc));
    }

    async function fetchJson(url) {
        const response = await fetch(url, {
            credentials: "same-origin",
            headers: {
                "X-Requested-With": "XMLHttpRequest"
            }
        });

        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            throw new Error(data?.message || "Request failed.");
        }

        return data;
    }

    async function fetchPostJson(url) {
        const token = disconnectForm?.querySelector('input[name="__RequestVerificationToken"]')?.value ?? "";
        const response = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "Content-Type": "application/json",
                "RequestVerificationToken": token,
                "X-Requested-With": "XMLHttpRequest"
            }
        });

        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            throw new Error(data?.message || "Request failed.");
        }

        return data;
    }

    async function loadMetaCampaigns() {
        setMetaCampaignsBodyMessage("Loading Meta campaign performance…");

        try {
            const data = await fetchJson(buildUrlWithParams(endpoints.metaCampaigns, currentAnalyticsParams()));
            renderMetaCampaigns(data);
        } catch (error) {
            setMetaCampaignsBodyMessage(error?.message || "Unable to load Meta campaigns.", "danger");
            console.error(error);
        }
    }

    async function loadMetaConnectionStatus() {
        updateMetaConnectHref();

        try {
            const data = await fetchJson(endpoints.metaConnectionStatus);

            if (!data || !data.connected) {
                statusEl.className = `${statusBaseClass} text-warning`;
                statusEl.textContent = data?.message || "Meta Ads not connected for Parfait.";
                setMetaConnectState(true, "Connect Meta Ads", "Connect Meta Ads for Parfait");
                if (disconnectBtn) {
                    disconnectBtn.style.display = "none";
                }

                setMetaCampaignsEnabled(false);
                setMetaAccountChip("Not connected", false);
                setMetaCampaignFields(null);
                setMetaCampaignsBodyMessage("Connect Meta Ads to load campaign performance.", "warning");

                if (openAdsLink) {
                    openAdsLink.href = "https://adsmanager.facebook.com/adsmanager/manage/campaigns";
                }

                return;
            }

            const account = data.accountName || data.accountId || "Meta account connected";
            const user = data.metaUserName ? ` as ${data.metaUserName}` : "";
            const expiry = data.accessTokenExpiresUtc ? ` · expires ${formatShortDate(data.accessTokenExpiresUtc)}` : "";

            statusEl.className = `${statusBaseClass} text-success`;
            statusEl.textContent = `Connected: ${account}${user}${expiry}`;
            setMetaConnectState(true, "Reconnect Meta Ads", "Reconnect Meta Ads for Parfait");
            if (disconnectBtn) {
                disconnectBtn.style.display = "";
            }

            setMetaCampaignsEnabled(true);
            setMetaAccountChip(account, true);
            setMetaCampaignFields(data);

            if (openAdsLink && data.accountId) {
                const accountId = String(data.accountId).replace(/^act_/, "");
                const businessId = data.businessId ? String(data.businessId).trim() : "";
                const params = new URLSearchParams();
                if (businessId) {
                    params.set("business_id", businessId);
                    params.set("global_scope_id", businessId);
                }

                params.set("act", accountId);
                openAdsLink.href = `https://adsmanager.facebook.com/adsmanager/manage/campaigns?${params.toString()}`;
            }
        } catch (error) {
            statusEl.className = `${statusBaseClass} text-danger`;
            statusEl.textContent = "Unable to read Meta Ads connection status.";
            setMetaConnectState(false, "Status Unavailable", statusEl.textContent);
            if (disconnectBtn) {
                disconnectBtn.style.display = "none";
            }

            setMetaCampaignsEnabled(false);
            setMetaAccountChip("Status unavailable", false);
            setMetaCampaignsBodyMessage("Unable to verify Meta Ads connection right now.", "danger");
            console.error(error);
        }
    }

    async function handleMetaDisconnect() {
        try {
            await fetchPostJson(endpoints.metaDisconnect);
            await loadMetaConnectionStatus();
        } catch (error) {
            statusEl.className = `${statusBaseClass} text-danger`;
            statusEl.textContent = error?.message || "Failed to disconnect Meta Ads.";
            console.error(error);
        }
    }

    function readMetaCallbackState() {
        let url;
        try {
            url = new URL(window.location.href);
        } catch {
            return null;
        }

        const meta = url.searchParams.get("meta");
        if (!meta) {
            return null;
        }

        const state = {
            meta,
            message: url.searchParams.get("message") || ""
        };

        url.searchParams.delete("meta");
        url.searchParams.delete("message");
        window.history.replaceState({}, "", url.toString());
        return state;
    }

    function applyMetaCallbackState(state) {
        if (!state) {
            return;
        }

        if (state.meta === "connected") {
            statusEl.className = `${statusBaseClass} text-success`;
            statusEl.textContent = "Meta Ads connected successfully.";
        } else if (state.meta === "error") {
            statusEl.className = `${statusBaseClass} text-danger`;
            statusEl.textContent = state.message || "Meta Ads connection failed.";
        }
    }

    connectBtn.addEventListener("click", event => {
        if (connectBtn.getAttribute("aria-disabled") === "true") {
            event.preventDefault();
        }
    });

    if (trafficQualityModeSelect instanceof HTMLSelectElement) {
        trafficQualityModeSelect.value = normalizeQualityMode(
            trafficQualityModeSelect.value ||
            currentAnalyticsParams().qualityMode ||
            pageRoot.dataset.qualityMode ||
            "all_traffic");
        trafficQualityModeSelect.addEventListener("change", () => {
            navigateWithQualityMode(trafficQualityModeSelect.value);
        });
    }

    disconnectBtn?.addEventListener("click", () => {
        void handleMetaDisconnect();
    });

    campaignsModal?.addEventListener("show.bs.modal", () => {
        if (campaignsBtn?.getAttribute("aria-disabled") === "true") {
            setMetaCampaignsBodyMessage("Connect Meta Ads to load campaign performance.", "warning");
            return;
        }

        void loadMetaCampaigns();
    });

    const metaCallbackState = readMetaCallbackState();
    void loadMetaConnectionStatus().finally(() => {
        applyMetaCallbackState(metaCallbackState);
    });

    updateTrafficQualityEmptyState();
})();
