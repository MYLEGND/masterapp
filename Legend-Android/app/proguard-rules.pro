# MSAL uses reflection for account and token persistence.
-keep class com.microsoft.identity.** { *; }
-dontwarn com.microsoft.identity.**

# Kotlin serialization models are referenced by generated serializers.
-keepclassmembers class **$$serializer { <fields>; }
-keepclasseswithmembers class * { kotlinx.serialization.KSerializer serializer(...); }
-dontwarn com.google.crypto.tink.subtle.Ed25519Sign$KeyPair
-dontwarn com.google.crypto.tink.subtle.Ed25519Sign
-dontwarn com.google.crypto.tink.subtle.Ed25519Verify
-dontwarn com.google.crypto.tink.subtle.X25519
-dontwarn com.google.crypto.tink.subtle.XChaCha20Poly1305
