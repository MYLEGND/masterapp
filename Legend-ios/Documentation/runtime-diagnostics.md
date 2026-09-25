# Native runtime observations

`MobileHTTPClient` is the iOS collection and authenticated upload boundary. It
records HTTP failures, network failures, and JSON decoding failures to the same
server issue authority via `POST /api/v1/mobile/runtime-diagnostics`. These are
untrusted observations; the server decides classification and Founder access.
There is no native release, repair, model-call, or diagnostic management authority.

The payload contains a fixed error category, HTTP method/status, a route template
with dynamic segments removed, the transport source path, timestamp and bundle
build metadata. It never includes HTTP bodies, query values, identity, credentials,
or exception descriptions. Existing debug performance output also uses the route
template. Tokens exist only in the existing transport and transient replay call.

The app-private queue retains at most 20 deduplicated observations / 64 KiB,
with at most three attempts per observation and three sends per minute. A later
authenticated request provides a replay opportunity, including after relaunch.
There is no background timer or unauthenticated uploader. Upload failures do not
create new observations. Pending observations use app preferences; persistence is
best effort and the most recent write can be lost on abrupt termination.

No existing safe fatal-crash hook was available. This implementation does not
catch Swift traps, signals, arbitrary unhandled Objective-C exceptions, or claim
that every crash can be recorded. It persists caught observations during ordinary
execution; it does not perform networking or Swift work from a signal handler.

The existing `scripts/sync-published-checkout.py --check-native` authority writes
`LegendBuildProvenance.json` under Xcode DerivedData from the actual verified HEAD.
The existing scheme pre-action requests that artifact, and a sandboxed target
resource phase copies it into the app. The guard removes an old artifact before
checking; a failed guard leaves the target without its required input and fails
the build. No build artifact is written into source or Git directories, and no
manual SHA setting competes with the checkout authority. The receipt also records
whether uncommitted changes were present; HEAD is not a claim that those changes
are committed. CFBundle version fields supply version metadata. The server treats
client metadata as an unverified hint. Existing diagnostic settings remain Founder
gated. Missing/corrupt resources in external or test bundles return `unknown`;
normal scheme builds require the generated resource.

## Focused check without installation

From `Legend-ios`:

```sh
xcrun swiftc -parse-as-library -module-cache-path /tmp/legend-diagnostics-swift-cache \
  Legend/Core/LegendDiagnostics.swift Legend/Core/MobileHTTPClient.swift \
  Scripts/check-runtime-diagnostics.swift -o /tmp/legend-diagnostics-check
/tmp/legend-diagnostics-check
```

The check compiles the actual transport and queue, stubbing only unrelated UI
types and intercepting HTTP with an in-process URLProtocol. It verifies privacy,
persisted queue recreation, bearer transport, retry/cooldown bounds, and recursion
suppression. XCTest coverage also compiles in the unsigned `build-for-testing`
workflow. Physical-device crash behavior and production ingestion require separate
verification; no app signing, installation, or deployment is part of these checks.
