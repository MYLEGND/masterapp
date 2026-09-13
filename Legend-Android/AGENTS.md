# Android release artifact handoff

The user requires every final signed Android AAB to be placed at
`/Users/zacowen/MASTERAPP/Legend-Android/app/build/outputs/bundle/release/app-release.aab`.
Use this location for future bundle handoffs, including bundles downloaded from CI.
Verify the signed artifact and its embedded version code before replacing an older
generated bundle there, and give the user this final path rather than a temporary download path.
