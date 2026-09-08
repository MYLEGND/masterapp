# Google Play application signing certificate

`play-app-signing.pem` is the public application signing certificate extracted
from the Play-signed version 3 APK. It contains no private key.

The release build derives its MSAL redirect URI, runtime configuration, and
manifest callback path from this certificate. The upload keystore signs bundles
submitted to Play; it does not identify the installed Play application.

Certificate SHA-256:
`8CA73A5A3ABEAA78F7C056C24389F22F885427B338C0A570AB140B46593A3552`

The corresponding Entra public-client redirect is:
`msauth://com.mylegnd.legend.registered/ScCQQUZgab6Wz7dOxoQ5rEXDnrA%3D`

If Play app signing is changed, verify the new installed APK certificate and
update this public certificate and the Entra registration together. Preserve
callbacks needed by existing installations and development builds.

Version 3 shipped with a different signing hash in its MSAL configuration and
fails before the credential screen. An Entra change alone cannot repair that
installed binary: build and upload version 4 or newer with the corrected resources.
