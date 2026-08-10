# MSAL uses reflection for account and token persistence.
-keep class com.microsoft.identity.** { *; }
-dontwarn com.microsoft.identity.**

# Kotlin serialization models are referenced by generated serializers.
-keepclassmembers class **$$serializer { <fields>; }
-keepclasseswithmembers class * { kotlinx.serialization.KSerializer serializer(...); }
