package com.mylegnd.legend.registered

import android.app.Activity
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.*
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.mylegnd.legend.registered.core.design.LegendTheme
import com.mylegnd.legend.registered.core.session.SessionViewModel
import com.mylegnd.legend.registered.ui.LegendRoot

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) { super.onCreate(savedInstanceState); val container = (application as LegendApplication).container
        container.notificationNavigation.capture(intent)
        setContent { LegendTheme { val session: SessionViewModel = viewModel(factory = LegendViewModelFactory { SessionViewModel(container.sessionRepository) }); LaunchedEffect(Unit) { session.restore() }; LegendRoot(session, container) } }
    }
    override fun onNewIntent(intent: android.content.Intent) { super.onNewIntent(intent); setIntent(intent); (application as LegendApplication).container.notificationNavigation.capture(intent) }
}
class LegendViewModelFactory<T : ViewModel>(private val creator: () -> T) : ViewModelProvider.Factory { @Suppress("UNCHECKED_CAST") override fun <R : ViewModel> create(modelClass: Class<R>): R = creator() as R }
