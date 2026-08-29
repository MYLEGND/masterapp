package com.mylegnd.legend.registered.core.security

import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import kotlin.coroutines.resume
import kotlinx.coroutines.suspendCancellableCoroutine

/**
 * Mandatory device-owner verification for financial reporting. Android's
 * system prompt uses enrolled strong biometrics first and exposes the device
 * PIN, pattern, or password when biometrics are unavailable or declined.
 */
enum class FinancialReportingAccessResult {
    Granted,
    Denied,
    Unavailable,
}

interface FinancialReportingAccessAuthenticating {
    suspend fun authenticate(activity: FragmentActivity): FinancialReportingAccessResult
}

class FinancialReportingAccessAuthenticator : FinancialReportingAccessAuthenticating {
    override suspend fun authenticate(
        activity: FragmentActivity,
    ): FinancialReportingAccessResult {
        if (BiometricManager.from(activity).canAuthenticate(AUTHENTICATORS) != BiometricManager.BIOMETRIC_SUCCESS) {
            return FinancialReportingAccessResult.Unavailable
        }

        return suspendCancellableCoroutine { continuation ->
            val prompt = BiometricPrompt(
                activity,
                ContextCompat.getMainExecutor(activity),
                object : BiometricPrompt.AuthenticationCallback() {
                    override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                        if (continuation.isActive) {
                            continuation.resume(FinancialReportingAccessResult.Granted)
                        }
                    }

                    override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
                        if (continuation.isActive) {
                            continuation.resume(FinancialReportingAccessResult.Denied)
                        }
                    }
                },
            )
            continuation.invokeOnCancellation { prompt.cancelAuthentication() }
            prompt.authenticate(
                BiometricPrompt.PromptInfo.Builder()
                    .setTitle("Unlock financial reporting")
                    .setSubtitle("Confirm your identity to view financial information.")
                    .setAllowedAuthenticators(AUTHENTICATORS)
                    .build(),
            )
        }
    }

    private companion object {
        const val AUTHENTICATORS =
            BiometricManager.Authenticators.BIOMETRIC_STRONG or
                BiometricManager.Authenticators.DEVICE_CREDENTIAL
    }
}
