# LEGEND shared design authority

This directory is the only platform-neutral source for LEGEND® visual values
and product-presentation rules. Native renderers consume the same JSON token
resource:

- `Legend-ios` bundles it and maps it to SwiftUI/UIKit types.
- `Legend-Android` bundles it from this directory and maps it to Compose types.

Do not add Swift, Kotlin, API DTOs, business logic, credentials, or platform
transport configuration here. Change a shared token here first, then validate
both native applications.
