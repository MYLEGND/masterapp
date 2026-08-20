# LEGEND NATIVE iOS IMPLEMENTATION DIRECTIVE

## EXECUTIVE MISSION

Build a production-grade native iOS application named **Legend** entirely inside the existing `/Legend-ios` directory of the MASTERAPP repository.

The authoritative naming is:

- Repository directory: `Legend-ios`
- Xcode project: `Legend.xcodeproj`
- Xcode workspace, if required: `Legend.xcworkspace`
- Xcode application target: `Legend`
- Product name: `Legend`
- App display name: `Legend`
- Swift application source directory: `Legend-ios/Legend`
- Unit-test target: `LegendTests`
- UI-test target: `LegendUITests`

Do not create or use a new root-level `/ios` directory.

Do not create a root-level `/Legend` directory.

Do not create a root-level `/LegendApp` directory.

All native iOS application files, Xcode files, tests, scripts, resources, configuration, and documentation must remain inside:

`MASTERAPP/Legend-ios/`

Legend must become a unified social and financial operating system for:

1. Agents who currently use AgentPortal.
2. Clients who currently use ClientApp.
3. Users who legitimately hold both roles.

This is not a website wrapper.

Do not create a WebView shell around AgentPortal or ClientApp.

Do not copy Razor pages into SwiftUI.

Do not reproduce desktop pages as vertically stacked mobile pages.

Do not embed the existing websites as the primary application experience.

Do not mechanically translate every page, section, card, table, or dashboard from the web applications into mobile screens.

Build a purpose-designed native SwiftUI application that uses MASTERAPP’s existing server-side business authority, identity, permissions, messaging relationships, subscriptions, financial data, workflows, and agent-client relationships.

Messaging must be the foundation of the mobile experience and the connective tissue between agents and clients.

The result must be an actual Xcode project or workspace inside `Legend-ios` that opens, compiles, tests, and runs through Xcode.

---

# ABSOLUTE PATH AND NAMING AUTHORITY

The repository layout must be:

MASTERAPP/
  AgentPortal/
  ClientApp/
  SHARED/
  Domain/
  Infrastructure/
  Legend-ios/
    Legend/
    LegendTests/
    LegendUITests/
    Documentation/
    Configuration/
    Resources/
    Scripts/
    Legend.xcodeproj

A workspace may also exist when genuinely required:

`Legend-ios/Legend.xcworkspace`

The internal `Legend-ios/Legend` directory is the Swift source directory for the Legend application target.

It is not a second application.

Do not place native source files directly throughout the MASTERAPP root.

Do not create:

- `MASTERAPP/ios`
- `MASTERAPP/Legend`
- `MASTERAPP/LegendApp`
- `MASTERAPP/Legend-iOS-App`
- another nested `Legend-ios/Legend-ios`

Do not rename:

- AgentPortal
- ClientApp
- SHARED
- Domain
- Infrastructure
- the MASTERAPP repository

The public application name shown to users must be exactly:

`Legend`

The repository folder must be exactly:

`Legend-ios`

---

# NON-NEGOTIABLE SAFETY RULES

1. Audit before modifying.
2. Read all applicable repository instructions, including every relevant `AGENTS.md`.
3. Inspect `git status`, the active branch, and the complete existing diff before changing anything.
4. Verify that the active branch is `production` or clearly report if it is not.
5. Do not discard, reset, stash, rewrite, clean, or overwrite existing work.
6. Do not run `git reset --hard`.
7. Do not run `git clean`.
8. Do not restore files that you did not modify.
9. Do not amend existing commits.
10. Do not commit or push unless explicitly instructed.
11. Do not deploy anything.
12. Do not run production migrations.
13. Do not modify production configuration or secrets.
14. Never place credentials, client secrets, access tokens, connection strings, private keys, raw payment data, or production-only identifiers into `Legend-ios`.
15. Preserve existing server-side authority.
16. Do not introduce parallel implementations of:
    - authentication;
    - authorization;
    - messaging identity;
    - conversation identity;
    - subscriptions;
    - recurring billing;
    - payment methods;
    - entitlements;
    - finance calculations;
    - client-agent ownership;
    - notifications;
    - auditing;
    - profile authority;
    - role authority;
    - account activation;
    - membership state.
17. Prefer exact replacements and cohesive architecture over overrides, patches stacked on patches, compatibility shims, or duplicated code paths.
18. Keep unrelated work untouched.
19. Do not claim completion unless the project compiles and applicable tests pass.
20. If an existing server contract is insufficient, document the verified gap and add the smallest secure mobile API contract to the existing authority.
21. All new backend endpoints must enforce server-side authentication, role authorization, ownership, participant authorization, and input validation.
22. Never trust a role, profile ID, client ID, agent ID, subscription status, entitlement, conversation ID, conversation participant, or ownership claim supplied only by the mobile client.
23. Never store raw card information.
24. Never log secrets, tokens, sensitive financial information, private message bodies, access tokens, authorization headers, bank data, or payment credentials.
25. Never silently weaken existing security to make native integration easier.
26. Never bypass existing authorization by calling internal services without the same ownership and participant checks.
27. Do not introduce mock production behavior.
28. Mocks are permitted only in previews and automated tests and must be isolated from production execution.
29. Do not invent backend behavior that does not exist.
30. Document any verified blocker honestly rather than hiding it with placeholders.

