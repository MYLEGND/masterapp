# App Store Readiness

## Current release status

**Not ready for TestFlight or App Store submission.** The app foundation exists,
but the required native authentication and server API contract has not been
implemented or approved.

## Required before TestFlight

- [ ] Approved native application registration, PKCE redirect URI, audience,
      delegated scopes, and consent text.
- [ ] Bearer-protected versioned mobile API with integration tests.
- [ ] Messaging end-to-end test using authenticated staging identities.
- [ ] Realtime or notification/reconciliation contract selected and tested.
- [ ] Account/profile, recipient search, attachment, and entitlement error
      handling validated with accessibility and localization review.
- [ ] Privacy policy, support URL, age rating, export compliance, and app
      privacy disclosures supplied by product/legal.
- [ ] Crash/diagnostic retention and consent policy approved.
- [ ] Full Xcode simulator/device build, unit tests, UI tests, and archive
      validation completed in CI.
- [ ] Accessibility tests: Dynamic Type, VoiceOver labels/hints, contrast,
      keyboard navigation, and reduced motion.
- [ ] Security review of token storage, redirects, logging, and backend scope
      enforcement.

## Required before App Store release

- [ ] TestFlight acceptance criteria met and no release-blocking defects.
- [ ] Production operational monitoring and support runbook approved.
- [ ] App review metadata accurately describes actual functionality.
- [ ] No debug endpoints, fake data, preview credentials, or bypass flags.
- [ ] Disaster recovery and logout/session revocation behavior tested.
