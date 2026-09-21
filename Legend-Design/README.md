# LEGEND shared design authority

This directory is the only platform-neutral source for LEGEND® visual values
and product-presentation rules. Native renderers consume the same JSON token
resource:

- `Legend-ios` bundles it and maps it to SwiftUI/UIKit types.
- `Legend-Android` bundles it from this directory and maps it to Compose types.

Do not add Swift, Kotlin, API DTOs, business logic, credentials, or platform
transport configuration here. Change a shared token here first, then validate
both native applications.

## Shared web presentation

`legend-web-foundation.css` owns the existing public web palette, typography,
base page behavior, and scrollbar presentation across all four web experiences.
`legend-public-web.css` composes public content; `legend-app-shell.css` composes
full-width authenticated layouts, Explore and footers. Both consume that one
foundation. Existing `SHARED/wwwroot/css/dashboard-home-shared.css` remains the
single owner of application navigation, dashboard, messaging and modal components.
Application-specific styles contain functional components, not alternate global
themes. Keep widths fluid at page boundaries; constrain readable prose and dialogs
at the component that owns them. Scrollbars are hidden without disabling scrolling.

ASP.NET projects link these assets into `wwwroot/design` at publish time; the
static public website bundles the same foundation. There are no copied theme
forks to update. The native JSON token contract is unchanged by web layout edits.