---

# FIRST ACTION: REPOSITORY AND WORKTREE AUDIT

Before implementation, inspect and map the existing system.

Begin with:

- current directory;
- repository root;
- active branch;
- `git status --short`;
- complete existing diff;
- untracked files;
- applicable `AGENTS.md` instructions;
- installed Xcode version;
- installed Swift version;
- available iOS simulator runtimes;
- existing contents of `Legend-ios`;
- existing project-generation tooling;
- existing build scripts;
- existing CI conventions.

Do not assume `Legend-ios` is empty.

Do not delete or replace its contents without auditing them.

At minimum, audit:

- MASTERAPP solution and project structure;
- AgentPortal;
- ClientApp;
- SHARED;
- Domain;
- Infrastructure;
- existing controllers;
- existing services;
- existing DTOs and view models;
- authentication and authorization;
- Microsoft identity configuration;
- user-to-agent and user-to-client profile resolution;
- users who may possess both roles;
- account activation;
- active-account requirements;
- subscription and entitlement authority;
- messaging entities;
- conversation key construction;
- participant types;
- SignalR hubs and clients;
- message read state;
- message attachments;
- notifications;
- profile images;
- client-agent relationships;
- CRM and client ownership;
- financial tools;
- stored financial state;
- financial calculation authority;
- analytics and telemetry;
- privacy controls;
- existing APIs;
- antiforgery and cookie assumptions;
- CORS and mobile-client requirements;
- deep-link-ready routes;
- tests covering these systems;
- subscription state;
- payment-method presentation;
- device registration capability;
- calendar and appointment authority;
- document authority;
- tasks and follow-up authority;
- role claims;
- Founder-only authorization;
- account impersonation behavior;
- identity-provider tenant and application assumptions;
- production host assumptions.

Use terminal searches and direct file inspection.

Do not guess from filenames.

Do not infer authority from UI labels.

Trace each workflow through:

1. Controller or endpoint.
2. Service.
3. Entity or persistent state.
4. Authorization.
5. Tests.
6. Client presentation.

Produce:

`Legend-ios/Documentation/MASTERAPP_MOBILE_AUTHORITY_AUDIT.md`

The audit must identify:

- existing authoritative entity or service;
- current web entry point;
- authorization rule;
- suitable mobile API contract, if one exists;
- missing contract, if any;
- safe native implementation;
- role restrictions;
- ownership restrictions;
- data classification;
- risks;
- unresolved dependencies;
- whether the workflow belongs in the first native release.

Do not begin broad UI implementation until this audit is written.

---

# PRODUCT DEFINITION

## One application, distinct role experiences

The app is named **Legend**.

It must support these authenticated states:

- Client only;
- Agent only;
- Agent and client;
- authenticated but inactive;
- authenticated but unauthorized;
- signed out;
- authentication expired;
- account requires activation;
- account lacks required entitlement;
- account temporarily unavailable.

The backend, not local UI assumptions, determines:

- identity;
- available roles;
- active account state;
- agent profile;
- client profile;
- current entitlement;
- accessible conversations;
- accessible clients;
- permitted actions;
- agent-client relationships;
- membership state;
- Founder privileges;
- organization context;
- profile ownership.

For dual-role users, provide a polished role or workspace switcher that preserves one signed-in session while keeping role-specific permissions, state, and navigation distinct.

Do not merge agent and client data merely because they share one login identity.

Do not infer that a user is an agent because they can access an agent-themed screen.

Do not infer that a user is a client because they have a client profile identifier stored locally.

Role state must be returned by authenticated server authority.

## Core product principle

Legend is one relationship-centered application with two specialized operating environments:

- the agent workspace;
- the client workspace.

They must feel like parts of one unified platform without collapsing their responsibilities or permissions.

Messaging is the shared bridge.

Financial collaboration is the shared operating layer.

The assigned agent-client relationship is the shared context.

## Agent experience

The agent experience must be built specifically for an agent’s mobile workday.

Prioritize:

- messaging command center;
- inbox and unread activity;
- assigned clients;
- client search;
- client relationship snapshots;
- tasks and required follow-up;
- appointments and schedule;
- leads and opportunities where authoritative support exists;
- concise CRM actions;
- subscription and membership status visibility where authorized;
- client financial progress;
- collaborative financial workflows;
- notifications;
- agent profile;
- settings;
- account security;
- quick relationship actions;
- mobile-first follow-up;
- daily priorities;
- recent client activity.

Do not reproduce large administrative desktop tables on mobile.

Transform complex reporting into:

- concise cards;
- ranked priorities;
- saved filters;
- drill-down views;
- bottom sheets;
- contextual actions;
- lightweight charts;
- progressive disclosure;
- focused task flows;
- searchable lists;
- expandable summaries;
- native menus;
- native swipe actions where safe;
- clear calls to action.

Founder-only or highly administrative functionality must remain permission-controlled and should not be copied into the first mobile release unless it has a justified native workflow.

Do not expose Founder tools to ordinary agents.

Do not recreate every AgentPortal page simply because it exists.

Determine what an agent actually needs while using a phone.

