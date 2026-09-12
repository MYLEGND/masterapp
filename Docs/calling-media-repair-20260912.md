# Calling media repair — 2026-09-12

Status: candidate in progress; no production release or native distribution verified.

## Reproduction and evidence

- User reports THE C.E.O physical iPhone calling the Xcode iOS simulator fails. This is not a two-physical-iPhone test. The booted iPhone 17 Pro Max simulator currently has build 32 installed; the exact build used at the time of failure remains to be correlated.
- Android emulator to physical iPhone reached the recipient and displayed Connected. User reports no audible speech after answering. Ringing and a connected ICE transport do not establish working two-way audio.
- Android-to-Android has not been reproduced; the user's suspicion is not a confirmed defect.
- User screenshots demonstrate navy framing around iOS video, unlike Android's full-screen camera presentation, and unreadable screen sharing.

## Confirmed code and service observations

1. iOS stores configure a process-wide WebRTC audio session. Per-store teardown could disable that session without proving ownership of the current CallKit call. The existing CallKit authority must own activation and teardown; this is a demonstrated lifecycle defect, not yet the proven cause of the reported silence.
2. Screen capture inherited camera adaptation constraints, including the lowest 320×180 profile. Screen sharing needs its own profiles in the existing server call policy, consumed by both native implementations. Camera fill and uncropped screen presentation have different requirements.
3. Production Application Insights operation `6759983ac904f9c17291e6908a00b6e4` at 2026-09-12 05:54:43 UTC failed after approximately 3.829 seconds with SQL deadlock victim 1205 in the call save/publish path. This proves a signaling failure, not a particular device-direction or audio failure. Epoch changes and the associated signal must commit atomically before a bounded deadlock retry can be considered safe.
4. Both Azure hosts expose no `Calling:*` app settings and resource group `masterapp-rg` exposes no VM relay. These observations alone do not prove every possible runtime configuration source is absent. No relay has been provisioned by this repair.

## Central authority and verification boundaries

`SHARED/Calling/LegendCallingContracts.cs`, `SHARED/Messaging/MessagingHub.cs`, and `Infrastructure/Messaging/MessagingService.Calling.cs` remain the shared policy, actor authorization, and signaling authorities. Swift and Kotlin retain their platform media implementations. The repository documents native calling; there is no browser WebRTC calling interface to claim as tested parity.

Required acceptance includes physical iPhone ↔ Android emulator, physical iPhone → iOS simulator, and additional directions where distinct signed-in endpoints are available. For each run record ringing, answer, inbound/outbound audio packet progression, audible speech in both directions, video, and readable shared text separately. Include mute, speaker route, account teardown, sharing stop/restart, and failure behavior. Simulator evidence cannot establish all physical-device/network combinations.

Native build/automated results and live results must be appended after execution. Do not treat this document, mocked tests, Connected labels, or pending builds as acceptance evidence.

## Executed candidate checks

- Shared backend commit `62b7165b`: 31 focused calling tests passed, including save rollback, bounded deadlock recovery, no replay after a committed save, authorization, and presentation-state validation.
- iOS commit `bdbe4720`: 10 calling tests passed on the separate LegendCreatorVerification simulator. Includes native peer negotiation and lifecycle assertions; does not establish audible physical-device audio.
- Android commit `d52e25c8`: 57 JVM tests passed. Debug APK installed in place on emulator-5554 with application data preserved; no live call established by these tests.
- First combined .NET run was interrupted because isolated output paths prevented source-contract tests from locating the repository. The rerun uses their existing `GITHUB_WORKSPACE` setting; no assertions changed.
- First development iOS installation omitted the existing ignored `Legend.local.xcconfig`. Launch inspection showed Configuration Required, so that installation is explicitly excluded from live validation. Corrected packaging must reuse the existing configuration and pass launch validation before the call test resumes.
- The first completed combined run executed 2,545 cases: 2,524 passed, 17 failed, four skipped. Investigation found two pre-existing tests mutating process-global Founder identity without the existing nonparallel Founder collection. TRX timestamps show their execution overlapping for approximately 63 ms, followed by additional Founder authorization failures. The harness correction uses that existing collection and moves setup inside existing `try/finally` protection; assertions, prompts, expected results, and thresholds remain unchanged. A fresh full run is required before comparing the accepted failure baseline.
- After configuration was restored, the physical phone signed in, but the simulator reported “Secure session storage could not be updated.” This recipient setup failure blocks calling validation. Signing/Keychain entitlement inspection is required; no credentials are cleared and no authorization bypass is permitted.
- Simulator security logs then confirmed OSStatus `-34018`: the unsigned test artifact lacked application-identity/Keychain entitlements. Rebuilding with normal Xcode simulator signing restored the identity entitlements. In-place installation retained the sign-in session; screenshot verification shows the Agent/Client role chooser with no storage error. No authentication code or Keychain records were changed. The corrected simulator artifact supersedes both earlier test artifacts.

