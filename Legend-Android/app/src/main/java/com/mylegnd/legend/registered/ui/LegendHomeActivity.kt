package com.mylegnd.legend.registered.ui

import android.Manifest
import android.content.ContentUris
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.provider.CalendarContract
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import com.mylegnd.legend.registered.core.design.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter

internal data class LegendCalendarEntry(val id: Long, val title: String, val start: Long, val end: Long, val allDay: Boolean) {
    fun occursOn(day: LocalDate, zone: ZoneId): Boolean {
        if (allDay) {
            val firstDay = Instant.ofEpochMilli(start).atZone(java.time.ZoneOffset.UTC).toLocalDate()
            val exclusiveEnd = Instant.ofEpochMilli(end).atZone(java.time.ZoneOffset.UTC).toLocalDate()
            return !day.isBefore(firstDay) && day.isBefore(exclusiveEnd)
        }
        return start < day.plusDays(1).atStartOfDay(zone).toInstant().toEpochMilli() && end > day.atStartOfDay(zone).toInstant().toEpochMilli()
    }
}

// CalendarContract is Android's device-planner authority, corresponding to EventKit on iOS.
// Only the connection preference is local; event contents remain in the calendar provider.
internal class LegendCalendarActivity(private val context: Context, accountKey: String) {
    private val preferences = context.getSharedPreferences("legend-planner-connections", Context.MODE_PRIVATE)
    private val key = "$accountKey:calendar"
    var connected by mutableStateOf(preferences.getBoolean(key, false)); private set
    var entries by mutableStateOf<List<LegendCalendarEntry>>(emptyList()); private set
    var failure by mutableStateOf<String?>(null); private set
    fun connect(value: Boolean) { connected = value; preferences.edit().putBoolean(key, value).apply(); if (!value) entries = emptyList() }
    suspend fun refresh() {
        failure = null
        if (!connected) { entries = emptyList(); return }
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.READ_CALENDAR) != PackageManager.PERMISSION_GRANTED) { connect(false); return }
        try {
            val loaded = withContext(Dispatchers.IO) {
                val zone = ZoneId.systemDefault()
                val day = LocalDate.now(zone)
                val from = day.atStartOfDay(zone).toInstant().toEpochMilli()
                val to = day.plusDays(1).atStartOfDay(zone).toInstant().toEpochMilli()
                val projection = arrayOf(CalendarContract.Instances.EVENT_ID, CalendarContract.Instances.TITLE, CalendarContract.Instances.BEGIN, CalendarContract.Instances.END, CalendarContract.Instances.ALL_DAY)
                buildList {
                    CalendarContract.Instances.query(context.contentResolver, projection, from - 86_400_000L, to + 86_400_000L).use { cursor ->
                        if (cursor == null) error("Calendar could not be read.")
                        while (cursor.moveToNext()) add(LegendCalendarEntry(cursor.getLong(0), cursor.getString(1).orEmpty(), cursor.getLong(2), cursor.getLong(3), cursor.getInt(4) == 1))
                    }
                }.filter { it.occursOn(day, zone) }.sortedWith(compareBy<LegendCalendarEntry> { !it.allDay }.thenBy { it.start })
            }
            entries = if (connected) loaded else emptyList()
        } catch (cancelled: kotlinx.coroutines.CancellationException) { throw cancelled }
        catch (_: Exception) { entries = emptyList(); failure = "Your calendar could not be loaded. Check calendar access and try again." }
    }
}

@Composable
internal fun rememberLegendCalendarActivity(context: Context, accountKey: String): LegendCalendarActivity {
    val planner = remember(accountKey) { LegendCalendarActivity(context, accountKey) }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    LaunchedEffect(lifecycle, planner) {
        lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            while (isActive) { planner.refresh(); delay(60_000) }
        }
    }
    return planner
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun LegendCalendarActivitySheet(planner: LegendCalendarActivity, dismiss: () -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    var actionFailure by remember { mutableStateOf<String?>(null) }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        planner.connect(granted)
        if (!granted) actionFailure = "Allow calendar access in Settings to connect your device calendar."
        scope.launch { planner.refresh() }
    }
    fun launch(intent: Intent) { try { context.startActivity(intent) } catch (_: android.content.ActivityNotFoundException) { actionFailure = "Install or enable a calendar app to manage events." } }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        LazyColumn(Modifier.fillMaxWidth(), contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            item {
                Surface(color = LegendColors.Navy, shape = LegendShapes.Control) {
                    Column(Modifier.fillMaxWidth().padding(20.dp)) {
                        Text(legendLocalized("YOUR LEGEND"), color = LegendColors.GoldBright, style = LegendTypography.Eyebrow)
                        Text(legendLocalized("Today's activity"), color = LegendColors.OnNavy, style = LegendTypography.Title)
                        Text(LocalDate.now().format(DateTimeFormatter.ofPattern("EEEE, MMMM d", LegendLocalizationRuntime.locale())), color = LegendColors.OnNavy, style = LegendTypography.Supporting)
                    }
                }
            }
            item {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = { launch(Intent(Intent.ACTION_INSERT, CalendarContract.Events.CONTENT_URI).putExtra(CalendarContract.Events.HAS_ALARM, true)) }, modifier = Modifier.weight(1f)) { Text(legendLocalized("Set reminder")) }
                    OutlinedButton(onClick = {
                        if (planner.connected) planner.connect(false)
                        else permission.launch(Manifest.permission.READ_CALENDAR)
                    }, modifier = Modifier.weight(1f)) { Text(legendLocalized(if (planner.connected) "Disconnect" else "Connect calendar")) }
                }
                Text(legendLocalized("Calendar events and their reminders stay in your connected device calendar."), style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
            }
            (actionFailure ?: planner.failure)?.let { message -> item { Text(legendLocalized(message), color = LegendColors.Error); TextButton(onClick = { scope.launch { planner.refresh() } }) { Text(legendLocalized("Retry")) } } }
            if (planner.entries.isEmpty()) item { Text(legendLocalized(if (planner.connected) "Your day is clear" else "Connect your calendar to see today's events and reminders."), color = LegendColors.TextSecondary) }
            items(planner.entries, key = { "${it.id}:${it.start}" }) { entry ->
                Card(onClick = { launch(Intent(Intent.ACTION_VIEW, ContentUris.withAppendedId(CalendarContract.Events.CONTENT_URI, entry.id)).putExtra(CalendarContract.EXTRA_EVENT_BEGIN_TIME, entry.start).putExtra(CalendarContract.EXTRA_EVENT_END_TIME, entry.end)) }, colors = CardDefaults.cardColors(containerColor = LegendColors.Navy), modifier = Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(16.dp)) {
                        Text(entry.title, color = LegendColors.OnNavy, style = LegendTypography.CardTitle)
                        Text(if (entry.allDay) legendLocalized("All day") else Instant.ofEpochMilli(entry.start).atZone(ZoneId.systemDefault()).format(DateTimeFormatter.ofPattern("h:mm a", LegendLocalizationRuntime.locale())), color = LegendColors.GoldBright)
                    }
                }
            }
            item { TextButton(onClick = dismiss) { Text(legendLocalized("Done")) } }
        }
    }
}