## Client experience

The client experience must be calm, trustworthy, understandable, premium, and action-oriented.

Prioritize:

- direct relationship with the assigned agent;
- messaging;
- financial home;
- financial health and progress;
- goals;
- next best actions;
- membership and billing;
- documents where supported;
- appointments;
- notifications;
- profile and security;
- collaborative financial tools that already have server authority;
- recent activity;
- upcoming obligations;
- financial plan progress;
- clear explanations;
- guided mobile workflows;
- secure personal information management.

Avoid exposing internal insurance, CRM, operational, Founder-only, or agent-only terminology.

Do not make clients interpret internal workflow states.

Translate authoritative state into client-appropriate language without changing its meaning.

Do not recreate every ClientApp page simply because it exists.

Determine what a client actually needs while using a phone.

## Agent-client relationship

The product must continually reinforce the relationship between the client and the correct assigned agent.

Examples:

- agent identity visible in relevant client contexts;
- client identity visible in relevant agent contexts;
- shared conversation;
- shared goal or financial-plan progress where authorized;
- collaborative action requests;
- appointment coordination;
- clear ownership of next steps;
- shared document requests where supported;
- shared financial review context;
- client follow-up context;
- agent guidance context;
- relationship history where authorized.

Do not create public client discovery.

Do not create public financial profiles.

Do not create unrestricted user-to-user messaging.

Do not create follower counts, public feeds, vanity metrics, or open social posting without verified business authority.

This is a permissioned social-financial network, not an open social network.

The social layer must strengthen trusted financial relationships rather than imitate consumer social media.

---

# MESSAGING-FIRST INFORMATION ARCHITECTURE

Messaging is the app’s central operating layer.

Design a first-class native messaging experience with:

- conversation inbox;
- unread counts;
- direct agent-client conversations;
- authorized internal conversations if supported;
- fast message composer;
- optimistic sending with authoritative reconciliation;
- delivery state;
- failed-send recovery;
- read state;
- typing and presence only if securely supported;
- attachment support only where the backend already safely supports it;
- profile images;
- participant role presentation;
- timestamps localized for the user;
- push-notification routing;
- deep links into the correct conversation;
- conversation search;
- contextual actions;
- empty states;
- offline states;
- loading states;
- error states;
- pagination;
- accessibility;
- Dynamic Type;
- VoiceOver labels;
- reduced-motion behavior;
- keyboard handling;
- draft preservation where safe;
- duplicate-send prevention;
- connection-state communication;
- reconnection handling;
- authorization-expiry handling;
- scroll-position preservation;
- deterministic message ordering;
- safe retry behavior;
- server-authoritative timestamps;
- server-authoritative participants;
- secure attachment presentation;
- conversation-specific actions.

Preserve the existing server-side participant-type and conversation-key authority.

Never identify a participant solely by:

- email;
- display name;
- first name;
- UPN;
- avatar path;
- user-supplied profile identifier;
- a locally cached role.

If the existing SignalR client contract can safely support native iOS, use it.

If a transport adaptation is needed, preserve the same message and authorization authority rather than creating a second messaging system.

Do not create a parallel mobile-only message table.

Do not create mobile-only conversation keys.

Do not reimplement conversation authorization in Swift.

Messaging should connect naturally to:

- a client profile;
- an agent profile;
- a financial goal;
- an appointment;
- a task;
- a document request;
- a membership issue;
- a follow-up;
- an authorized financial workflow;
- a client review;
- an activity item;
- a requested action.

Contextual messaging must link to authoritative objects.

Do not duplicate authoritative object state inside chat metadata.

The messaging experience should be immediately reachable from all primary workspaces.

---

# NATIVE MOBILE EXPERIENCE

Build with Swift and SwiftUI using current stable Apple SDK conventions available in the installed Xcode version.

Use UIKit only when a required capability is not adequately supported by SwiftUI.

Prefer:

- SwiftUI application lifecycle;
- NavigationStack;
- modern observation;
- structured concurrency;
- async/await;
- actors where they solve real isolation requirements;
- URLSession;
- Codable;
- Swift Package Manager;
- Keychain for sensitive session material;
- OSLog with privacy controls;
- BackgroundTasks only where justified;
- UserNotifications;
- AuthenticationServices;
- LocalAuthentication;
- PhotosUI;
- native share sheets;
- native accessibility APIs;
- XCTest;
- XCUITest;
- Swift Charts when appropriate;
- native refresh controls;
- native search;
- native sheets;
- native menus;
- native context menus;
- native alerts;
- native haptics used sparingly.

Do not add a third-party dependency when Apple’s frameworks adequately provide the capability.

All dependencies must:

- be actively maintained;
- have a justified purpose;
- use a pinned or controlled version;
- be documented;
- avoid collecting unnecessary data;
- be compatible with App Store distribution;
- avoid introducing unnecessary binary size;
- avoid duplicating Apple framework capabilities;
- be reviewed for privacy and licensing.

Do not use React Native.

Do not use Flutter.

Do not use Ionic.

Do not use Capacitor.

Do not use Cordova.

Do not use a WebView as the application foundation.

The required implementation is a native SwiftUI application.

---

# MOBILE-FIRST UX REQUIREMENTS

