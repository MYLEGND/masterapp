# Security and Privacy

## Data classification

| Data | Classification | Native handling |
| --- | --- | --- |
| Access/refresh tokens | Secret | Keychain only; never logs, UserDefaults, backups, or screenshots |
| User/profile details | Sensitive personal data | in-memory where practical; redact in diagnostics |
| Messages and attachments | Sensitive communications | no content logging; protected local storage only when an offline feature is formally approved |
| Billing and payment metadata | Restricted financial data | server-rendered state only; no card data handling in app |
| Financial intelligence | Restricted financial data | no analytics export; redact values and account identifiers |
| Journey Circles data | Sensitive preference/relationship data | server-controlled visibility; no local matching authority |

## Authentication and token handling

- Use `ASWebAuthenticationSession` for system-browser Authorization Code + PKCE.
- Generate a fresh PKCE verifier and state for every sign-in attempt.
- Validate state before exchanging a callback code.
- Keep tokens in Keychain using `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`
  or stricter policy approved by product/security.
- Prefer refresh tokens only if the identity provider and backend contract
  explicitly permit them; otherwise require interactive reauthentication.
- Clear all token material and volatile feature state at logout.

## Networking

- Use `URLSession` and HTTPS with the platform trust store.
- Do not disable certificate validation or pin an unapproved certificate.
- Send bearer credentials only to the configured approved API origin.
- Reject redirects that would forward credentials to a different origin.
- Handle `401` by clearing/re-authenticating; handle `403` as an authorization
  decision, not a reason to retry with altered role data.
- Use correlation IDs from responses in user support diagnostics without
  exposing internal exception details.

## Diagnostic policy

`LegendDiagnostics` records only bounded, redacted operational events. It must
not include authorization headers, query bodies, message text, attachment names,
email addresses, phone numbers, finance values, or profile IDs. Production
telemetry integration is intentionally absent until retention, consent, and
data-processing terms are approved.

## Platform privacy

- App privacy nutrition labels must be derived from actual SDKs and data flows,
  not projected features.
- ATT is not requested unless an approved tracking use case exists.
- Push notification previews must be generic by default: no message body or
  counterparty identity on a locked screen without an explicit setting.
- Face ID / passcode app lock is an optional future enhancement and must not
  claim to replace server authentication or authorization.
