# LEGEND product and design authority

`legend-design.tokens.json` is the one value authority for the native iOS and
Android renderers. Its initial values were extracted from the working iOS
`LegendNextTheme` implementation. This document records platform-neutral
product rules—not backend logic and not SwiftUI or Compose source.

## Product hierarchy

1. Launch resolves the established identity and server session before showing
   the authenticated shell.
2. The shell carries the fixed LEGEND® wordmark and compact tab navigation:
   Home; agent-only Clients; Discover; For You; Messages; Account.
3. Discover is the one primary tab with the full navy presentation. Standard
   screens use the adaptive quiet canvas; sheets use the midnight/navy language.
4. A message thread temporarily owns its navigation space and hides the shell
   tab bar. Notification navigation opens the corresponding conversation.
5. Home, social, messages, financial intelligence, Journey Circles, account,
   language, safety, and lifecycle screens display server-authoritative data.

## Shared presentation rules

- Compactness is deliberate: 16-point page gutters, 14-point card content,
  46-point controls, and 44-point minimum touch targets.
- Navy/midnight establish structure; gold is an intentional accent for primary
  emphasis, selected state, metrics, and creation—not generic decoration.
- Elevated cards are quiet, bordered, and softly shadowed. Avoid oversized
  Material cards, generic blue controls, or excessive floating controls.
- Loading uses a calm brand-colored progress/skeleton treatment. Empty states
  use the elevated card, gold icon circle, concise title, supporting copy, and
  an optional real action. Errors use the compact danger icon/card and retry
  only where a real retry exists.
- Recipient-facing message body is primary. When the server exposes a distinct
  original body, it is shown beneath it with the canonical `Original` label.

## Social creation contract

The creation flow is shared product behavior, while media UI is native:

1. Choose Post, Story, or Hac.
2. Choose or capture supported media; text-only publication is allowed only
   where the established content type permits it.
3. Preview and edit the selected source with native crop, adjustment, cover,
   trim, and playback facilities that the product supports.
4. Add caption, accessibility text, tags/mentions, location, audience, and
   comment preference.
5. Publish through the existing server upload/publishing authority; preserve
   progress, failure, and retry behavior. Discard explicitly removes draft
   media/edit state.

## Authority boundaries

The backend remains authoritative for identity, authorization, messaging,
translation, financial projections, social visibility and actions, safety,
account lifecycle, notification content, and media processing. APNs and FCM
are platform transports only. SwiftUI and Compose are native renderers only.