No production deployment, App Store upload, Android release bundle replacement, or release version increment occurred in this repair.

## Combined regression result

After the two-test environment-isolation correction (`b7400639`), the full suite executed 2,545 cases: **2,532 passed, nine failed, four skipped**. Failed/skipped identities and failure-message hashes exactly match the previously recorded `4cc5d30a` baseline. The raw test result remains failed because those nine failures remain; no release gate was bypassed. Result: `/private/tmp/legend-calling-environment-verified/full.trx`.

## Live iOS simulator reproduction

The user reproduced immediate failure in both directions between THE C.E.O's agent profile and the simulator's client profile after both were authenticated. At 2026-09-11 23:43:53.644 Phoenix time, simulator `callservicesd` failed to launch its system incoming-call interface with `LSApplicationWorkspaceErrorDomain 115`; it then generated generic call failure 55 and `CXEndCallAction`. At .647 the app sent decline. This precedes media establishment and is not evidence of a speech/audio failure or of universal physical-iPhone-to-iPhone failure. The deadline hypothesis was rejected: the CallKit failure occurred before the ringing deadline was armed, and server HTTP date and host UTC aligned.

Apple's [VoIP calling with CallKit sample](https://developer.apple.com/documentation/callkit/voip-calling-with-callkit) requires a physical device. No simulator-only alternate call path was added. The failing receiving simulator app was stopped without clearing its data before preparing the physical-iPhone/Android-emulator audio test, to prevent its system rejection from ending the same recipient's call.

## Physical iPhone → Android emulator retest

Android was confirmed signed into the client profile, distinct from the physical phone's agent profile. The incoming call appeared and was answered. Before media started, the candidate reported that Android could not grant call audio. No RTP audio observations were recorded; this test does not validate speech. The existing implementation requested a second AudioManager focus owner before Telecom activation/foreground service readiness. The repair must use the existing self-managed ConnectionService focus lifecycle on supported API versions, preserving bounded failure handling and legacy compatibility. No successful media result is recorded yet.

## Consecutive-call and continuing-ringtone follow-up

Android `9dd00521` passed 63 JVM tests and was installed with data preserved. On September 12, 00:19:49–00:20:04 Phoenix time, six samples showed inbound/outbound RTP packet progression with local audio enabled, focus granted, and speaker routing. These samples belong only to that interval; the initial attribution to a later call was corrected. They do not prove audible speech. A later attempt started its foreground service at 00:21:31 and reported audio-readiness timeout. The earlier logger did not isolate which prerequisite failed on that attempt.

The user reports that the receiving device continues ringing after pickup. Android source demonstrated two related lifecycle defects: the audio gate discarded service-owned focus between calls although Telecom can retain that grant; and the insistent incoming notification could persist or be reposted by a late avatar fetch after answering. Commit `58f48a96` retains focus only for the same actual service, records loss between calls, rejects retired-service callbacks, cancels the incoming notification on answer, and requires the exact still-incoming notification identity before an avatar update can repost. No focus denial is ignored and the readiness deadline is unchanged. A process-local media-session ordinal now distinguishes each attempt's lifecycle and packet evidence without account or call identifiers.

