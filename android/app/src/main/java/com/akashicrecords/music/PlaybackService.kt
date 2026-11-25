package com.akashicrecords.music

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.ForwardingPlayer
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.common.Timeline
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.session.CommandButton
import androidx.media3.session.MediaButtonReceiver
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService
import androidx.media3.session.SessionCommand
import androidx.media3.session.SessionResult
import com.akashicrecords.Launch
import com.akashicrecords.MainActivity
import com.akashicrecords.R
import com.akashicrecords.calendar.Reminders
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.ConfigStore
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.SettableFuture
import java.io.File
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject

// Background playback: the player lives in a MediaSessionService so music keeps playing with
// the screen off, and Android shows the standard media notification / lock-screen controls.
// The UI talks to it through a MediaController (see PlayerConnection).
//
// The notification also carries a ⤓ while a suggestion plays (same as the mini-player's): one
// tap queues it and starts the download; it turns into ✓ once queued.
class PlaybackService : MediaSessionService() {
    private var session: MediaSession? = null
    private var chain: SuggestionChain? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    private val downloadCommand = SessionCommand(ACTION_DOWNLOAD, Bundle.EMPTY)

    // Keeps the ⏮ and ⏭ spots of the system player even while there is no next track yet (a
    // suggestion still loading); otherwise Android moves ⤓ into the empty ⏭ spot.
    private val slotReservation = Bundle().apply {
        putBoolean(androidx.media3.session.MediaConstants.EXTRAS_KEY_SLOT_RESERVATION_SEEK_TO_NEXT, true)
        putBoolean(androidx.media3.session.MediaConstants.EXTRAS_KEY_SLOT_RESERVATION_SEEK_TO_PREV, true)
    }

    private val callback = object : MediaSession.Callback {
        override fun onConnect(session: MediaSession, controller: MediaSession.ControllerInfo): MediaSession.ConnectionResult {
            val commands = MediaSession.ConnectionResult.DEFAULT_SESSION_COMMANDS.buildUpon().add(downloadCommand).build()
            return MediaSession.ConnectionResult.AcceptedResultBuilder(session)
                .setAvailableSessionCommands(commands)
                .setCustomLayout(session.customLayout)
                .setSessionExtras(slotReservation)
                .build()
        }

        override fun onCustomCommand(
            session: MediaSession,
            controller: MediaSession.ControllerInfo,
            customCommand: SessionCommand,
            args: Bundle,
        ): ListenableFuture<SessionResult> {
            if (customCommand.customAction == ACTION_DOWNLOAD) downloadCurrent()
            return Futures.immediateFuture(SessionResult(SessionResult.RESULT_SUCCESS))
        }

        // ▶ (headset button, widget) on an empty player, typically after Android killed the process
        // during a long pause: the saved queue comes back where the listening stopped.
        override fun onPlaybackResumption(
            session: MediaSession,
            controller: MediaSession.ControllerInfo,
        ): ListenableFuture<MediaSession.MediaItemsWithStartPosition> {
            val future = SettableFuture.create<MediaSession.MediaItemsWithStartPosition>()
            scope.launch {
                val saved = runCatching { withContext(Dispatchers.IO) { SavedQueue.load(this@PlaybackService) } }.getOrNull()
                // Nothing to resume: same answer as the default callback.
                if (saved == null) future.setException(UnsupportedOperationException()) else future.set(saved)
            }
            return future
        }
    }

