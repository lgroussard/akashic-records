package com.akashicrecords.music

import android.content.Context
import android.net.Uri
import android.os.Bundle
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.ConfigStore
import com.akashicrecords.data.MusicTrack
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// Media items shared by the UI and the service. Suggestions carry their original title/artist in
// the extras, for the ⤓ of the mini-player.
object PlayerItems {
    const val REMOTE_PREFIX = "remote:"
    const val EXTRA_TITLE = "akashic.title"
    const val EXTRA_ARTIST = "akashic.artist"

    fun local(t: MusicTrack, uri: Uri): MediaItem = MediaItem.Builder()
        .setMediaId(t.Id.toString())
        .setUri(uri)
        .setMediaMetadata(
            MediaMetadata.Builder()
                .setTitle(t.Title.ifBlank { "(sans titre)" })
                .setArtist(t.Artist)
                .setAlbumTitle(t.Album)
                .build(),
        )
        .build()

    // [full] = the whole track fetched by yt-dlp; otherwise the 30 s iTunes clip.
    fun suggestion(s: SimilarTrack, uri: Uri, album: String, full: Boolean, prefix: String = REMOTE_PREFIX): MediaItem =
        MediaItem.Builder()
            .setMediaId(prefix + s.title + "|" + s.artist)
            .setUri(uri)
            .setMediaMetadata(
                MediaMetadata.Builder()
                    .setTitle(s.title)
                    .setArtist(if (full) s.artist else "${s.artist} · extrait")
                    .setAlbumTitle(album)
                    .setExtras(Bundle().apply {
                        putString(EXTRA_TITLE, s.title)
                        putString(EXTRA_ARTIST, s.artist)
                    })
                    .build(),
            )
            .build()

    fun isSuggestion(item: MediaItem?) =
        item?.mediaId?.let { it.startsWith(REMOTE_PREFIX) || it.startsWith(PREVIEW_PREFIX) } == true
}