Android verification: 65 tests passed, zero failed/skipped; independent review found no concrete blocker. The signed debug APK was installed in place on emulator-5554 with login preserved. Its SHA-256 is `f74bfd8288c47c549d3317500a937310b876754b6074712202ac11b08b2b9495`. Live ringtone-stop and two-way speech verification remain pending.

iOS review found that CallKit answer fulfillment follows permissions, peer preparation, and server acceptance. Peer preparation does not await audio activation, so no circular activation dependency was demonstrated. Source inspection alone does not establish continued ringing after successful CallKit fulfillment; that receiving direction still needs its own live observation. Existing iOS retirement fixes through `eb6f7fdf` passed 15 calling tests and are installed on THE C.E.O. No speculative fulfillment bypass was introduced.

### Live test of Android 58f48a96, session 1

Physical agent → Android client, September 12 at 07:32 UTC: the user confirmed **the receiving ringtone stopped**, then reported **could not establish a direct call**. Android process 25307, mediaSession 1 recorded foreground/audio-focus readiness. At 00:32:36.308 Phoenix time, one sample recorded 150 outbound audio packets and zero inbound audio packets. The UI then showed Reconnecting and the call ended at 00:33:10 without an agent-initiated hangup. No successful two-way audio is established. The ringing symptom is verified resolved only for this tested receiving direction; media transport remains an open gate. Missing TURN alone is not yet proven the cause of this specific attempt.

## Latest transport and audible verification

Android `77c8423a` passed 67 tests; `76a94dc6` added candidate inventory and termination-origin diagnostics and passed 69 tests. iOS `83909c83` added DEBUG-only safe console state; signed build succeeded and was installed with existing account data. Historical physical log collection was blocked by administrator access (`log collect` requires root; `sudo -n` requires a password), so the development console was used instead. No credential or authorization change was made.

At 07:49:53 UTC Android process 26837/session 1 reached ICE connected with DTLS connected and a selected TCP peer-reflexive→host pair. Five samples showed audio packets in both directions. The user explicitly reported **no audible speech**. At 07:51:02 the first recovery trigger was ICE disconnection; recovery failed and local cleanup occurred at 07:51:34. Packet progression does not close the audio gate. During the emulator audio audit, the host microphone enable command was invoked; input is now enabled, but its prior state was not observed. This could affect emulator capture and does not explain all reported silence or transport loss. A new audible test with the effective microphone setting remains necessary.

## Adjacent requested repairs

- `180697b6`: HAC upload accepted with HTTP 202 but worker failed to launch FFmpeg. Production Windows package contains `ffmpeg.exe`; lookup incorrectly searched the unsuffixed name. Packaged Windows resolution repaired without changing explicit paths, Unix behavior, duration limits, or working post/story lifecycle. 19 focused tests passed. Actual Windows execution remains the existing CI/package and post-deployment check.
- `750140fc`: member-facing ClientApp wording, 19 files; compile passed without warnings/errors. Technical identities and agent-only behavior retained.
- `ad89d76a`: Android chat presentation plus three iOS member-facing labels. Concurrent agent staging resulted in these related changes sharing one commit; scope inspected, no work discarded. Android compile passed.
- `39056cae`: iOS/web shared-content reaction placement, compact author/original pills, custom iOS reaction popover, and localized member scope wording. iOS signed development build passed; 40 web messaging tests passed. Test DOM gained selector/classList support for the actual DOM operation and a new ownership assertion; prior assertions retained. Device visual acceptance remains pending.

All changes remain an unreleased candidate. Calling audibility and recovery, remaining physical-device directions, Windows HAC processing, UI visual review, and combined protected release verification remain open as applicable. No production-readiness claim is made.
