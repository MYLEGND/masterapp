(() => {
    const pageRoot = document.getElementById("parfaitAnalyticsPage");
    if (!pageRoot) {
        return;
    }

    const viewerTimezoneCookieName = "ParfaitAnalyticsViewerTimeZone";
    const viewerTimezoneOffsetCookieName = "ParfaitAnalyticsViewerOffsetMinutes";

    function getBrowserTimezone() {
        return {
            id: Intl?.DateTimeFormat?.().resolvedOptions?.().timeZone || "",
            offsetMinutes: new Date().getTimezoneOffset()
        };
    }

    function persistBrowserTimezone() {
        const viewerTimezone = getBrowserTimezone();
        const cookieAttributes = "path=/; max-age=31536000; samesite=lax";

        if (viewerTimezone.id) {
            document.cookie = `${viewerTimezoneCookieName}=${encodeURIComponent(viewerTimezone.id)}; ${cookieAttributes}`;
        }

        document.cookie = `${viewerTimezoneOffsetCookieName}=${encodeURIComponent(String(viewerTimezone.offsetMinutes))}; ${cookieAttributes}`;

        try {
            const url = new URL(window.location.href);
            if (viewerTimezone.id) {
                url.searchParams.set("timezoneId", viewerTimezone.id);
            } else {
                url.searchParams.delete("timezoneId");
            }

            url.searchParams.set("timezoneOffsetMinutes", String(viewerTimezone.offsetMinutes));
            window.history.replaceState({}, "", url.toString());
        } catch {
        }

        if (viewerTimezone.id) {
            pageRoot.dataset.timezoneId = viewerTimezone.id;
        }

        pageRoot.dataset.timezoneOffsetMinutes = String(viewerTimezone.offsetMinutes);
        return viewerTimezone;
    }

    const viewerTimezone = persistBrowserTimezone();

    const endpoints = {
        metaConnect: "/internal/analytics/meta-connect",
        metaConnectionStatus: "/internal/analytics/meta-connection-status",
        metaCampaigns: "/internal/analytics/meta-campaigns",
        metaDisconnect: "/internal/analytics/meta-disconnect",
        healthMonitor: "/internal/analytics/health-monitor"
    };

    const connectBtn = document.getElementById("meta-connect-btn");
    const disconnectBtn = document.getElementById("meta-disconnect-btn");
    const statusEl = document.getElementById("meta-connection-status");
    const campaignsBtn = document.getElementById("meta-campaigns-open");
    const campaignsModal = document.getElementById("pfMetaCampaignsModal");
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
                return "all_traffic";
            default:
                return "real_human_traffic";
        }
    }

    function currentAnalyticsParams() {
        const url = new URL(window.location.href);
        return {
            preset: url.searchParams.get("preset") || pageRoot.dataset.preset || "30d",
            fromUtc: url.searchParams.get("fromUtc") || pageRoot.dataset.fromUtc || "",
            toUtc: url.searchParams.get("toUtc") || pageRoot.dataset.toUtc || "",
            qualityMode: normalizeQualityMode(url.searchParams.get("qualityMode") || pageRoot.dataset.qualityMode || "real_human_traffic"),
            timezoneId: url.searchParams.get("timezoneId") || pageRoot.dataset.timezoneId || viewerTimezone.id || "",
            timezoneOffsetMinutes: url.searchParams.get("timezoneOffsetMinutes") || pageRoot.dataset.timezoneOffsetMinutes || String(viewerTimezone.offsetMinutes)
        };
    }

    function setMetaCampaignsEnabled(enabled) {
        if (!campaignsBtn) {
            return;
        }

        campaignsBtn.disabled = !enabled;
        campaignsBtn.setAttribute("aria-disabled", enabled ? "false" : "true");
        campaignsBtn.title = enabled ? "View Meta campaigns" : "Connect Meta Ads to view campaigns";
    }

    function updateMetaConnectHref() {
        if (!connectBtn) {
            return;
        }

        const params = {
            returnUrl: `${window.location.pathname}${window.location.search}`
        };

        connectBtn.href = buildUrlWithParams(endpoints.metaConnect, params);
    }

    function setMetaConnectState(enabled, label, title = "") {
        if (!connectBtn) {
            return;
        }

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

    function setMetaAccountChip(text, connected = true) {
        if (!accountChip) {
            return;
        }

        accountChip.classList.remove("d-none");
        accountChip.textContent = text || (connected ? "Connected" : "Not connected");
        accountChip.style.opacity = connected ? "1" : ".75";
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
        campaignsBody.innerHTML = `<tr><td colspan="13" class="${className}">${escapeHtml(message)}</td></tr>`;
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

    function renderMetaCampaigns(data) {
        setText(rangeValue, data?.rangeLabel || pageRoot.dataset.preset || "30d");
        setMetaCampaignFields(data, true);

        if (accountChip) {
            accountChip.classList.remove("d-none");
            accountChip.textContent = data?.accountName || data?.accountId || "Connected";
            accountChip.style.opacity = "1";
        }

        if (noteValue) {
            noteValue.textContent = "Purchases and revenue are Parfait ecommerce outcomes. ROAS is ecommerce revenue divided by Meta spend.";
        }

        if (!campaignsBody) {
            return;
        }

        const rows = Array.isArray(data?.rows) ? data.rows : [];
        if (!rows.length) {
            setMetaCampaignsBodyMessage("No Meta campaign rows were returned for the selected analytics range.");
            return;
        }

        campaignsBody.innerHTML = rows.map(row => `
            <tr>
                <td>${pill(row.campaignName || row.name || "—", `${metaCampaignNameClass(row)} meta-campaign-name-pill`)}<div class="fa-muted small mt-1">${escapeHtml(row.campaignId || "")}</div></td>
                <td>${pill(row.status || "—", metaStatusClass(row.status))}</td>
                <td>${pill(row.objective || "—", metaObjectiveClass(row.objective))}</td>
                <td class="text-end">${pill(formatMoney(row.spend), metaSpendClass(row))}</td>
                <td class="text-end">${pill(formatInt(row.impressions), metaImpressionsClass(row.impressions))}</td>
                <td class="text-end">${pill(formatInt(row.reach), metaReachClass(row.reach))}</td>
                <td class="text-end">${pill(formatInt(row.clicks), metaClicksClass(row.clicks))}</td>
                <td class="text-end">${pill(formatPercent(row.ctr), metaCtrClass(row.ctr))}</td>
                <td class="text-end">${pill(formatMoney(row.cpc), metaCpcClass(row.cpc))}</td>
                <td class="text-end">${pill(formatMoney(row.cpm), metaCpmClass(row.cpm))}</td>
                <td class="text-end">${pill(formatInt(row.policiesPaid || row.purchases || row.purchaseCount || 0), (row.policiesPaid || row.purchases || row.purchaseCount) > 0 ? "meta-good" : "meta-neutral")}</td>
                <td class="text-end">${pill(formatMoney(row.paidPremium || row.revenue || row.purchaseValue || 0), (row.paidPremium || row.revenue || row.purchaseValue) > 0 ? "meta-good" : "meta-neutral")}</td>
                <td class="text-end">${pill(`${toNumber(row.premiumRoas || row.roas || 0).toFixed(2)}x`, toNumber(row.premiumRoas || row.roas || 0) > 0 ? "meta-good" : "meta-neutral")}</td>
            </tr>`).join("");
    }


    function setMetaCampaignFields(data, preserveExisting = false) {
        const account = data?.accountName || data?.accountId;
        const business = data?.businessName || data?.businessId;
        const syncStamp = data?.syncedUtc || data?.connectedUtc;

        setText(accountValue, account || (preserveExisting ? (accountValue?.textContent || "Not connected") : "Not connected"));
        setText(businessValue, business || (preserveExisting ? (businessValue?.textContent || "—") : "—"));
        setText(syncValue, syncStamp ? formatShortDate(syncStamp) : (preserveExisting ? (syncValue?.textContent || "Not synced yet") : "Not synced yet"));
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

    function setHealthText(id, value) {
        const element = document.getElementById(id);
        if (element) {
            element.textContent = value;
        }
    }

    function healthPercent(value) {
        return `${Number(value || 0).toFixed(1)}%`;
    }

    async function loadHealthMonitor() {
        setHealthText("pf-health-status", "Loading ecommerce health snapshot…");

        try {
            const data = await fetchJson(buildUrlWithParams(endpoints.healthMonitor, currentAnalyticsParams()));

            setHealthText("pf-health-range-label", data?.rangeLabel || "Selected range");
            setHealthText("pf-health-status", data?.summary || "Parfait ecommerce health snapshot loaded.");

            const focusGrid = document.getElementById("pf-health-focus-grid");
            const focus = Array.isArray(data?.focusMetrics) ? data.focusMetrics : [];
            if (focusGrid) {
                focusGrid.innerHTML = focus.map(metric => `
                    <div class="col"><div class="kpi-card">
                        <div class="fa-kpi-title">${escapeHtml(metric.label || "Metric")}</div>
                        <div class="fa-kpi-value">${formatInt(metric.currentValue)}</div>
                        <div class="fa-kpi-sub">${healthPercent(metric.deltaPercent)} conversion context</div>
                    </div></div>`).join("");
            }

            const attribution = data?.attributionHealth || {};
            setHealthText("pf-health-match-rate", healthPercent(attribution.serverBrowserMatchRate));
            setHealthText("pf-health-match-sub", `${formatInt(attribution.matchedEvents)} matched / ${formatInt(attribution.eligibleEvents)} eligible`);
            setHealthText("pf-health-missing-rate", healthPercent(attribution.missingAttributionRate));
            setHealthText("pf-health-missing-sub", `${formatInt(attribution.missingAttributionEvents)} issues / ${formatInt(attribution.eligibleEvents)} eligible`);

            const reconciliation = data?.reconciliation || {};
            setHealthText("pf-health-reconciliation", `${formatInt(reconciliation.paidOrders)} paid / ${formatInt(reconciliation.purchaseEvents)} purchase events`);
            setHealthText("pf-health-reconciliation-sub", `${formatInt(reconciliation.unmatchedPaidOrders)} paid orders without matching purchase event`);

            const funnelBody = document.getElementById("pf-health-funnel-body");
            const funnel = Array.isArray(data?.funnel) ? data.funnel : [];
            if (funnelBody) {
                funnelBody.innerHTML = funnel.map(row => `
                    <tr>
                        <td><strong>${escapeHtml(row.label || "—")}</strong></td>
                        <td class="text-end">${formatInt(row.sessions)}</td>
                        <td class="text-end">${healthPercent(row.conversionRate)}</td>
                    </tr>`).join("") || '<tr><td colspan="3" class="fa-empty">No ecommerce funnel activity exists for this range.</td></tr>';
            }

            const eventsBody = document.getElementById("pf-health-events-body");
            const events = Array.isArray(data?.recentEvents) ? data.recentEvents : [];
            if (eventsBody) {
                eventsBody.innerHTML = events.map(row => `
                    <tr>
                        <td>${formatShortDate(row.createdUtc)}</td>
                        <td>${escapeHtml(row.severity || "Info")}</td>
                        <td><strong>${escapeHtml(row.eventName || "—")}</strong></td>
                        <td>${escapeHtml(row.summary || "—")}</td>
                    </tr>`).join("") || '<tr><td colspan="4" class="fa-empty">No recent health events exist for this range.</td></tr>';
            }
        } catch (error) {
            setHealthText("pf-health-status", error?.message || "Unable to load ecommerce health.");
            console.error(error);
        }
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

    disconnectBtn?.addEventListener("click", () => {
        void handleMetaDisconnect();
    });

    document.getElementById("pfMetaHealthModal")?.addEventListener("show.bs.modal", () => {
        void loadHealthMonitor();
    });

    campaignsModal?.addEventListener("show.bs.modal", () => {
        if (campaignsBtn?.getAttribute("aria-disabled") === "true") {
            setMetaCampaignsBodyMessage("Connect Meta Ads to load campaign performance.", "warning");
            return;
        }

        void loadMetaCampaigns();
    });

    pageRoot.addEventListener("keydown", event => {
        if (event.defaultPrevented || (event.key !== "Enter" && event.key !== " ")) {
            return;
        }

        const trigger = event.target instanceof Element
            ? event.target.closest('[role="button"][data-bs-toggle="modal"]')
            : null;

        if (!trigger || !pageRoot.contains(trigger)) {
            return;
        }

        event.preventDefault();
        trigger.click();
    });

    const metaCallbackState = readMetaCallbackState();
    void loadMetaConnectionStatus().finally(() => {
        applyMetaCallbackState(metaCallbackState);
    });

})();
