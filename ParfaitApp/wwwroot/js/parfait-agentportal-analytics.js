(() => {
    const endpoints = {
        metaConnect: "/internal/analytics/meta-connect",
        metaConnectionStatus: "/internal/analytics/meta-connection-status",
        metaDisconnect: "/internal/analytics/meta-disconnect"
    };

    const connectBtn = document.getElementById("meta-connect-btn");
    const disconnectBtn = document.getElementById("meta-disconnect-btn");
    const statusEl = document.getElementById("meta-connection-status");
    const campaignsBtn = document.getElementById("meta-campaigns-open");
    const disconnectForm = document.getElementById("meta-disconnect-form");
    const openAdsLink = document.getElementById("openMetaAdsManagerLink");
    const accountChip = document.getElementById("meta-campaigns-account-chip");
    const accountValue = document.getElementById("pf-meta-campaign-account-value");
    const businessValue = document.getElementById("pf-meta-campaign-business-value");
    const syncValue = document.getElementById("pf-meta-campaign-sync-value");
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
        if (accountValue) {
            accountValue.textContent = data?.accountName || data?.accountId || "Not connected";
        }

        if (businessValue) {
            businessValue.textContent = data?.businessName || data?.businessId || "—";
        }

        if (syncValue) {
            syncValue.textContent = formatShortDate(data?.connectedUtc);
        }
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

    function showMetaCallbackBanner() {
        let url;
        try {
            url = new URL(window.location.href);
        } catch {
            return;
        }

        const meta = url.searchParams.get("meta");
        if (!meta) {
            return;
        }

        if (meta === "connected") {
            statusEl.className = `${statusBaseClass} text-success`;
            statusEl.textContent = "Meta Ads connected successfully.";
        } else if (meta === "error") {
            const message = url.searchParams.get("message") || "Meta Ads connection failed.";
            statusEl.className = `${statusBaseClass} text-danger`;
            statusEl.textContent = message;
        }

        url.searchParams.delete("meta");
        url.searchParams.delete("message");
        window.history.replaceState({}, "", url.toString());
    }

    connectBtn.addEventListener("click", event => {
        if (connectBtn.getAttribute("aria-disabled") === "true") {
            event.preventDefault();
        }
    });

    disconnectBtn?.addEventListener("click", () => {
        void handleMetaDisconnect();
    });

    showMetaCallbackBanner();
    void loadMetaConnectionStatus();
})();
