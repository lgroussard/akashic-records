package com.akashicrecords.music

import android.content.ComponentName
import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.ContextCompat
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import com.akashicrecords.data.MusicTrack
import kotlinx.coroutines.delay

// Snapshot of the player, read by the Compose UI.
data class PlayerUi(
    val title: String = "",
    val artist: String = "",
    val album: String = "",
    val isPreview: Boolean = false, // a suggestion (full track or 30 s clip), not a library track
    val suggestionTitle: String? = null, // original title/artist of a suggestion, for the ⤓
    val suggestionArtist: String? = null,
    val fromSimilarTab: Boolean = false, // started by a tap in Similaires (not chained)
    val isPlaying: Boolean = false,
    val positionMs: Long = 0,
    val durationMs: Long = 0,
    val queue: List<Pair<Int, String>> = emptyList(), // trackId -> label
    val currentIndex: Int = -1,
    val shuffle: Boolean = false,
)

// UI-side handle on the PlaybackService, through a MediaController.
class PlayerConnection(context: Context) {
    var controller: MediaController? by mutableStateOf(null)
        private set
    var ui by mutableStateOf(PlayerUi())
        private set

    private val listener = object : Player.Listener {
        override fun onEvents(player: Player, events: Player.Events) = refresh()
    }

    private val future = MediaController.Builder(
        context,
        SessionToken(context, ComponentName(context, PlaybackService::class.java)),
    ).buildAsync().also { f ->
        f.addListener({
            runCatching { f.get() }.getOrNull()?.let {
                it.addListener(listener)
                controller = it
                refresh()
            }
        }, ContextCompat.getMainExecutor(context))
    }

    fun refresh() {
        val c = controller ?: return
        val md = c.mediaMetadata
        ui = PlayerUi(
            title = md.title?.toString() ?: "",
            artist = md.artist?.toString() ?: "",
            album = md.albumTitle?.toString() ?: "",
            isPreview = PlayerItems.isSuggestion(c.currentMediaItem),
            suggestionTitle = if (PlayerItems.isSuggestion(c.currentMediaItem)) md.extras?.getString(PlayerItems.EXTRA_TITLE) ?: md.title?.toString() else null,
            suggestionArtist = if (PlayerItems.isSuggestion(c.currentMediaItem)) md.extras?.getString(PlayerItems.EXTRA_ARTIST) ?: "" else null,
            fromSimilarTab = c.currentMediaItem?.mediaId?.startsWith(PREVIEW_PREFIX) == true,
            isPlaying = c.isPlaying,
            positionMs = c.currentPosition.coerceAtLeast(0),
            durationMs = c.duration.takeIf { it > 0 } ?: 0,
            queue = (0 until c.mediaItemCount).map { i ->
                val item = c.getMediaItemAt(i)
                (item.mediaId.toIntOrNull() ?: -1) to (item.mediaMetadata.title?.toString() ?: "")
            },
            currentIndex = if (c.mediaItemCount == 0) -1 else c.currentMediaItemIndex,
            shuffle = c.shuffleModeEnabled,
        )
    }

    // Replaces the queue with [tracks] and starts at [startIndex] (desktop PlayQueue).
    fun playQueue(tracks: List<Pair<MusicTrack, android.net.Uri>>, startIndex: Int) {
        val c = controller ?: return
        if (tracks.isEmpty()) return
        c.setMediaItems(tracks.map { (t, uri) -> toItem(t, uri) }, startIndex.coerceIn(0, tracks.size - 1), 0L)
        c.prepare()
        c.play()
    }

    // Inserts right after the current track (desktop PlayNext).
    fun playNext(track: MusicTrack, uri: android.net.Uri) {
        val c = controller ?: return
        if (c.mediaItemCount == 0) return playQueue(listOf(track to uri), 0)
        c.addMediaItem(c.currentMediaItemIndex + 1, toItem(track, uri))
    }

    // Plays a suggestion right after the current track (desktop PlayNext on a remote entry):
    // [uri] is the full track fetched by yt-dlp ([full]) or the 30 s clip. [album] keeps the run's
    // album so a ⤓ from it files the download in the same zone.
    fun playSuggestion(s: SimilarTrack, uri: android.net.Uri, album: String, full: Boolean) {
        val c = controller ?: return
        val item = PlayerItems.suggestion(s, uri, album, full, prefix = PREVIEW_PREFIX)
        if (c.mediaItemCount == 0) {
            c.setMediaItem(item)
            c.prepare()
        } else {
            c.addMediaItem(c.currentMediaItemIndex + 1, item)
            c.seekTo(c.currentMediaItemIndex + 1, 0)
        }
        c.play()
    }

    // The full track of [s] has arrived while its 30 s clip plays (or waits in the queue): swapped in place,
    // at the same second when it is the one playing, so the music carries on without starting over.
    // Nothing happens when the clip has left the queue (the user moved on).
    fun upgradeSuggestion(s: SimilarTrack, uri: android.net.Uri, album: String) {
        val c = controller ?: return
        val id = PREVIEW_PREFIX + s.title + "|" + s.artist
        val index = (0 until c.mediaItemCount).firstOrNull { c.getMediaItemAt(it).mediaId == id } ?: return
        val item = PlayerItems.suggestion(s, uri, album, full = true, prefix = PREVIEW_PREFIX)
        // Also when the clip ran out before the full track came (player stopped on it): it resumes from there.
        val ended = c.playbackState == androidx.media3.common.Player.STATE_ENDED && index == c.currentMediaItemIndex
        if (index == c.currentMediaItemIndex) {
            val position = c.currentPosition
            val playing = c.playWhenReady
            c.replaceMediaItem(index, item)
            c.seekTo(index, position)
            c.playWhenReady = playing || ended
            if (ended) c.prepare()
        } else {
            c.replaceMediaItem(index, item)
        }
    }

    fun togglePlay() {
        val c = controller ?: return
        if (c.isPlaying) c.pause() else {
            if (c.playbackState == Player.STATE_ENDED) c.seekTo(0, 0)
            c.play()
        }
    }

    // ⏮ restarts the track past 3 s, otherwise goes to the previous one (standard behaviour).
    fun previous() = controller?.seekToPrevious()
    fun next() = controller?.seekToNext()
    fun seekTo(ms: Long) = controller?.seekTo(ms)
    fun jumpTo(index: Int) = controller?.let { it.seekTo(index, 0); it.play() }
    fun removeAt(index: Int) = controller?.removeMediaItem(index)
    fun toggleShuffle() = controller?.let { it.shuffleModeEnabled = !it.shuffleModeEnabled }

    fun release() {
        controller?.removeListener(listener)
        MediaController.releaseFuture(future)
        controller = null
    }

    private fun toItem(t: MusicTrack, uri: android.net.Uri) = PlayerItems.local(t, uri)
}

internal const val PREVIEW_PREFIX = "preview:"

@Composable
fun rememberPlayerConnection(): PlayerConnection {
    val context = LocalContext.current.applicationContext
    val connection = remember { PlayerConnection(context) }
    DisposableEffect(connection) { onDispose { connection.release() } }
    // Position ticker: the listener does not fire while the playhead simply advances.
    LaunchedEffect(connection) {
        while (true) {
            if (connection.ui.isPlaying) connection.refresh()
            delay(500)
        }
    }
    return connection
}
