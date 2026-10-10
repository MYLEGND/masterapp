# Parfait public-route baseline (read-only)

Capture date: 2026-10-10. Sources: https://shopparfait.com/ , https://shopparfait.com/store , https://shopparfait.com/store/cart , https://shopparfait.com/store/product/sculpt-jacket

This is a read-only content/route baseline from public HTML retrieval. It is not a database export, browser screenshot, pixel-diff baseline, dynamic payment test, or authorization proof. Product names/prices/inventory may legitimately change; reconcile against a fresh live read before migration/cutover. No user or payment data captured.

## Main public page /

- Document title: Home - Parfait
- H1: Welcome to Parfait
- Hero: Build your Parfait Body. Own your power.
- CTA links: Shop Parfait, Book Your Training Sessions
- Distinctive copy includes Wear The Grind, Build Your Parfait Body, Mission Statement, Vision Statement.
- Footer copyright: MYLEGND, LLC (2026)

## Store /store

- Document title: Collection - Parfait
- Catalog listing readback: nine products, each publicly displaying 15% Off at capture time.
- Sculpt Jacket: $65.00 reference / $55.25 displayed
- Allure Fit: $99.00 reference / $84.15 displayed
- Unintuitive Fit: $85.00 reference / $72.25 displayed
- Flex Form Jumpsuit: $75.00 reference / $63.75 displayed
- Poweride Bike Shorts: $45.00 reference / $38.25 displayed
- Hybridflex Leggings: $55.00 reference / $46.75 displayed
- Zenwave Sports Bra: $35.00 reference / $29.75 displayed
- Core Tank: $45.00 reference / $38.25 displayed
- Contour Fit: $99.00 reference / $84.15 displayed

## Product /store/product/sculpt-jacket

- Title: Sculpt Jacket - Parfait
- Collection-return link, image carousel, category Training
- Discount label 15% Off, size selector, Add to Cart, View Cart
- Product feature and care descriptions present
- Public inventory UI displayed 10 XS Left at readback; NOT authoritative inventory proof

## Cart /store/cart

- Title: Cart - Parfait, heading Your Cart
- Empty-cart readback: 0 Items, subtotal/discount/shipping/tax/total $0.00
- Promo input with Apply and Clear; Secure Checkout and Back To Shop
- Cart route accessible on empty session

## Not independently captured

- Live responsive screenshots, typography/color computed styles, dynamic DOM interactions
- Authenticated business management, client/team permission state
- Checkout /store/checkout and /store/success (direct anonymous retrieval unavailable)
- Payment provider sandbox/live idempotency, fulfillment and receipt webhooks
- Real SQL product/order/automation/media data inventory

## Cutover no-go checks

Do NOT equate successful public HTML retrieval with live runtime parity, billing proof, signed routing ownership, or safe ParfaitApp removal. Retain this as one historical comparison point only.
