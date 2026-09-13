# Typed-profile call confirmation repair

## Production reproduction

Production base: `33aa8a7cbd485935c678c384d38599ef40dff232`.
Devices: THE C.E.O physical iPhone and Android emulator `emulator-5554`.
The first recording targeted an unintended iOS simulator and is not valid evidence
of physical-device delivery. LEGEND was then explicitly launched on THE C.E.O and
closed on the iOS simulators. Android received the next incoming-call event but
reported that incoming presentation could not be confirmed.

A debug-only trace of the existing SignalR transport reproduced the receipt
command rejection in 287 ms: `Only the recipient can confirm call delivery.`
This rules out the 12-second transport deadline for that attempt. No access token,
SDP, ICE candidate, participant identifier or raw wire frame was logged. The
instrumentation was removed after capturing the observation. Recordings and local
logs remain outside the committed evidence and do not constitute successful calls.

## First incorrect decision and repair

`AgentPortalMessagingActorContextResolver.ResolveAsync` compared the JWT claims
identity's `AuthenticationType` with the registered ASP.NET bearer scheme name.
Those identifiers differ under the existing JWT validation configuration. It
therefore skipped `IMobileActorResolver` and returned the Agent identity from
`EffectiveAgentContext`, even when the mobile account selected its Client profile.
For profiles sharing an Entra object ID, that made a receiver's `received` command
look like the caller's command to `MessagingService.ExecuteAsync`. Its recipient
check correctly rejected the command and must not be removed.

The existing resolver now obtains the validated bearer authentication ticket,
applies the existing mobile authorization policy, and passes its principal and
requested participant type to `IMobileActorResolver`. Failed authentication,
insufficient scope and rejected profile selection cannot fall through to an Agent.
Browser requests retain the existing effective-Agent authority after successful
cookie authentication. ClientApp's existing web Client resolver is unchanged.
Both native clients consume this same hub authority; no alternate call route,
identity store, provider fallback or platform-specific authorization exception is
introduced.

## Verification and limits

Six new authenticated actor-binding tests use real JWT validation plus the
registered-handler ticket boundary: five fail before the repair and all six pass
afterward. Existing authentication and calling regressions pass (56 focused cases).
An additional real calling-authority regression covers one owner with both Agent
and Client profiles, rejects the caller receipt, accepts the recipient receipt,
and permits recipient acceptance without weakening device binding (18 focused
calling/binding cases including that new regression).

The complete suite and protected release decision must be recorded for the exact
candidate. The previously authorized nine native failures and four skipped tests
are not repaired by this change and must not be silently waived for a new source.
After deployment, reconnect both native apps and repeat the physical-device call.
Successful receipt, two-way media, background operation and screen sharing remain
unverified until that live test completes. No successful Play demonstration is
claimed by these code or unit-test results.