Every screen must be designed for a phone first.

Do not begin with desktop layout assumptions.

For each existing web workflow:

1. Identify the user’s actual mobile goal.
2. Identify the minimum authoritative data needed.
3. Identify the safest primary action.
4. Remove desktop-only density.
5. Use progressive disclosure.
6. Break long workflows into focused steps.
7. Keep critical context visible.
8. Preserve user position and unsaved state where safe.
9. Design loading, empty, offline, error, and success states.
10. Add accessibility semantics.
11. Validate small-screen behavior.
12. Validate keyboard behavior.
13. Validate one-handed reachability.
14. Validate dark mode.
15. Validate large Dynamic Type.

Do not create long-form mobile pages containing all desktop content.

Do not place dozens of cards into one scrolling dashboard.

Do not convert every desktop sidebar item into a bottom-tab item.

Do not use horizontal scrolling for primary financial information unless it is clearly the best native interaction.

Do not shrink desktop tables until they fit.

Replace tables with:

- focused lists;
- summary rows;
- filters;
- search;
- drill-down details;
- segmented views;
- expandable sections;
- native charts;
- contextual menus;
- dedicated detail screens.

Every primary screen must have a clear purpose.

Every primary action must be visible and understandable.

---

# DESIGN SYSTEM

Create a cohesive native design system, not disconnected screen styling.

Create reusable foundations for:

- typography;
- semantic spacing;
- corner radii;
- elevation;
- material and surface hierarchy;
- color roles;
- iconography;
- buttons;
- text fields;
- secure fields;
- search;
- avatars;
- badges;
- metric cards;
- action cards;
- timeline rows;
- empty states;
- skeleton states;
- error states;
- banners;
- sheets;
- dialogs;
- charts;
- message bubbles;
- financial values;
- privacy-sensitive values;
- responsive layouts;
- navigation bars;
- tab bars;
- toolbars;
- menus;
- list rows;
- section headers;
- status indicators;
- relationship cards;
- progress displays;
- account-state displays;
- membership-state displays.

Brand characteristics:

- premium;
- disciplined;
- trustworthy;
- modern;
- warm;
- human;
- financially credible;
- socially connected;
- calm;
- clear;
- confident;
- polished;
- not visually noisy;
- not gimmicky;
- not casino-like;
- not a generic banking clone;
- not a generic social-media clone;
- not a generic insurance app;
- not an imitation of the desktop site;
- not filled with decorative gradients without purpose;
- not dependent on excessive animation.

Support:

- light mode;
- dark mode;
- Dynamic Type;
- VoiceOver;
- sufficient contrast;
- reduced motion;
- one-handed interaction;
- safe-area behavior;
- keyboard avoidance;
- all supported iPhone sizes;
- graceful iPad adaptation where practical;
- landscape where appropriate;
- localization-ready layout;
- content-size category changes;
- increased contrast settings;
- button shapes;
- differentiate-without-color requirements.

Use native interaction patterns.

Primary navigation should be intentionally different by role.

Potential client foundation:

- Home;
- Messages;
- Plan;
- Activity;
- Profile.

Potential agent foundation:

- Command;
- Messages;
- Clients;
- Activity;
- Profile.

These labels are hypotheses, not mandates.

Validate them against the repository audit and actual supported capabilities.

Keep messaging immediately accessible from every primary workspace.

Do not use more primary tabs than the mobile information architecture genuinely supports.

---

# FINANCIAL OPERATING SYSTEM

Do not duplicate server-side financial calculations in Swift unless the repository already defines a portable, verified calculation contract and local execution is explicitly necessary.

The server remains authoritative for:

- persisted financial state;
- calculation outputs;
- client ownership;
- agent access;
- financial workflow authorization;
- membership requirements;
- imported-data state;
- final stored values.

The mobile app should present financial information as:

- current position;
- trend;
- progress;
- risks;
- opportunities;
- next action;
- agent collaboration;
- explainable calculations;
- transparent assumptions;
- recent changes;
- upcoming impact;
- actionable decisions;
- relationship context.

Use progressive disclosure.

A client should not need to interpret a desktop report on a phone.

Transform deep financial tooling into focused mobile sessions, such as:

- review this week;
- update an income stream;
- confirm a recurring expense;
- review upcoming cash flow;
- inspect a debt trajectory;
- adjust a goal;
- discuss the result with an agent;
- understand what changed;
- see the next recommended action;
- verify an assumption;
- approve an update;
- request agent guidance;
- review progress over time.

Do not invent financial recommendations.

Do not claim fiduciary authority.

Do not present projections as guarantees.

Use existing authenticated server data and clearly distinguish:

- user-entered data;
- calculated values;
- estimates;
- imported data;
- agent guidance;
- system-generated insights;
- pending changes;
- confirmed changes;
- stale cached data.

Protect sensitive financial data in:

- logs;
- screenshots;
- notifications;
- background previews;
- analytics;
- crash reports;
- clipboard operations;
- cached files;
- app-switcher snapshots where appropriate.

Implement privacy controls appropriate to the data actually exposed.

Use decimal-safe money handling.

Do not use binary floating-point for authoritative currency arithmetic.

Use server-provided formatted values where appropriate and preserve raw decimal-safe representations where calculations or sorting require them.

