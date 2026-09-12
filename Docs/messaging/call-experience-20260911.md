# Call experience repair candidate

Base: production b3eeef29 (PR #120). This candidate is separate from that release.

Observed: the first recorded outgoing Android call received a device acknowledgement, then showed direct connection unavailable. The subsequent user screenshots show Connected and live video, followed by recursive call UI in shared content and colored video corruption. These are different observations; successful sustained media is not yet verified.

Changes in the existing authorities:
- Native call controls use a right-side icon rail, preserve localized accessibility labels, remove the displayed name, and retain centered hang-up.
- Android screen sharing minimizes its full call overlay, matching the existing iOS behavior. Capture sizing follows the existing shared media limits and preserves even dimensions and aspect ratio. Android handles captured-region resize; receivers fit the full video frame.
- Existing shared incoming and ringback WAVs are replaced by an original generated glass-chime motif. One reproducible generator owns both source assets. Both native packages already consume these shared resources.
- Existing NotificationEngine image capabilities now support call sender photos, tied to the recipient, live call and conversation access. Android reuses the existing message avatar downloader. iOS donates an incoming call intent with the server-issued image URL after reporting to CallKit, without delaying required PushKit reporting.

Validation: initial Android and iOS simulator builds passed. Server build passed with zero warnings/errors. Nine focused notification/capability tests passed, including call expiry, tampering, recipient-type mismatch and revoked access. Android debug package after capture changes built and was installed preserving sign-in. Application copy generator retains 5,219 entries. Final iOS device build/installation and live photo, ringtone, video and screen-sharing verification remain pending. THE C.E.O became unavailable to the Mac during preparation.

Unresolved: colored video corruption has not been reproduced with codec/frame diagnostics; capture geometry repairs are not proof of its cause. Message-avatar absence is not yet attributed: aggregate production telemetry returned no sender-image requests during the inspected two-hour window. Browser calling implementation was not located; no browser call parity is claimed. Native direct-only ICE policy has no relay; successful ringing does not prove media connectivity on every network. No additional release or known-failure waiver is included in this candidate.

Additional validation: the signed generic-device iOS build succeeded, but physical installation remains blocked by the disconnected phone. The first full server run omitted GITHUB_WORKSPACE; source-contract tests could not locate the checkout because build outputs live under /tmp/masterapp. Preserve this run as an invalid runner setup, then rerun with the existing GITHUB_WORKSPACE input set to the candidate. No harness code or assertions are changed.
