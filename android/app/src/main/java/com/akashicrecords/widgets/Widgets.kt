package com.akashicrecords.widgets

import android.content.Context
import android.content.Intent
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.action.ActionParameters
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetManager
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.appwidget.state.getAppWidgetState
import androidx.glance.appwidget.state.updateAppWidgetState
import androidx.glance.background
import androidx.glance.currentState
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.ColumnScope
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.padding
import androidx.glance.state.GlanceStateDefinition
import androidx.glance.state.PreferencesGlanceStateDefinition
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import androidx.glance.unit.ColorProvider
import com.akashicrecords.Launch
import com.akashicrecords.MainActivity
import com.akashicrecords.calendar.Reminders
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.time.LocalDate

// Home-screen widgets: put them on the first page of the launcher and a swipe to that page shows
// Akashic (Android does not let apps take over the left-most "feed" page itself). Same dark
// palette as the app; each widget opens the matching section.
internal object W {
    val bg = ColorProvider(Color(0xF216162A))
    val text = ColorProvider(Color(0xFFECECF4))
    val muted = ColorProvider(Color(0xFFA0A0BD))
    val faint = ColorProvider(Color(0xFF6F6F8C))
    val accent = ColorProvider(Color(0xFF5B8CFF))

    fun open(context: Context, section: String, tab: String? = null) =
        Intent(context, MainActivity::class.java).apply {
            action = Intent.ACTION_VIEW
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP
            putExtra(Reminders.EXTRA_SECTION, section)
            if (tab != null) putExtra(Launch.EXTRA_TAB, tab)
        }
}

// Per-widget state (Glance preferences): a refresh tick, and the ↻ count of the day.
internal val TICK = longPreferencesKey("tick")
internal val SHIFT = intPreferencesKey("shift")
internal val SHIFT_DAY = stringPreferencesKey("shiftDay")

private fun shiftOf(prefs: Preferences?): Int =
    if (prefs?.get(SHIFT_DAY) == LocalDate.now().toString()) prefs[SHIFT] ?: 0 else 0

// Base of every Akashic widget. Data is (re)loaded inside the composition, keyed on the widget
// state: a state change (↻, a refresh, the player) re-renders with fresh data. Loading it once
// before provideContent — the first version — left widgets frozen while their session lived.
abstract class AkashicWidget<D> : GlanceAppWidget() {
    override val stateDefinition: GlanceStateDefinition<*> = PreferencesGlanceStateDefinition

    abstract suspend fun load(context: Context, shift: Int): D

    @Composable
    abstract fun Content(context: Context, data: D)

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val prefs = runCatching { getAppWidgetState(context, PreferencesGlanceStateDefinition, id) }.getOrNull()
        val initial = withContext(Dispatchers.IO) { load(context, shiftOf(prefs)) }
        provideContent {
            val tick = currentState(TICK) ?: 0L
            val shift = shiftOf(currentState<Preferences>())
            val data by produceState(initial, tick, shift) {
                value = withContext(Dispatchers.IO) { load(context, shift) }
            }
            Content(context, data)
        }
    }
}

@Composable
internal fun WidgetCard(modifier: GlanceModifier = GlanceModifier, content: @Composable ColumnScope.() -> Unit) {
    Box(GlanceModifier.fillMaxSize().background(W.bg).cornerRadius(20.dp).then(modifier)) {
        Column(GlanceModifier.fillMaxSize().padding(horizontal = 14.dp, vertical = 12.dp), content = content)
    }
}

@Composable
internal fun WidgetHeader(text: String) {
    Text(text, style = TextStyle(color = W.accent, fontSize = 12.sp, fontWeight = FontWeight.Bold))
}

// Day-stable pick, shifted by the ↻ count; heavier items come up more often.
internal fun <T> pickOfDay(items: List<T>, shift: Int, weight: (T) -> Int = { 1 }): T? {
    if (items.isEmpty()) return null
    val pool = items.flatMap { item -> List(weight(item).coerceAtLeast(1)) { item } }
    // Distinct picks while shifting: step through the pool, skipping repeats of the same item.
    val start = Math.floorMod(LocalDate.now().toEpochDay().toInt() * 31, pool.size)
    val order = (pool.indices).map { pool[(start + it) % pool.size] }.distinct()
    return order[Math.floorMod(shift, order.size)]
}

// Re-renders widgets with fresh data (after edits, on leaving the app, from the reminder worker,
// from the player).
object Widgets {
    private suspend fun <W : GlanceAppWidget> bump(context: Context, cls: Class<W>, widget: W) {
        runCatching {
            GlanceAppWidgetManager(context).getGlanceIds(cls).forEach { id ->
                updateAppWidgetState(context, id) { it[TICK] = System.nanoTime() }
                widget.update(context, id)
            }
        }
    }

    suspend fun refreshMusic(context: Context) = bump(context, MusicWidget::class.java, MusicWidget())

    suspend fun refreshAll(context: Context) {
        bump(context, AgendaWidget::class.java, AgendaWidget())
        bump(context, JournalWidget::class.java, JournalWidget())
        refreshMusic(context)
        bump(context, PoemWidget::class.java, PoemWidget())
        bump(context, RecipeWidget::class.java, RecipeWidget())
        bump(context, WatchlistWidget::class.java, WatchlistWidget())
        bump(context, OrganisationWidget::class.java, OrganisationWidget())
        bump(context, PhotoWidget::class.java, PhotoWidget())
    }
}

internal val WIDGET_KEY = ActionParameters.Key<String>("widget")

// ↻ on a "du jour" widget: next item for today, on that widget only.
class ShuffleAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) {
        updateAppWidgetState(context, glanceId) { prefs ->
            val today = LocalDate.now().toString()
            val current = if (prefs[SHIFT_DAY] == today) prefs[SHIFT] ?: 0 else 0
            prefs[SHIFT] = current + 1
            prefs[SHIFT_DAY] = today
        }
        when (parameters[WIDGET_KEY]) {
            "poem" -> PoemWidget().update(context, glanceId)
            "recipe" -> RecipeWidget().update(context, glanceId)
            "watch" -> WatchlistWidget().update(context, glanceId)
            "photo" -> PhotoWidget().update(context, glanceId)
        }
    }
}