---

# AUTHENTICATION AND ACCOUNT ACCESS

Audit the existing Microsoft identity and account-resolution architecture before choosing an iOS authentication implementation.

Use the existing identity authority.

Implement a secure native authorization-code-with-PKCE flow appropriate to the verified identity configuration.

Use system authentication sessions.

Do not use an embedded username and password form.

Do not collect Microsoft credentials directly.

Do not invent a second user database.

Do not hard-code production identity configuration.

Do not commit secrets.

Provide configuration separation for:

- Debug/local;
- staging, if the repository supports it;
- production.

Configuration must use non-secret build settings or configuration files where appropriate.

Sensitive tokens must be protected using the Keychain.

Implement:

- sign in;
- sign out;
- token or session restoration;
- expiration handling;
- refresh handling where supported;
- authorization failure handling;
- inactive-account handling;
- role resolution;
- dual-role workspace selection;
- secure deep-link continuation;
- account switching where supported;
- session invalidation;
- cancellation of authentication;
- network failure handling;
- unavailable identity-provider handling;
- signed-out deep-link handling;
- reauthentication where required;
- secure local state clearing on sign out.

Biometrics may protect restoration of an existing local session but must not replace the server identity authority.

Do not assume that the existing browser cookie flow can be reused directly by the native application.

Audit the current identity registration and determine the smallest correct native-client addition required.

Do not weaken existing browser authentication.

---

# ACTIVE ACCOUNT AND ENTITLEMENT REQUIREMENTS

Users must be able to enter an authorized Legend workspace only when the server confirms the required active account state.

The application must distinguish:

- valid active account;
- inactive account;
- pending activation;
- subscription activation required;
- entitlement missing;
- access revoked;
- role unavailable;
- user unauthorized;
- account configuration incomplete.

Do not use a locally cached Boolean as the final access decision.

The server must remain authoritative.

Do not assume every agent uses the same subscription requirement as every client.

Audit the current authorization and activation policies separately for AgentPortal and ClientApp.

Provide a native state-specific experience rather than redirect loops.

Do not expose protected content while account state is unresolved.

---

# NETWORKING AND API ARCHITECTURE

Create one coherent networking layer.

Required concerns:

- environment configuration;
- authenticated requests;
- token renewal where supported;
- typed request and response models;
- server problem details;
- request IDs;
- correlation IDs;
- pagination;
- cancellation;
- retry rules;
- idempotency where appropriate;
- reachability-aware UX;
- timeout handling;
- secure logging;
- decoding failure visibility in Debug;
- no sensitive payload logging;
- date handling;
- decimal and currency precision;
- testable transport abstraction;
- HTTP status handling;
- content-type validation;
- retry-after behavior;
- cancellation propagation;
- request deduplication where useful;
- upload behavior;
- download behavior;
- background transition handling;
- environment-specific base URLs;
- certificate and transport security;
- deterministic error mapping.

Do not silently convert money through floating-point arithmetic.

Respect the server’s currency and decimal authority.

Do not retry non-idempotent writes blindly.

Do not automatically retry a message send unless duplicate-send protection is authoritative.

Do not automatically retry a billing or payment action unless the server supports safe idempotency.

Design offline behavior intentionally:

- cache only what is safe and useful;
- clearly mark stale information;
- queue only operations that can be safely replayed;
- never imply a financial, billing, or messaging write succeeded before server confirmation;
- do not persist private data unnecessarily;
- provide an explicit retry path;
- preserve drafts where safe;
- prevent duplicate submissions;
- handle transitions between offline and online states;
- invalidate protected caches at sign out.

---

# SERVER CONTRACT RULES

Before adding any mobile endpoint, search for an existing reusable service.

Controllers must remain thin.

Use existing services and domain authority.

Mobile endpoints should return purpose-built DTOs rather than Razor view models.

Do not serialize EF entities directly.

Do not expose database entities as public contracts.

Use explicit versioned API routing if the repository’s conventions permit it.

At minimum, determine whether secure mobile contracts are needed for:

- session/bootstrap;
- role and profile resolution;
- account state;
- client home;
- agent command center;
- conversations;
- messages;
- unread counts;
- client search;
- agent-client relationship;
- financial dashboard;
- financial tool state;
- goals;
- tasks;
- appointments;
- notifications;
- membership;
- payment-method presentation;
- payment-method operations;
- profile;
- device registration;
- deep-link resolution;
- activity;
- documents;
- attachment authorization;
- profile-image delivery;
- workspace switching;
- logout or session invalidation.

Every endpoint must have focused authorization tests.

Do not expose backend internals simply to make UI implementation easier.

Do not create a giant mobile bootstrap response containing all protected data.

Use purpose-built, bounded contracts.

Add pagination to potentially unbounded collections.

Apply cancellation tokens.

Use server-side validation.

Use existing audit infrastructure where applicable.

Mobile endpoints must not weaken antiforgery or browser protections globally.

If token-authenticated native APIs require different handling, isolate that handling to the native API surface.

Do not globally disable antiforgery.

Do not globally weaken CORS.

Do not permit wildcard origins with credentials.

---

# PUSH NOTIFICATIONS

