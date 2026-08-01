# Build and Signing

## Repository-safe configuration

The checked-in `Configuration/*.xcconfig` files are value-free templates for
backend and identity configuration. They contain no backend URLs, tenant IDs,
client IDs, signing team IDs, or secrets. The product bundle identifier is a
non-secret build identity; the runtime always reads its final value from
`Bundle.main.bundleIdentifier`.

For local development, create the ignored
`Configuration/Legend.local.xcconfig` file and provide values there (or inject
them through CI build settings). Each checked-in environment configuration
optionally includes that file after its safe defaults, so local values take
effect without changing tracked source. Keep the same setting names:

- `LEGEND_API_BASE_URL`
- `LEGEND_AUTHORIZATION_ENDPOINT`
- `LEGEND_TOKEN_ENDPOINT`
- `LEGEND_AUTH_CLIENT_ID`
- `LEGEND_AUTH_REDIRECT_SCHEME`
- `LEGEND_AUTH_SCOPE`
- `LEGEND_AUTH_AUDIENCE`

An app with any missing identity/API setting remains intentionally
contract-unavailable. That behavior is a security feature.

## App icon

The AppIcon is generated only from the approved transparent AgentPortal artwork. Regenerate it after that source artwork changes:

```bash
cd Legend-ios
swift Scripts/generate-app-icon.swift
```

The generator removes transparent padding, centers the original shield at 73.5% visible coverage, and uses the dashboard navy background. It does not redraw or substitute the Legend artwork.

## Local build

1. Install full Xcode and choose it with `xcode-select`.
2. Open `Legend-ios/Legend.xcodeproj`.
3. Select the `Legend` scheme and an iOS 17+ simulator.
4. Supply local non-production configuration only through untracked settings.
5. Run unit tests and UI tests from Xcode’s Test action.

This workspace has full Xcode 26.6 and the iOS SDK. The app, unit-test, and
UI-test targets compile successfully with a local placeholder bundle identifier.
The native test suite runs on the available iPhone 17 Pro simulator; release
validation must still include a physical-device pass using approved,
non-production identity and mobile API configuration.

## Device, archive, and TestFlight procedure

1. Inject approved environment settings through the ignored local configuration
   or the CI secret store; do not edit the checked-in templates.
2. In Xcode, select the approved team and a device registered for the selected
   environment. Confirm the resulting bundle identifier and redirect URI match
   the approved native app registration.
3. Run the `Legend` scheme on that physical device and complete the system
   browser sign-in flow. Verify server-side scope, entitlement, and participant
   authorization responses rather than relying on the client UI.
4. Run the unit and UI tests on an installed simulator runtime or a dedicated
   device-test job. Run server contract tests only against staging.
5. Select **Any iOS Device (arm64)**, choose **Product > Archive**, and validate
   the archive in Xcode Organizer. Do not archive until the approved
   environment configuration has been deployed and the bearer-authenticated
   mobile API contract described in `API_CONTRACTS.md` has passed staging
   validation.
6. Upload the validated archive to the authorized App Store Connect/TestFlight
   account using the organization’s CI credentials or the approved release
   operator. Never store those credentials in this repository.

## Signing policy

- Bundle identifier and Apple team are selected per environment outside this
  repository.
- Automatic signing is suitable only for personal/local development after
  approved team selection.
- Distribution signing, provisioning profiles, App Store Connect API keys, and
  notarization credentials must remain in the organization’s secret manager or
  CI secure store.
- Never commit `.p12`, `.mobileprovision`, `.cer`, `.xcarchive`, `.ipa`, or
  provisioning-derived metadata.

## CI gate once Xcode is available

The release pipeline must at minimum run:

1. `xcodebuild build` for Debug configuration;
2. `xcodebuild test` for unit and UI test plans;
3. source-secret scanning;
4. dependency/license review;
5. archive validation with a non-production signing configuration;
6. server contract/integration tests against a staging environment only.

No deployment, production database migration, or App Store submission is part
of this foundation.