    override fun onCreate() {
        super.onCreate()
        val player = ExoPlayer.Builder(this)
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(C.USAGE_MEDIA)
                    .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
                    .build(),
                /* handleAudioFocus = */ true,
            )
            .setHandleAudioBecomingNoisy(true) // pause when headphones are unplugged
            // Suggestions can be network streams (30 s clips): keeps the CPU and the Wi-Fi awake while
            // playing with the screen off.
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .build()
        val chain = SuggestionChain(this, player).also { this.chain = it }
        // ⏭ is always offered, so the system player never drops it (and never slides ⤓ into its
        // place) while the next suggestion is still loading. A ⏭ pressed in that window is kept
        // and applied as soon as the suggestion lands.
        val sessionPlayer = object : ForwardingPlayer(player) {
            override fun getAvailableCommands(): Player.Commands =
                super.getAvailableCommands().buildUpon()
                    .add(Player.COMMAND_SEEK_TO_NEXT)
                    .add(Player.COMMAND_SEEK_TO_NEXT_MEDIA_ITEM)
                    .build()

            override fun isCommandAvailable(command: Int): Boolean =
                command == Player.COMMAND_SEEK_TO_NEXT || command == Player.COMMAND_SEEK_TO_NEXT_MEDIA_ITEM ||
                    super.isCommandAvailable(command)

            override fun seekToNext() {
                if (hasNextMediaItem()) super.seekToNext() else chain.requestSkip()
            }

            override fun seekToNextMediaItem() {
                if (hasNextMediaItem()) super.seekToNextMediaItem() else chain.requestSkip()
            }
        }
        session = MediaSession.Builder(this, sessionPlayer)
            .setCallback(callback)
            .setSessionExtras(slotReservation)
            .setSessionActivity(openPlayer())
            .build()
        player.addListener(object : Player.Listener {
            override fun onEvents(p: Player, events: Player.Events) {
                if (events.containsAny(Player.EVENT_MEDIA_ITEM_TRANSITION, Player.EVENT_TIMELINE_CHANGED)) refreshLayout()
                if (events.containsAny(Player.EVENT_IS_PLAYING_CHANGED, Player.EVENT_MEDIA_ITEM_TRANSITION, Player.EVENT_MEDIA_METADATA_CHANGED)) publishNowPlaying()
                // Where the listening is, at each track change and at each pause (see SavedQueue).
                if (events.containsAny(Player.EVENT_MEDIA_ITEM_TRANSITION, Player.EVENT_TIMELINE_CHANGED, Player.EVENT_PLAY_WHEN_READY_CHANGED)) SavedQueue.savePosition(this@PlaybackService, p)
            }

            // Only when the queue itself changed, not when a track merely reports its duration.
            override fun onTimelineChanged(timeline: Timeline, reason: Int) {
                if (reason == Player.TIMELINE_CHANGE_REASON_PLAYLIST_CHANGED) SavedQueue.saveItems(this@PlaybackService, player)
            }
        })
        // A ⤓ from the app's mini-player also turns the notification button into ✓.
        scope.launch {
            Downloader.state.collect { st ->
                if (st.running) refreshLayout()
                publishNowPlaying()
            }
        }
    }

    // Title/artist of the playing suggestion, or null for a library track.
    private fun currentSuggestion(): Triple<String, String, String>? {
        val item = session?.player?.currentMediaItem
        if (!PlayerItems.isSuggestion(item)) return null
        val md = item!!.mediaMetadata
        val title = md.extras?.getString(PlayerItems.EXTRA_TITLE) ?: md.title?.toString() ?: return null
        val artist = md.extras?.getString(PlayerItems.EXTRA_ARTIST) ?: ""
        return Triple(title, artist, md.albumTitle?.toString() ?: "")
    }

    private fun refreshLayout() {
        val s = currentSuggestion()
        if (s == null) {
            session?.setCustomLayout(emptyList())
            return
        }
        val url = Downloader.searchQuery(s.first, s.second)
        scope.launch {
            val queued = withContext(Dispatchers.IO) { AkashicDatabase.get(this@PlaybackService).downloadItemDao().countByUrl(url) > 0 }
            if (currentSuggestion()?.first != s.first) return@launch // moved on meanwhile
            session?.setCustomLayout(listOf(downloadButton(queued)))
        }
    }

    private fun downloadButton(queued: Boolean) = CommandButton.Builder()
        .setDisplayName(if (queued) "Téléchargé" else "Télécharger")
        .setIconResId(if (queued) R.drawable.ic_downloaded else R.drawable.ic_download)
        .setSessionCommand(downloadCommand)
        // Kept enabled: the system media controls hide disabled buttons. A tap on ✓ is a no-op.
        .setEnabled(true)
        .build()

    private fun downloadCurrent() {
        val (title, artist, album) = currentSuggestion() ?: return
        val db = AkashicDatabase.get(this)
        scope.launch {
            val added = withContext(Dispatchers.IO) { Downloader.enqueue(db, Downloader.searchQuery(title, artist), "$title — $artist", album) }
            session?.setCustomLayout(listOf(downloadButton(queued = true)))
            publishNowPlaying()
            if (added) Downloader.runQueue(this@PlaybackService, db, ConfigStore(this@PlaybackService).musicFolder())
        }
    }

    // Home-screen music widget: what plays, and whether the suggestion is already downloaded.
    private fun publishNowPlaying() {
        val p = session?.player ?: return
        val item = p.currentMediaItem
        val md = item?.mediaMetadata
        val suggestion = currentSuggestion()
        val playing = p.isPlaying
        scope.launch {
            val saved = suggestion != null && withContext(Dispatchers.IO) {
                AkashicDatabase.get(this@PlaybackService).downloadItemDao().countByUrl(Downloader.searchQuery(suggestion.first, suggestion.second)) > 0
            }
            com.akashicrecords.widgets.NowPlaying.write(
                this@PlaybackService,
                com.akashicrecords.widgets.NowPlaying.State(
                    title = md?.title?.toString() ?: "",
                    artist = md?.artist?.toString() ?: "",
                    playing = playing,
                    suggestion = suggestion != null,
                    saved = saved,
                ),
            )
            com.akashicrecords.widgets.Widgets.refreshMusic(this@PlaybackService)
        }
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? = session

    // A tap on the notification (or lock-screen player) opens the app on the full player's « À suivre ».
    private fun openPlayer(): PendingIntent {
        val intent = Intent(this, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP
            putExtra(Reminders.EXTRA_SECTION, Launch.PLAYER)
        }
        return PendingIntent.getActivity(this, 7301, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    }

    // Swiping the app away while paused stops the service; while playing, music continues.
    override fun onTaskRemoved(rootIntent: android.content.Intent?) {
        val player = session?.player
        if (player == null || !player.playWhenReady || player.mediaItemCount == 0) stopSelf()
    }

    override fun onDestroy() {
        scope.cancel()
        chain?.release()
        chain = null
        session?.run {
            SavedQueue.savePosition(this@PlaybackService, player)
            player.release()
            release()
        }
        session = null
        super.onDestroy()
    }

    companion object {
        const val ACTION_DOWNLOAD = "akashic.download"
    }
}

// The queue as last listened to, kept on disk: Android kills the process during a long pause, and
// the player's queue with it. Holds what rebuilds each MediaItem (library tracks and suggestions
// alike), plus the current entry and the position in it.
internal object SavedQueue {
    private const val ITEMS = "items"
    private const val INDEX = "index"
    private const val POSITION = "position"

    private fun prefs(context: Context) = context.getSharedPreferences("akashic_playback", Context.MODE_PRIVATE)

    // An emptied queue (■ Arrêter) clears the save: there is nothing to come back to.
    fun saveItems(context: Context, player: Player) {
        if (player.mediaItemCount == 0) {
            prefs(context).edit().clear().apply()
            return
        }
        val items = JSONArray()
        for (i in 0 until player.mediaItemCount) {
            val item = player.getMediaItemAt(i)
            val md = item.mediaMetadata
            items.put(
                JSONObject()
                    .put("id", item.mediaId)
                    .put("uri", item.localConfiguration?.uri?.toString() ?: "")
                    .put("title", md.title?.toString() ?: "")
                    .put("artist", md.artist?.toString() ?: "")
                    .put("album", md.albumTitle?.toString() ?: "")
                    // Suggestions only (PlayerItems.suggestion).
                    .putOpt("xTitle", md.extras?.getString(PlayerItems.EXTRA_TITLE))
                    .putOpt("xArtist", md.extras?.getString(PlayerItems.EXTRA_ARTIST)),
            )
        }
        prefs(context).edit().putString(ITEMS, items.toString()).apply()
    }

    fun savePosition(context: Context, player: Player) {
        if (player.mediaItemCount == 0) return
        prefs(context).edit()
            .putInt(INDEX, player.currentMediaItemIndex)
            .putLong(POSITION, player.currentPosition.coerceAtLeast(0))
            .apply()
    }

    private fun items(context: Context): JSONArray? =
        prefs(context).getString(ITEMS, null)?.let { runCatching { JSONArray(it) }.getOrNull() }?.takeIf { it.length() > 0 }

    fun isEmpty(context: Context) = items(context) == null

    // Suggestion files (app cache) the saved queue still points at.
    fun files(context: Context): Set<String> {
        val items = items(context) ?: return emptySet()
        return (0 until items.length())
            .map { Uri.parse(items.getJSONObject(it).optString("uri")) }
            .filter { it.scheme == "file" }
            .mapNotNull { it.path }
            .toSet()
    }

    // The saved queue as media items, without the entries whose file is gone since (track deleted
    // from the phone, cache cleared); null when nothing is left to play.
    suspend fun load(context: Context): MediaSession.MediaItemsWithStartPosition? {
        val saved = items(context) ?: return null
        val library = MusicLibrary.get(context)
        if (library.available.isEmpty()) library.scanAndSync(ConfigStore(context).musicFolder())
        val onPhone = library.available.values.toSet()
        val index = prefs(context).getInt(INDEX, 0)
        var position = prefs(context).getLong(POSITION, 0)
        var start = 0
        val items = ArrayList<MediaItem>()
        for (i in 0 until saved.length()) {
            val o = saved.getJSONObject(i)
            val uri = Uri.parse(o.optString("uri"))
            val there = when (uri.scheme) {
                "content" -> uri in onPhone // a library track
                "file" -> uri.path?.let { File(it).exists() } == true // a suggestion fetched whole
                "http", "https" -> true // a 30 s clip, streamed
                else -> false
            }
            if (i == index) {
                start = items.size
                // The track that was playing is gone: the one after it starts from its beginning.
                if (!there) position = 0
            }
            if (!there) continue
            val md = MediaMetadata.Builder()
                .setTitle(o.optString("title"))
                .setArtist(o.optString("artist"))
                .setAlbumTitle(o.optString("album"))
            if (o.has("xTitle")) {
                md.setExtras(Bundle().apply {
                    putString(PlayerItems.EXTRA_TITLE, o.optString("xTitle"))
                    putString(PlayerItems.EXTRA_ARTIST, o.optString("xArtist"))
                })
            }
            items += MediaItem.Builder().setMediaId(o.optString("id")).setUri(uri).setMediaMetadata(md.build()).build()
        }
        if (items.isEmpty()) return null
        if (start >= items.size) {
            start = items.size - 1
            position = 0
        }
        return MediaSession.MediaItemsWithStartPosition(items, start, position)
    }
}

// The headset's ▶ once the service is gone. Media3's receiver starts the service in the foreground
// for any ▶, and Android kills the app if no playback follows within seconds: with nothing saved
// to resume, the press is ignored instead.
class ResumeButtonReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (!SavedQueue.isEmpty(context)) MediaButtonReceiver().onReceive(context, intent)
    }
}