Design a secure notification architecture without assuming production Apple credentials exist.

Create native device-registration support and server contracts only where justified.

Never place these items in lock-screen notification text:

- private financial values;
- private message bodies;
- health information;
- payment details;
- account numbers;
- sensitive client details;
- private document names;
- access tokens;
- confidential CRM information.

Notifications should use minimal generic text and route the authenticated user into the authorized app destination.

Support categories that exist in the actual product, potentially including:

- new message;
- action requested;
- appointment update;
- membership action;
- financial-plan update;
- task reminder.

Do not fabricate notification categories without underlying business events.

Document all Apple Developer and server configuration that still requires human credentials.

Device tokens must be treated as replaceable device identifiers, not user identity.

Device registration must be associated server-side with the authenticated user.

Support token rotation and device removal.

Do not assume APNs delivery proves authorization.

Reauthorize the destination after the user opens the notification.

---

# PROJECT STRUCTURE

Create a maintainable feature-oriented structure similar to:

Legend-ios/
  Legend/
    App/
    Core/
      Authentication/
      Networking/
      Persistence/
      Security/
      DesignSystem/
      Navigation/
      Telemetry/
      Utilities/
    Features/
      Bootstrap/
      RoleSelection/
      ClientHome/
      AgentCommand/
      Messaging/
      Clients/
      FinancialPlan/
      Activity/
      Membership/
      Notifications/
      Profile/
      Settings/
    Resources/
    Configuration/
  LegendTests/
  LegendUITests/
  Documentation/
  Scripts/
  Legend.xcodeproj

Adapt this structure only when a clearly better native architecture is justified.

Avoid:

- one massive AppState;
- one massive networking service;
- one massive view model;
- feature logic in views;
- duplicated DTOs;
- global mutable state;
- singleton-heavy architecture;
- unnecessary protocol abstraction for every type;
- speculative frameworks;
- premature modularization that makes the project difficult to build;
- circular feature dependencies;
- direct URLSession calls throughout views;
- direct Keychain calls throughout features;
- direct business calculations inside SwiftUI views.

Keep feature boundaries clear.

Keep shared foundations genuinely shared.

Do not create abstractions without a verified use case.

---

# XCODE PROJECT REQUIREMENTS

Create a real Xcode project or workspace inside:

`Legend-ios`

The preferred project path is:

`Legend-ios/Legend.xcodeproj`

If a workspace is genuinely required, use:

`Legend-ios/Legend.xcworkspace`

Product name:

`Legend`

Application target:

`Legend`

Display name:

`Legend`

Unit-test target:

`LegendTests`

UI-test target:

`LegendUITests`

Use a placeholder bundle identifier that is clearly documented if the exact production identifier is not already authoritative in the repository.

Do not invent:

- Apple Team ID;
- App Store Connect identifiers;
- APNs credentials;
- production associated domains;
- production universal-link configuration;
- production signing certificates;
- production bundle ID;
- merchant identifiers;
- Sign in with Apple configuration;
- Keychain access groups.

The Xcode project must include:

- application target;
- unit-test target;
- UI-test target;
- Debug configuration;
- Release configuration;
- Swift Package Manager dependencies, if any;
- asset catalogs;
- native launch experience;
- privacy manifest where required by used APIs;
- entitlements only when genuinely required;
- configuration placeholders;
- no committed secrets;
- appropriate deployment target;
- build settings that do not rely on one developer’s absolute machine paths;
- valid Info.plist configuration;
- correct app display name;
- required usage descriptions only for capabilities actually used.

If project-generation tooling is used, preserve both its source configuration and a generated Xcode project that can be opened immediately.

Do not require the user to regenerate the project merely to open it for the first time.

Prefer the simplest reliable project strategy compatible with the installed environment.

The project must not depend on `/Users/zacowen/...` absolute paths.

All repository-relative paths must continue working after the repository is moved or cloned elsewhere.

---

# CONFIGURATION REQUIREMENTS

Create explicit native configuration for:

- local development;
- staging, when verified;
- production.

Do not commit secrets.

Document required non-secret values.

Use `.xcconfig` files or another justified native configuration mechanism.

Provide checked-in example or template configuration where needed.

Ensure secret-bearing local files are ignored.

Do not place placeholder secrets that look real.

The app must fail clearly when required configuration is missing.

Do not silently fall back to production.

Do not silently fall back to localhost.

---

# IMPLEMENTATION PHASES

## Phase 0 — Verified architecture

Deliver:

- repository/mobile authority audit;
- mobile product architecture;
- server contract inventory;
- authorization matrix;
- data classification;
- navigation map;
- implementation plan;
- risk register;
- existing-worktree assessment;
- native authentication assessment;
- API gap assessment;
- messaging transport assessment.

## Phase 1 — Compiling native foundation

Deliver:

- Xcode project;
- app lifecycle;
- environment configuration;
- networking foundation;
- authentication shell;
- secure storage;
- session bootstrap;
- account-state resolution;
- role resolution;
- role-aware navigation;
- native design system;
- representative loading states;
- representative empty states;
- representative error states;
- unit-test foundation;
- UI-test foundation;
- build scripts;
- developer documentation.

The project must compile before moving into broad feature work.

## Phase 2 — Messaging vertical slice

