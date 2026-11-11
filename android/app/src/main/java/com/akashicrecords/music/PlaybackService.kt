package com.akashicrecords.music

import android.app.PendingIntent
import android.content.Intent
import android.os.Bundle
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.ForwardingPlayer
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.session.CommandButton
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
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

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
