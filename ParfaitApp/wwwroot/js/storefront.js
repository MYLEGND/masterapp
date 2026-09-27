window.ParfaitStorefront = (() => {
    const context = window.PARFAIT_COMMERCE_CONTEXT || {};
    const storageKey = String(context.cartStorageKey || "parfaitCart");
    const discountStorageKey = storageKey === "parfaitCart"
        ? "parfaitDiscountCode"
        : storageKey + ":discount";

    const readJson = key => {
        try {
            const raw = localStorage.getItem(key);
            return raw ? JSON.parse(raw) : null;
        } catch {
            return null;
        }
    };

    const emitCartUpdated = () => {
        window.dispatchEvent(new Event("parfait-cart-updated"));
        window.dispatchEvent(new Event("legend-commerce-cart-updated"));
    };

    const writeCart = cart => {
        localStorage.setItem(storageKey, JSON.stringify(cart));
        emitCartUpdated();
        return cart;
    };

    const readCart = () => {
        const parsed = readJson(storageKey);
        return Array.isArray(parsed) ? parsed : [];
    };

    const clearCart = () => {
        localStorage.removeItem(storageKey);
        emitCartUpdated();
    };

    const money = cents => `$${(Number(cents || 0) / 100).toFixed(2)}`;

    const itemCount = cart => cart.reduce((sum, item) => sum + Number(item.quantity || 0), 0);

    const subtotal = cart => cart.reduce((sum, item) => sum + ((Number(item.priceCents) || 0) * (Number(item.quantity) || 0)), 0);

    const normalizeDiscountCode = value => (value || "")
        .trim()
        .toUpperCase()
        .replace(/[^A-Z0-9]+/g, "-")
        .replace(/^-+|-+$/g, "");

    const readDiscountCode = () => localStorage.getItem(discountStorageKey) || "";

    const writeDiscountCode = value => {
        const normalized = normalizeDiscountCode(value);
        if (!normalized) {
            localStorage.removeItem(discountStorageKey);
        } else {
            localStorage.setItem(discountStorageKey, normalized);
        }

        emitCartUpdated();
        return normalized;
    };

    const clearDiscountCode = () => {
        localStorage.removeItem(discountStorageKey);
        emitCartUpdated();
    };

    const addItem = async item => {
        const eventId = crypto.randomUUID();
        const response = await fetch((context.storeRootPath || '/store') + '/cart/items', {
            method:'POST', headers:{'Content-Type':'application/json'},
            body:JSON.stringify({eventId,productId:item.id,size:item.size,quantity:item.quantity||1,
                sessionId:window.LegendAnalytics?.ids?.getSessionId?.(), visitorId:window.LegendAnalytics?.ids?.getVisitorId?.(),
                url:location.href,referrer:document.referrer})
        });
        if (!response.ok) throw new Error('Unable to add this item to your cart.');
        const accepted = await response.json();
        item = {...item,priceCents:accepted.priceCents};
        const cart = readCart();
        const quantity = accepted.quantity;
        const existing = cart.find(entry => entry.key === item.key);

        if (existing) {
            existing.quantity = quantity;
        } else {
            cart.push({
                key: item.key,
                id: item.id,
                name: item.name,
                slug: item.slug,
                priceCents: Number(item.priceCents || 0),
                compareAtPriceCents: Number(item.compareAtPriceCents || 0),
                priceLabel: item.priceLabel,
                imageUrl: item.imageUrl,
                badge: item.badge || context.storeName || "",
                size: item.size,
                quantity
            });
        }

        return writeCart(cart);
    };

    const updateQuantity = (key, quantity) => {
        const cart = readCart();
        const nextQuantity = Number(quantity || 0);

        if (nextQuantity <= 0) {
            return removeItem(key);
        }

        const match = cart.find(item => item.key === key);
        if (!match) {
            return cart;
        }

        match.quantity = nextQuantity;
        return writeCart(cart);
    };

    const removeItem = key => {
        const cart = readCart().filter(item => item.key !== key);
        return writeCart(cart);
    };

    return {
        context,
        storageKey,
        discountStorageKey,
        readCart,
        writeCart,
        clearCart,
        emitCartUpdated,
        money,
        itemCount,
        subtotal,
        normalizeDiscountCode,
        readDiscountCode,
        writeDiscountCode,
        clearDiscountCode,
        addItem,
        updateQuantity,
        removeItem
    };
})();