// Desktop AdvanceAsync, in the playback service so it also runs with the screen off. As soon as
// fewer than AHEAD (2) tracks follow the current one, suggestions are appended until there are,
// each the next unheard one: the local copy if the title is in the library, else the full track
// fetched by yt-dlp, else its 30 s clip. Keeping two ready means ⏭ always has a target, even
// while the following one loads. When the pool runs dry it is refilled from the newest queued
// track. A new queue (first item changes) restarts the pool, like the desktop PlayQueue.
class SuggestionChain(private val context: Context, private val player: Player) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val db = AkashicDatabase.get(context)
    private val library = MusicLibrary.get(context)

    private var runKey: String? = null
    private var runAlbum = ""
    private val played = ArrayList<String>()
    private val pool = ArrayList<SimilarTrack>()
    private var job: Job? = null
    private var skipRequested = false

    // ⏭ pressed while the next suggestion is still loading: jump to it once it is appended.
    fun requestSkip() {
        skipRequested = true
    }

    private companion object {
        // Tracks kept ready after the current one, so ⏭ always has somewhere to go.
        const val AHEAD = 2
    }

    private val listener = object : Player.Listener {
        override fun onEvents(p: Player, events: Player.Events) {
            if (events.containsAny(Player.EVENT_MEDIA_ITEM_TRANSITION, Player.EVENT_TIMELINE_CHANGED)) onQueueMoved()
        }

        // Unplayable entry (expired stream, bad file): move on instead of stopping.
        override fun onPlayerError(error: PlaybackException) {
            if (player.hasNextMediaItem()) {
                player.seekToNextMediaItem()
                player.prepare()
                player.play()
            }
        }
    }

    init {
        player.addListener(listener)
        // Temp suggestion files of a previous session (app cache only).
        Downloader.streamDir(context).listFiles()?.forEach { it.delete() }
    }

    fun release() {
        player.removeListener(listener)
        scope.cancel()
    }

    private fun onQueueMoved() {
        if (player.mediaItemCount == 0) {
            // Listening stopped (queue cleared): forget the run, so no suggestion is appended into the empty queue.
            job?.cancel()
            job = null
            runKey = null
            skipRequested = false
            return
        }
        val first = player.getMediaItemAt(0)
        if (first.mediaId != runKey) {
            runKey = first.mediaId
            runAlbum = first.mediaMetadata.albumTitle?.toString() ?: ""
            played.clear()
            pool.clear()
            skipRequested = false
            job?.cancel()
            job = null
        }
        player.currentMediaItem?.let { played += titleOf(it) }
        if (ahead() < AHEAD && job?.isActive != true) {
            val key = runKey
            // Tops the queue up one track at a time until AHEAD tracks follow the current one.
            job = scope.launch {
                while (ahead() < AHEAD && key == runKey) {
                    if (!appendNext(key)) break
                }
            }
        }
    }

    private fun titleOf(item: MediaItem): String =
        item.mediaMetadata.extras?.getString(PlayerItems.EXTRA_TITLE) ?: item.mediaMetadata.title?.toString() ?: ""

    private fun artistOf(item: MediaItem): String =
        item.mediaMetadata.extras?.getString(PlayerItems.EXTRA_ARTIST)
            ?: item.mediaMetadata.artist?.toString()?.removeSuffix(" · extrait") ?: ""

    // Heard already, or waiting in the queue (desktop Owned/PlayedAlready).
    private fun heard(title: String) =
        played.any { Recommendations.matches(it, title) } ||
            (0 until player.mediaItemCount).any { Recommendations.matches(titleOf(player.getMediaItemAt(it)), title) }

    private fun ahead() = player.mediaItemCount - 1 - player.currentMediaItemIndex

    // Appends one suggestion; false when none could be found or loaded.
    private suspend fun appendNext(key: String?): Boolean {
        if (player.mediaItemCount == 0) return false
        // Pool refills are seeded from the newest queued track, so the run keeps drifting forward.
        val seed = player.getMediaItemAt(player.mediaItemCount - 1)
        val seedTitle = titleOf(seed)
        val seedArtist = artistOf(seed)
        val tracks = withContext(Dispatchers.IO) {
            if (library.available.isEmpty()) library.scanAndSync(ConfigStore(context).musicFolder())
            db.musicTrackDao().getAll().filter { library.uriFor(it) != null }
        }

        var next = takeFromPool()
        if (next == null) {
            val fresh = withContext(Dispatchers.IO) { Recommendations.similar(seedTitle, seedArtist) }
            for (s in fresh) {
                if (Recommendations.matches(seedTitle, s.title)) continue
                if (pool.none { Recommendations.matches(it.title, s.title) }) pool += s
            }
            next = takeFromPool()
        }
        next ?: return false

        val local = tracks.firstOrNull { Recommendations.matches(it.Title, next.title) }
        val item = if (local != null) {
            PlayerItems.local(local, library.uriFor(local)!!)
        } else {
            val file = Downloader.fetchStream(context, next.title, next.artist)
            when {
                file != null -> PlayerItems.suggestion(next, Uri.fromFile(file), runAlbum, full = true)
                next.preview != null -> PlayerItems.suggestion(next, Uri.parse(next.preview), runAlbum, full = false)
                else -> return false
            }
        }
        // The user may have started another queue meanwhile: drop the stale suggestion.
        if (key != runKey || player.mediaItemCount == 0) return false
        val ended = player.playbackState == Player.STATE_ENDED
        val wasLast = ahead() == 0
        player.addMediaItem(item)
        // The fetch outlasted the track, or ⏭ was pressed meanwhile: go to the new entry.
        if (ended || (skipRequested && wasLast)) {
            skipRequested = false
            player.seekTo(player.currentMediaItemIndex + 1, 0)
            player.play()
        }
        return true
    }

    // First pooled suggestion not heard yet (desktop NextSuggestion).
    private fun takeFromPool(): SimilarTrack? {
        while (pool.isNotEmpty()) {
            val s = pool.removeAt(0)
            if (!heard(s.title)) return s
        }
        return null
    }
}
