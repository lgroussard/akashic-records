package com.akashicrecords.widgets

import android.content.ComponentName
import android.content.Context
import android.os.Bundle
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.action.ActionParameters
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.cornerRadius
import androidx.glance.background
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.Row
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionCommand
import androidx.media3.session.SessionToken
import com.akashicrecords.R
import com.akashicrecords.music.PlaybackService
import com.google.common.util.concurrent.MoreExecutors
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.coroutines.resume

// What the playback service last played, for the widget (survives the app UI being closed).
object NowPlaying {
    private const val PREFS = "akashic_nowplaying"

    data class State(val title: String, val artist: String, val playing: Boolean, val suggestion: Boolean, val saved: Boolean)

    fun write(context: Context, s: State) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString("title", s.title).putString("artist", s.artist)
            .putBoolean("playing", s.playing).putBoolean("suggestion", s.suggestion).putBoolean("saved", s.saved)
            .apply()
    }

    fun read(context: Context): State? {
        val p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val title = p.getString("title", null)?.takeIf { it.isNotBlank() } ?: return null
        return State(title, p.getString("artist", "") ?: "", p.getBoolean("playing", false), p.getBoolean("suggestion", false), p.getBoolean("saved", false))
    }
}

// One row, fits 4×1: title/artist (tap = open Musique), then ⏮ ⏯ ⏭ and ⤓ on a suggestion.
// The service refreshes it on every track / play-state / download change.
class MusicWidget : AkashicWidget<NowPlaying.State?>() {
    override suspend fun load(context: Context, shift: Int) = NowPlaying.read(context)

    @Composable
    override fun Content(context: Context, data: NowPlaying.State?) {
        Box(GlanceModifier.fillMaxSize().background(W.bg).cornerRadius(20.dp)) {
            Row(GlanceModifier.fillMaxSize().padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(GlanceModifier.defaultWeight().clickable(actionStartActivity(W.open(context, "Musique")))) {
                    Text(data?.title ?: "Rien en lecture", style = TextStyle(color = W.text, fontSize = 14.sp, fontWeight = FontWeight.Bold), maxLines = 1)
                    Text(
                        data?.artist?.takeIf { it.isNotBlank() } ?: "Akashic · Musique",
                        style = TextStyle(color = W.muted, fontSize = 12.sp),
                        maxLines = 1,
                    )
                }
                Button(R.drawable.ic_prev, "Précédent", actionRunCallback<PrevAction>())
                Button(if (data?.playing == true) R.drawable.ic_pause else R.drawable.ic_play, "Lecture", actionRunCallback<PlayPauseAction>())
                Button(R.drawable.ic_next, "Suivant", actionRunCallback<NextAction>())
                if (data?.suggestion == true) {
                    Button(if (data.saved) R.drawable.ic_downloaded else R.drawable.ic_download, "Télécharger", actionRunCallback<DownloadAction>())
                }
            }
        }
    }

    @Composable
    private fun Button(icon: Int, label: String, action: androidx.glance.action.Action) {
        Image(ImageProvider(icon), contentDescription = label, modifier = GlanceModifier.size(44.dp).padding(10.dp).clickable(action))
    }
}

class MusicWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = MusicWidget()
}

// Widget buttons drive the PlaybackService through a short-lived MediaController. The command
// is sent asynchronously: the controller is kept a moment before release, otherwise the release
// can cut the command off (the first version's ⏭ that "did nothing").
// The callback's context is a receiver-restricted one that may not bind to services (the cause of
// the dead buttons): the application context is used instead.
private suspend fun withController(callbackContext: Context, block: (MediaController) -> Unit): Unit = withContext(Dispatchers.Main) {
    val context = callbackContext.applicationContext
    val future = MediaController.Builder(context, SessionToken(context, ComponentName(context, PlaybackService::class.java))).buildAsync()
    val controller = withTimeoutOrNull(5000) {
        suspendCancellableCoroutine<MediaController?> { cont ->
            future.addListener({ cont.resume(runCatching { future.get() }.getOrNull()) }, MoreExecutors.directExecutor())
        }
    }
    try {
        if (controller != null) {
            block(controller)
            delay(800)
        }
    } finally {
        MediaController.releaseFuture(future)
    }
    Widgets.refreshMusic(context)
}

class PlayPauseAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) =
        withController(context) { c ->
            if (c.isPlaying) c.pause() else {
                if (c.playbackState == Player.STATE_IDLE) c.prepare()
                c.play()
            }
        }
}

class NextAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) =
        withController(context) { it.seekToNext() }
}

class PrevAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) =
        withController(context) { it.seekToPrevious() }
}

class DownloadAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) =
        withController(context) { it.sendCustomCommand(SessionCommand(PlaybackService.ACTION_DOWNLOAD, Bundle.EMPTY), Bundle.EMPTY) }
}
