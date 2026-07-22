(() => {
    "use strict";

    function text(value) {
        return value == null ? "" : String(value).trim();
    }

    function parseUtc(value) {
        if (typeof window.crmParseUtcDate === "function") {
            return window.crmParseUtcDate(value);
        }

        const raw = text(value);
        if (!raw) return null;

        const parsed = new Date(raw);
        return Number.isNaN(parsed.getTime()) ? null : parsed;
    }

    function formatLocalDateTime(value) {
        const parsed = parseUtc(value);
        if (!parsed) return "—";

        return parsed.toLocaleString([], {
            dateStyle: "medium",
            timeStyle: "short"
        });
    }

    function formatMoney(amountCents, currency) {
        if (!Number.isFinite(amountCents)) return "—";

        try {
            return new Intl.NumberFormat(undefined, {
                style: "currency",
                currency: text(currency) || "USD"
            }).format(amountCents / 100);
        } catch {
            return `$${(amountCents / 100).toFixed(2)}`;
        }
    }

    function describeBillingAnchor(offer) {
        const mode = text(offer?.billingAnchorSelectionMode);
        const day = offer?.selectedBillingAnchorDay;

        switch (mode) {
            case "FirstOfMonth":
                return "1st of month";
            case "FifteenthOfMonth":
                return "15th of month";
            case "SpecificDayOfMonth":
                return Number.isFinite(day) ? `Day ${day} of month` : "Agent-selected day";
            default:
                return "Scheduled monthly";
        }
    }

    function getNode(id) {
        return document.getElementById(id);
    }

    function notify(message) {
        const adapter = window.quickViewCalendarAdapter;
        if (adapter && typeof adapter.toast === "function") {
            adapter.toast(message);
            return;
        }

        window.alert(message);
    }

    function getAntiForgeryToken() {
        return document.querySelector("#__af input[name='__RequestVerificationToken']")?.value || "";
    }

    async function postJson(url, payload) {
        const response = await fetch(url, {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "RequestVerificationToken": getAntiForgeryToken()
            },
            body: JSON.stringify(payload || {})
        });

        let data = null;
        try {
            data = await response.json();
        } catch {
            data = null;
        }

        if (!response.ok) {
            const message = text(data?.message) || text(data) || `Request failed with HTTP ${response.status}.`;
            throw new Error(message);
        }

        return data || {};
    }

    function getLoadedState() {
        if (typeof window.isQuickViewAppointmentLoaded === "function") {
            try {
                return !!window.isQuickViewAppointmentLoaded();
            } catch {
                return false;
            }
        }

        return false;
    }

    function getBillingContext() {
        if (typeof window.getQuickViewBillingContext === "function") {
            try {
                return window.getQuickViewBillingContext();
            } catch {
                return null;
            }
        }

        return null;
    }

    function isPortalRecord(context) {
        const recordType = text(context?.recordType).replace(/[\s-]/g, "").toLowerCase();
        return recordType === "client" || recordType === "businessclient";
    }

    function canSetFounderSubscriptionOptions() {
        return !!window.quickViewBillingOptions?.canSetFounderSubscriptionOptions;
    }

    function setSubscriptionSetupVisible(visible) {
        const setup = getNode("dBillingSubscriptionSetup");
        if (setup) setup.hidden = !visible;
    }

    function syncSubscriptionSetupControls() {
        const priceType = getNode("dBillingSubscriptionPriceType");
        const customAmountWrap = getNode("dBillingSubscriptionCustomAmountWrap");
        const customAmount = getNode("dBillingSubscriptionCustomAmount");
        const anchor = getNode("dBillingSubscriptionAnchor");
        const founderAnchorOption = anchor?.querySelector("[data-founder-only]");
        const anchorDayWrap = getNode("dBillingSubscriptionAnchorDayWrap");

        const founderOptions = canSetFounderSubscriptionOptions();
        if (founderAnchorOption) founderAnchorOption.hidden = !founderOptions;
        if (!founderOptions && anchor?.value === "SpecificDayOfMonth") {
            anchor.value = "FirstOfMonth";
        }

        const isCustom = priceType?.value === "Custom";
        if (customAmountWrap) customAmountWrap.hidden = !isCustom;
        if (customAmount) {
            customAmount.min = founderOptions ? "0" : "50";
            customAmount.placeholder = founderOptions ? "0.00" : "50.00";
        }

        if (anchorDayWrap) {
            anchorDayWrap.hidden = anchor?.value !== "SpecificDayOfMonth";
        }
    }

    function updateButtonState(snapshot, loaded) {
        const configureButton = getNode("btnBillingConfigureSubscription");
        const resendButton = getNode("btnBillingResendInvite");
        const revokeButton = getNode("btnBillingRevokeInvite");
        const cancelButton = getNode("btnBillingCancelPeriodEnd");

        const actions = snapshot?.actions || {};
        const context = getBillingContext();
        if (configureButton) {
            configureButton.disabled = !loaded ||
                !context?.clientProfileId ||
                !isPortalRecord(context) ||
                !actions.canConfigureSubscription;
        }
        if (resendButton) resendButton.disabled = !loaded || !actions.canResendInvitation;
        if (revokeButton) revokeButton.disabled = !loaded || !actions.canRevokeInvitation;
        if (cancelButton) cancelButton.disabled = !loaded || !actions.canCancelSubscription;
    }

    function renderQuickViewBillingSnapshot(snapshot, options = {}) {
        const loaded = Object.prototype.hasOwnProperty.call(options, "loaded")
            ? !!options.loaded
            : getLoadedState();

        const section = getNode("dBillingSection");
        const offerNode = getNode("dBillingOffer");
        const invitationNode = getNode("dBillingInvitation");
        const subscriptionNode = getNode("dBillingSubscription");
        const entitlementNode = getNode("dBillingEntitlement");
        const nextBillingNode = getNode("dBillingNextBilling");
        const deliveryNode = getNode("dBillingDelivery");
        const statusNode = getNode("dBillingStatus");

        const context = getBillingContext();
        if (section) section.hidden = !loaded || (!snapshot && !isPortalRecord(context));
        setSubscriptionSetupVisible(false);

        if (!loaded || !snapshot) {
            [offerNode, invitationNode, subscriptionNode, entitlementNode, nextBillingNode, deliveryNode].forEach(node => {
                if (node) node.textContent = "—";
            });
            if (statusNode) statusNode.textContent = loaded ? "No ClientApp billing workspace on this record yet." : "Ready";
            updateButtonState({ actions: { canConfigureSubscription: isPortalRecord(context) } }, loaded);
            return;
        }

        if (offerNode) {
            const offer = snapshot.offer;
            offerNode.textContent = offer
                ? `${text(offer.status) || "Unknown"} • ${formatMoney(offer.monthlyAmountCents, offer.currency)} • ${describeBillingAnchor(offer)}`
                : "No subscription configured";
        }

        if (invitationNode) {
            const invitation = snapshot.invitation;
            invitationNode.textContent = invitation
                ? `${text(invitation.status) || "Unknown"} • ${text(invitation.intendedEmail) || "No email"}`
                : "No invitation prepared";
        }

        if (subscriptionNode) {
            const subscription = snapshot.subscription;
            subscriptionNode.textContent = subscription
                ? `${text(subscription.status) || "Unknown"} • ${text(subscription.paymentStanding) || "Unknown payment standing"}`
                : "No active subscription";
        }

        if (entitlementNode) {
            const entitlement = snapshot.entitlement;
            entitlementNode.textContent = entitlement
                ? `${text(entitlement.status) || "Unknown"}${text(entitlement.reasonCode) ? ` • ${text(entitlement.reasonCode)}` : ""}`
                : "Not granted";
        }

        if (nextBillingNode) {
            nextBillingNode.textContent =
                formatLocalDateTime(snapshot.subscription?.nextBillingDateUtc);
        }

        if (deliveryNode) {
            const invitation = snapshot.invitation;
            if (!invitation) {
                deliveryNode.textContent = "No invitation delivery recorded";
            } else {
                const action = text(invitation.lastDeliveryAction);
                const summary = text(invitation.lastDeliverySummary);
                const sentAt = formatLocalDateTime(invitation.lastSentUtc || invitation.lastDeliveryUtc);
                if (action === "send_failed") {
                    deliveryNode.textContent = summary
                        ? `Send failed • ${summary}`
                        : "Latest send attempt failed";
                } else if (action === "sent" || invitation.sendCount > 0) {
                    deliveryNode.textContent = `${invitation.sendCount || 0} sent • ${sentAt}`;
                } else {
                    deliveryNode.textContent = "Invitation created but not sent yet";
                }
            }
        }

        if (statusNode) {
            statusNode.textContent = "Billing workspace ready.";
        }

        updateButtonState(snapshot, loaded);
    }

    async function runAction(actionKey, successMessage) {
        const context = getBillingContext();
        if (!context?.clientProfileId) {
            notify("Open a linked client record first.");
            return;
        }

        const url = text(context?.actionUrls?.[actionKey]);
        if (!url) {
            notify("Billing actions are not available for this record.");
            return;
        }

        try {
            const data = await postJson(url, {
                clientProfileId: context.clientProfileId
            });

            if (typeof window.setQuickViewBillingSnapshot === "function") {
                window.setQuickViewBillingSnapshot(data.billing || null);
            }

            renderQuickViewBillingSnapshot(data.billing || null, {
                loaded: getLoadedState()
            });

            const recipient = text(data.recipient);
            notify(recipient ? `${successMessage} to ${recipient}` : successMessage);
        } catch (error) {
            console.error(error);
            notify(error?.message || "Billing action failed.");
        }
    }

    async function configureSubscription() {
        const context = getBillingContext();
        const url = text(context?.actionUrls?.configureSubscription);
        if (!context?.clientProfileId || !url || !isPortalRecord(context)) {
            notify("Convert this lead through the shared client account form before setting a subscription.");
            return;
        }

        const priceType = text(getNode("dBillingSubscriptionPriceType")?.value);
        const customAmountRaw = text(getNode("dBillingSubscriptionCustomAmount")?.value);
        const anchorMode = text(getNode("dBillingSubscriptionAnchor")?.value);
        const anchorDayRaw = text(getNode("dBillingSubscriptionAnchorDay")?.value);
        const customAmount = priceType === "Custom" && customAmountRaw !== ""
            ? Number(customAmountRaw)
            : null;
        const anchorDay = anchorMode === "SpecificDayOfMonth" && anchorDayRaw !== ""
            ? Number(anchorDayRaw)
            : null;

        if (!priceType || !anchorMode ||
            (priceType === "Custom" && !Number.isFinite(customAmount)) ||
            (anchorMode === "SpecificDayOfMonth" && !Number.isInteger(anchorDay))) {
            notify("Complete the subscription amount and billing anchor.");
            return;
        }

        const saveButton = getNode("btnBillingSaveSubscription");
        if (saveButton) saveButton.disabled = true;

        try {
            const data = await postJson(url, {
                clientProfileId: context.clientProfileId,
                subscriptionPriceType: priceType,
                subscriptionCustomMonthlyAmount: customAmount,
                subscriptionBillingAnchorMode: anchorMode,
                subscriptionBillingAnchorDay: anchorDay
            });

            if (typeof window.setQuickViewBillingSnapshot === "function") {
                window.setQuickViewBillingSnapshot(data.billing || null);
            }

            setSubscriptionSetupVisible(false);
            renderQuickViewBillingSnapshot(data.billing || null, {
                loaded: getLoadedState()
            });
            const recipient = text(data.recipient);
            notify(recipient
                ? `Subscription invitation sent to ${recipient}`
                : "Subscription invitation sent.");
        } catch (error) {
            console.error(error);
            notify(error?.message || "Subscription setup failed.");
        } finally {
            if (saveButton) saveButton.disabled = false;
        }
    }

    document.addEventListener("click", event => {
        if (event.target?.closest?.("#btnBillingConfigureSubscription")) {
            event.preventDefault();
            syncSubscriptionSetupControls();
            setSubscriptionSetupVisible(true);
            return;
        }

        if (event.target?.closest?.("#btnBillingCancelSubscriptionSetup")) {
            event.preventDefault();
            setSubscriptionSetupVisible(false);
            return;
        }

        if (event.target?.closest?.("#btnBillingSaveSubscription")) {
            event.preventDefault();
            void configureSubscription();
            return;
        }

        if (event.target?.closest?.("#btnBillingResendInvite")) {
            event.preventDefault();
            void runAction("resendInvitation", "Subscription invitation resent");
            return;
        }

        if (event.target?.closest?.("#btnBillingRevokeInvite")) {
            event.preventDefault();
            void runAction("revokeInvitation", "Subscription invitation revoked");
            return;
        }

        if (event.target?.closest?.("#btnBillingCancelPeriodEnd")) {
            event.preventDefault();
            void runAction("cancelSubscription", "Subscription will cancel at period end");
        }
    });

    document.addEventListener("change", event => {
        if (event.target?.matches?.("#dBillingSubscriptionPriceType, #dBillingSubscriptionAnchor")) {
            syncSubscriptionSetupControls();
        }
    });

    window.renderQuickViewBillingSnapshot = renderQuickViewBillingSnapshot;
})();
