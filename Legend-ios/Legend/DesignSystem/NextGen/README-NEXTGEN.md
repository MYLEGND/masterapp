# Legend NextGen Design System

This directory is the new presentation authority for Legend iOS.

## Non-negotiable boundary

The NextGen system owns visual presentation only:

- colors
- typography
- spacing
- surfaces
- controls
- cards
- avatars
- loading and empty states
- animation and visual modifiers

It does not own or modify:

- authentication
- API clients
- network requests
- backend contracts
- stores
- persistence
- authorization
- identity resolution
- business calculations
- messaging transport

## Migration model

The existing design system remains in place while screens migrate individually.

1. Application shell
2. Home
3. Messages
4. Social
5. Finance
6. Journey Circles
7. Account and profile
8. Legacy reference audit
9. Legacy design-system deletion

## Naming

All new types begin with `LegendNext` to prevent collisions with the existing system.

## Core primitives

- `LegendNextColor`
- `LegendNextGradient`
- `LegendNextSpacing`
- `LegendNextRadius`
- `LegendNextTypography`
- `LegendNextMotion`
- `LegendNextSurface`
- `LegendNextButtonStyle`
- `LegendNextHero`
- `LegendNextSectionHeader`
- `LegendNextBadge`
- `LegendNextMetricTile`
- `LegendNextQuickAction`
- `LegendNextAvatar`
- `LegendNextLoadingState`
- `LegendNextEmptyState`
- `LegendNextErrorState`
- `LegendNextSkeletonCard`
