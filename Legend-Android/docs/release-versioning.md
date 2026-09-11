# Android release version codes

The existing app Gradle configuration allocates release version codes automatically.
Continue using `./gradlew clean bundleRelease` (or `./gradlew bundleRelease`).
The displayed version name remains independent of the Play upload version code.

The allocator uses UTC seconds since 2020, bounded by Google Play's 2,100,000,000
limit, and a locked high-water mark in
`$GRADLE_USER_HOME/legend-release/com.mylegnd.legend.registered.version-code`
(default `~/.gradle`). Checkouts sharing that Gradle user home share the reservation.
`clean` does not erase it. Failed builds can consume codes; gaps are intentional.
Malformed reservation state fails the build instead of resetting the counter.
A Gradle ValueSource rechecks the reservation on configuration-cache reuse.
Non-release tasks do not allocate codes. The existing CI Gradle command uses the
same allocator once this change reaches its checked-out branch; it needs no separate
version override. CI continues using its existing serialized release workflow and
signing step.

This is build-time allocation, not a query of Google Play. Keep build-host clocks
correct and upload the newest build. Independently built artifacts on different
hosts within the same second are not globally coordinated. Re-uploading an old
bundle can still be rejected; rebuilding generates a fresh code.

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