Deliver one real end-to-end vertical slice:

- authorized conversation list;
- conversation detail;
- message history;
- native composer;
- send behavior;
- server reconciliation;
- unread handling;
- read handling;
- connection state;
- reconnection behavior;
- deep-link model;
- participant authorization;
- message ordering;
- duplicate-send protection;
- tests.

Use actual repository authority.

Do not substitute permanent mock data.

Mocks may exist only for previews and tests and must be clearly separated from production paths.

The vertical slice must be complete enough to prove the native architecture.

## Phase 3 — Role-specific foundations

Client:

- client home;
- assigned-agent relationship;
- financial snapshot;
- next actions;
- membership summary;
- activity;
- profile;
- secure settings.

Agent:

- command center;
- priority inbox;
- client search;
- client snapshot;
- follow-up actions;
- activity;
- profile;
- secure settings.

The two workspaces must remain distinct.

Shared components may be reused where the underlying experience is genuinely shared.

## Phase 4 — Financial workflows

Implement focused native workflows backed by existing server authority.

Prioritize the workflows that create the most mobile value.

Do not port every financial tool indiscriminately.

For each workflow, document:

- authoritative service;
- authoritative stored state;
- authorization;
- mobile contract;
- native interaction model;
- offline behavior;
- tests.

## Phase 5 — Native platform capabilities

Only after the core architecture is stable:

- push notifications;
- biometrics;
- secure deep links;
- universal links;
- document workflows;
- camera workflows;
- background refresh where justified;
- privacy protections;
- analytics;
- diagnostics;
- app-state privacy;
- haptic feedback;
- native sharing where safe.

Do not prioritize cosmetic breadth over verified end-to-end functionality.

---

# TESTING REQUIREMENTS

Add tests continuously.

At minimum, cover:

- session bootstrap;
- role resolution;
- dual-role switching;
- inactive accounts;
- unauthorized accounts;
- token or session failure;
- account activation state;
- endpoint authorization;
- client-agent ownership;
- conversation participant authorization;
- message DTO decoding;
- pagination;
- message send reconciliation;
- duplicate-send prevention;
- offline states;
- timeout states;
- financial decimal decoding;
- membership-state presentation;
- sensitive logging protection;
- deep-link authorization;
- navigation;
- accessibility identifiers for critical flows;
- environment configuration;
- sign-out cleanup;
- invalid server responses;
- stale cache state;
- retry behavior;
- server problem details;
- profile separation for dual-role users.

Run all relevant existing .NET tests after backend changes.

Run native unit tests.

Run native UI smoke tests.

Use available simulator destinations.

Do not suppress warnings merely to achieve a green build.

Do not disable tests because they fail.

Correct the implementation or document a proven environmental blocker.

Document environmental blockers precisely.

When an iOS simulator build is possible, use an explicit available destination rather than assuming a device name.

Run `git diff --check`.

Inspect the final diff.

Verify that no secret-bearing files were added.

Verify that no raw payment data was added.

Verify that no new root-level `ios`, `Legend`, or `LegendApp` folder was created.

---

# REQUIRED DOCUMENTATION

Create:

- `Legend-ios/README.md`
- `Legend-ios/Documentation/MASTERAPP_MOBILE_AUTHORITY_AUDIT.md`
- `Legend-ios/Documentation/PRODUCT_ARCHITECTURE.md`
- `Legend-ios/Documentation/AUTHORIZATION_MATRIX.md`
- `Legend-ios/Documentation/API_CONTRACTS.md`
- `Legend-ios/Documentation/SECURITY_AND_PRIVACY.md`
- `Legend-ios/Documentation/BUILD_AND_SIGNING.md`
- `Legend-ios/Documentation/APP_STORE_READINESS.md`
- `Legend-ios/Documentation/KNOWN_GAPS.md`
- `Legend-ios/Documentation/IMPLEMENTATION_LOG.md`

`BUILD_AND_SIGNING.md` must explain the exact human steps required to:

- open the project in Xcode;
- open the workspace instead when one exists;
- select a development team;
- set the final bundle identifier;
- configure signing;
- configure environments;
- run on a simulator;
- run on a physical device;
- archive;
- validate;
- upload to App Store Connect;
- configure any required capabilities;
- provide missing Apple credentials;
- set production configuration safely.

The expected opening command should be one of:

`open Legend-ios/Legend.xcodeproj`

or:

`open Legend-ios/Legend.xcworkspace`

Do not claim the app is ready for App Store submission until required credentials, privacy declarations, screenshots, metadata, export-compliance answers, policy review, backend readiness, and release validation are actually complete.

---

# SECURITY AND PRIVACY REQUIREMENTS

Classify all mobile-accessible data.

At minimum, distinguish:

- public;
- authenticated;
- client-private;
- agent-private;
- relationship-shared;
- financial-sensitive;
- payment-sensitive;
- administrative;
- Founder-only;
- operational telemetry.

Do not cache sensitive data without a verified product requirement.

Do not include sensitive values in analytics.

Do not include private message bodies in crash reports.

Do not include authorization headers in logs.

Use Keychain for sensitive session material.

Clear protected local state when the user signs out.

Protect app-switcher snapshots when screens contain sensitive information, where appropriate.

