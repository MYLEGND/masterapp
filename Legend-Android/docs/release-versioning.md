# Android release version codes

The existing app Gradle configuration allocates release version codes automatically.
Run `./release` from Legend-Android, or `~/MASTERAPP/Legend-Android/release`
from any directory. This shortcut invokes the existing `./gradlew bundleRelease`;
it does not add another signing or versioning implementation. A routine release
does not need `clean` or `signingReport`. The original Gradle commands still work.
The displayed version name remains independent of the Play upload version code.

The Mac's `legend-release` terminal shortcut invokes `scripts/release-mobile.sh`:
with no argument it builds Android and then archives iOS. Use `legend-release android`
or `legend-release ios` to build only one. The shortcut advances the Xcode project build number beyond its current value and
existing LEGEND archives on this Mac, keeping all targets aligned and binding the
archive to the reserved number. Existing signing is reused; each archive has a unique
path under Xcode's dated Archives folder. Store uploads remain separate. The helper
stops on a build failure, preserving any earlier successful output.

The allocator advances a locked persisted reservation by exactly one per release
build. Wall-clock timestamps are never used. The reservation remains in
`$GRADLE_USER_HOME/legend-release/com.mylegnd.legend.registered.version-code`
(default `~/.gradle`). `clean` does not erase it, failed builds may consume a code,
and Gradle configuration-cache reuse still checks the reservation. Missing,
malformed or exhausted state fails explicitly; it never guesses a starting code.

The migration from timestamp-based codes requires confirming the highest version
actually distributed through every Play track before changing the saved reservation.
Do not lower the reservation merely because a draft was rejected. A fresh CI host
must restore verified version history through its release authority before building;
this local counter does not coordinate independent hosts or query Google Play.

Signing still requires the existing local signing environment or the existing CI
signing step. A successful unsigned build is not a signed upload artifact.
Final verified signed bundles belong at:
`app/build/outputs/bundle/release/app-release.aab`.

## One-time macOS signing setup

Run `swift tools/setup-release-signing.swift` from Legend-Android. Existing
`LEGEND_STORE_PASSWORD` and `LEGEND_KEY_PASSWORD` values are used if loaded;
otherwise secure macOS dialogs request them locally. A disposable JAR verifies
both passwords against `Legend.jks` and alias `legend-upload-2026` before saving
Keychain generic-password entries under service
`com.mylegnd.legend.android.release`. Passwords never appear in script arguments,
project files, or setup output. Cancelling stops setup. Existing entries are
updated without changing the upload key.

Subsequent local release builds retrieve these credentials automatically. macOS
can require unlocking the login Keychain or approving access to the entries.
Environment values still work; the GitHub Actions workflow retains its existing
separate signing step. Local bundle/assemble/package release commands fail early
when signing is unavailable. Never upload an old bundle after a failed build.

## Play foreground-service declaration

The manifest declares phoneCall, microphone, camera and mediaProjection for
LegendCallForegroundService. Complete Play Console App content > Foreground
service permissions with the actual calling/video/screen-sharing use cases and
the requested demonstration. Building or signing does not submit this declaration.
Do not remove required calling permissions just to suppress the Console error.

On 2026-09-11 the owner confirmed version 7 as the released Android baseline.
The Mac timestamp reservation was preserved in a migration receipt and changed
once to 7 so the corrected build reserves 8. This is a one-time recovery, not an
automatic reset rule. Future builds continue from the persisted reservation.
