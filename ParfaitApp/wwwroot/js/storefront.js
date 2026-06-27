window.ParfaitStorefront = (() => {
    const storageKey = "parfaitCart";

    const readCart = () => {
        try {
            const raw = localStorage.getItem(storageKey);
            const parsed = raw ? JSON.parse(raw) : [];
            return Array.isArray(parsed) ? parsed : [];
        } catch {
            return [];
        }
    };

    const emitCartUpdated = () => {
        window.dispatchEvent(new Event("parfait-cart-updated"));
    };

    const writeCart = cart => {
        localStorage.setItem(storageKey, JSON.stringify(cart));
        emitCartUpdated();
        return cart;
    };

    const money = cents => `$${(Number(cents || 0) / 100).toFixed(2)}`;

    const itemCount = cart => cart.reduce((sum, item) => sum + Number(item.quantity || 0), 0);

    const subtotal = cart => cart.reduce((sum, item) => sum + ((Number(item.priceCents) || 0) * (Number(item.quantity) || 0)), 0);

    const addItem = item => {
        const cart = readCart();
        const quantity = Math.max(1, Number(item.quantity || 1));
        const existing = cart.find(entry => entry.key === item.key);

        if (existing) {
            existing.quantity = Math.max(1, Number(existing.quantity || 0) + quantity);
        } else {
            cart.push({
                key: item.key,
                id: item.id,
                name: item.name,
                slug: item.slug,
                priceCents: Number(item.priceCents || 0),
                priceLabel: item.priceLabel,
                imageUrl: item.imageUrl,
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
        storageKey,
        readCart,
        writeCart,
        emitCartUpdated,
        money,
        itemCount,
        subtotal,
        addItem,
        updateQuantity,
        removeItem
    };
})();