Do not prevent screenshots globally without a justified reason.

Do not claim screenshot prevention is absolute.

Use privacy-aware logging.

Ensure Debug diagnostics cannot leak into Release behavior.

Document all data stored locally.

Document all data sent to third parties.

Document all required App Store privacy disclosures based on the actual implementation.

---

# ACCESSIBILITY REQUIREMENTS

Accessibility is part of completion, not a later cosmetic pass.

Support:

- VoiceOver;
- Dynamic Type;
- reduced motion;
- sufficient contrast;
- meaningful accessibility labels;
- accessibility values;
- accessibility hints where useful;
- correct heading structure;
- logical focus order;
- minimum tap targets;
- keyboard and switch-control compatibility where practical;
- content that does not rely only on color;
- chart alternatives or summaries;
- meaningful status announcements.

Add stable accessibility identifiers to critical authentication, navigation, messaging, and role-switching flows for UI testing.

Test at large content sizes.

Do not truncate essential financial or relationship information without an accessible way to reveal it.

---

# PERFORMANCE REQUIREMENTS

Design for realistic mobile conditions.

Avoid:

- loading all conversations at once;
- loading all messages at once;
- loading all clients at once;
- unbounded in-memory caches;
- decoding large responses on the main actor;
- blocking the main thread;
- excessive polling;
- duplicate network requests;
- unnecessary image downloads;
- full-resolution image loading when thumbnails are sufficient.

Use pagination.

Use cancellation.

Use lazy rendering.

Use image caching only when safe and bounded.

Keep UI state responsive during network operations.

Measure before introducing complex optimization infrastructure.

---

# WORKING STYLE

Work in verified increments.

Before each phase:

1. inspect the current implementation;
2. state the verified authority being reused;
3. identify the smallest cohesive change;
4. implement it;
5. build;
6. test;
7. inspect the diff;
8. update documentation.

Provide progress updates based on actual findings, not assumptions.

Do not stop after creating placeholders or a visual prototype.

Do not spread incomplete stubs across the entire app.

Complete vertical slices.

Do not create dozens of empty feature folders and call the architecture complete.

The first functional priority is:

1. secure authentication and bootstrap;
2. account-state resolution;
3. role-aware navigation;
4. messaging;
5. agent-client relationship;
6. client and agent home experiences;
7. financial workflows;
8. membership;
9. native enhancements.

When the full vision cannot safely be completed in one execution, complete the current vertical slice fully and leave an exact documented continuation point.

Do not claim unfinished phases are complete.

---

# FINAL VALIDATION

Before finishing:

1. Confirm all native work is inside `Legend-ios`.
2. Confirm no root-level `ios` directory was created.
3. Confirm no root-level `Legend` directory was created.
4. Confirm no root-level `LegendApp` directory was created.
5. Confirm the app product name is `Legend`.
6. Confirm the application target is `Legend`.
7. Confirm the display name is `Legend`.
8. Confirm the project path is `Legend-ios/Legend.xcodeproj` or the documented workspace path.
9. Confirm the project builds with the installed Xcode toolchain, or document a proven environmental blocker.
10. Confirm native unit tests run.
11. Confirm applicable UI smoke tests run.
12. Confirm affected .NET tests run.
13. Confirm `git diff --check` passes.
14. Confirm no secrets were added.
15. Confirm no production deployment occurred.
16. Confirm no production migration was run.
17. Confirm no unrelated working-tree changes were discarded.
18. Confirm documentation uses `Legend-ios`, not the obsolete root path `ios`.
19. Confirm there is no WebView-based reproduction of AgentPortal or ClientApp.
20. Confirm at least one real server-connected vertical slice exists before claiming production foundation completion.

---

# DEFINITION OF DONE FOR THIS EXECUTION

This execution is complete only when:

1. The existing MASTERAPP architecture has been audited.
2. A real native Xcode project exists under `Legend-ios`.
3. The project opens through the documented Xcode project or workspace.
4. It builds successfully with the installed Xcode toolchain, or an exact environmental blocker is proven.
5. Unit tests for the native foundation run.
6. Existing .NET tests affected by backend changes pass.
7. Authentication and role architecture are implemented without duplicating identity.
8. Account-state and entitlement resolution remain server-authoritative.
9. At least one production-connected messaging vertical slice is implemented against authenticated server contracts.
10. Agent and client navigation are distinct and role-authorized.
11. The assigned agent-client relationship remains authoritative.
12. There is no WebView-based reproduction of AgentPortal or ClientApp.
13. No secrets or raw payment data are present.
14. Documentation identifies all remaining requirements honestly.
15. `git diff --check` passes.
16. The final report includes:
    - files created;
    - files modified;
    - architecture reused;
    - server contracts added;
    - tests run;
    - build results;
    - known gaps;
    - exact Xcode opening command;
    - exact next human actions;
    - confirmation that all native files are contained inside `Legend-ios`.

If completing all phases safely exceeds one execution:

- finish the current vertical slice completely;
- leave the repository build-clean;
- preserve all existing unrelated work;
- document the exact continuation point;
- do not claim unfinished phases are complete;
- do not leave knowingly broken compilation;
- do not create superficial breadth instead of a completed foundation.
