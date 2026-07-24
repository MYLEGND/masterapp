(function () {
    "use strict";

    const modalElement = document.getElementById("pricingGroupModal");
    const modalTitle = document.getElementById("pricingGroupModalTitle");
    const modalBody = document.getElementById("pricingGroupModalBody");
    const actionDialog = document.getElementById("subscriberActionModal");
    const actionDialogTitle = document.getElementById("subscriberActionModalTitle");
    const actionDialogBody = document.getElementById("subscriberActionModalBody");
    const actionToken = document.querySelector("#founderSubscriberActionToken input[name='__RequestVerificationToken']");
    let selectedDetail = null;
    const subscriberActions = [
        { label: "Open client", destination: "client" },
        { label: "Open CRM", destination: "crm" },
        { label: "Open timeline", destination: "timeline" }
    ];

    function formatLocalTimes(root) {
        (root || document).querySelectorAll("time[data-local-utc]").forEach((element) => {
            const value = element.dataset.localUtc;
            const date = value ? new Date(value) : null;
            if (!date || Number.isNaN(date.getTime())) return;

            element.textContent = new Intl.DateTimeFormat(undefined, {
                dateStyle: "medium",
                timeStyle: "short"
            }).format(date);
            element.title = date.toLocaleString();
        });
    }

    function showLoadError() {
        modalBody.replaceChildren();
        const message = document.createElement("p");
        message.className = "founder-subscriber-empty";
        message.textContent = "Subscriber details could not be loaded. Refresh and try again.";
        modalBody.append(message);
    }

    async function loadDetail(page) {
        if (!selectedDetail || !modalBody) return;

        let url;
        if (selectedDetail.kind === "cancelled") {
            url = `/founder/subscribers/cancelled?page=${String(page || 1)}`;
        } else {
            const params = new URLSearchParams(window.location.search);
            params.delete("GroupPage");
            params.set("monthlyAmountCents", selectedDetail.amountCents);
            params.set("currency", selectedDetail.currency);
            params.set("page", String(page || 1));
            url = `/founder/subscribers/pricing-group?${params.toString()}`;
        }

        modalBody.setAttribute("aria-busy", "true");
        modalBody.replaceChildren();
        const loading = document.createElement("p");
        loading.className = "founder-subscriber-empty";
        loading.textContent = "Loading subscribers…";
        modalBody.append(loading);

        try {
            const response = await fetch(url, {
                credentials: "same-origin",
                headers: { "X-Requested-With": "XMLHttpRequest" }
            });

            if (!response.ok) throw new Error(`Subscriber group request failed: ${response.status}`);
            modalBody.innerHTML = await response.text();
            formatLocalTimes(modalBody);
        } catch (error) {
            console.error(error);
            showLoadError();
        } finally {
            modalBody.removeAttribute("aria-busy");
        }
    }

    function showModal(title, isSummary) {
        if (!modalElement || !window.bootstrap) return false;
        modalElement.classList.toggle("is-summary", isSummary);
        modalTitle.textContent = title || "Subscribers";
        window.bootstrap.Modal.getOrCreateInstance(modalElement).show();
        return true;
    }

    function openGroup(button) {
        selectedDetail = {
            kind: "pricing-group",
            amountCents: button.dataset.amountCents || "",
            currency: button.dataset.currency || "USD"
        };

        if (!selectedDetail.amountCents || !showModal(button.dataset.groupLabel, false)) return;
        void loadDetail(1);
    }

    function openCancelled(button) {
        selectedDetail = { kind: "cancelled" };
        if (!showModal(button.dataset.modalTitle, false)) return;
        void loadDetail(1);
    }

    function openSummary(button) {
        const template = document.getElementById(button.dataset.summaryTemplate || "");
        if (!(template instanceof HTMLTemplateElement) || !modalBody || !showModal(button.dataset.modalTitle, true)) return;

        selectedDetail = null;
        modalBody.replaceChildren(template.content.cloneNode(true));
        formatLocalTimes(modalBody);
    }

    function appendHiddenInput(form, name, value) {
        const input = document.createElement("input");
        input.type = "hidden";
        input.name = name;
        input.value = value;
        form.append(input);
    }

    function openSubscriberActions(button) {
        if (!actionDialog || !actionDialogBody || !actionDialogTitle || !actionToken) return;

        const clientProfileId = button.dataset.clientProfileId || "";
        const agentUserId = button.dataset.agentUserId || "";
        if (!clientProfileId || !agentUserId) return;

        actionDialogTitle.textContent = `${button.dataset.customer || "Subscriber"} actions`;
        actionDialogBody.replaceChildren();

        subscriberActions.forEach((action) => {
            const form = document.createElement("form");
            form.method = "post";
            form.action = "/founder/subscribers/open-client-context";
            form.target = "_blank";
            appendHiddenInput(form, "__RequestVerificationToken", actionToken.value);
            appendHiddenInput(form, "clientProfileId", clientProfileId);
            appendHiddenInput(form, "agentUserId", agentUserId);
            appendHiddenInput(form, "destination", action.destination);

            const choice = document.createElement("button");
            choice.type = "submit";
            choice.className = "founder-subscriber-action-choice";
            choice.textContent = action.label;
            form.append(choice);
            form.addEventListener("submit", closeSubscriberActions);
            actionDialogBody.append(form);
        });

        actionDialog.hidden = false;
    }

    function closeSubscriberActions() {
        if (actionDialog) actionDialog.hidden = true;
    }

    document.addEventListener("click", (event) => {
        const groupButton = event.target.closest("[data-open-pricing-group]");
        if (groupButton) {
            openGroup(groupButton);
            return;
        }

        const cancelledButton = event.target.closest("[data-open-cancelled-subscribers]");
        if (cancelledButton) {
            openCancelled(cancelledButton);
            return;
        }

        const summaryButton = event.target.closest("[data-open-subscriber-summary]");
        if (summaryButton) {
            openSummary(summaryButton);
            return;
        }

        const actionButton = event.target.closest("[data-open-subscriber-actions]");
        if (actionButton) {
            openSubscriberActions(actionButton);
            return;
        }

        if (event.target.closest("[data-close-subscriber-actions]")) {
            closeSubscriberActions();
            return;
        }

        const pageButton = event.target.closest("[data-pricing-group-page]");
        if (pageButton && !pageButton.disabled) {
            void loadDetail(Number(pageButton.dataset.pricingGroupPage));
        }
    });

    if (actionDialog) {
        actionDialog.addEventListener("click", (event) => {
            if (event.target === actionDialog) closeSubscriberActions();
        });
    }

    formatLocalTimes(document);
})();
