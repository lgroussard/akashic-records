package com.akashicrecords.music

import android.content.Context
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.sync.HubClient
import com.akashicrecords.sync.SyncService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

// The download board kept by the hub and shared by every device: the hub downloads, then adds the track to the
// catalogue; this phone lists it at its next sync (to stream it, or keep it). Used whenever the phone is paired;
// unpaired, Downloader keeps downloading on the phone itself.
object HubDownloads {
    class Zone(val id: String, val name: String, val folder: String)

    class Item(
        val id: String, val label: String, val query: String, val zoneId: String?, val status: String,
        val error: String?, val addedBy: String, val folder: String?, val trackPath: String?,
    ) {
        val waiting get() = status == "queued"
        val running get() = status == "running"
        val failed get() = status == "failed"
        val done get() = status == "done"
    }

    class Board(
        val enabled: Boolean, val toolVersion: String?, val toolProblem: String?, val current: String?,
        val zones: List<Zone>, val items: List<Item>, val folders: List<String>,
    ) {
        val busy get() = items.any { it.waiting || it.running }
        fun zone(id: String?) = zones.firstOrNull { it.id == id }
    }

    private val _board = MutableStateFlow<Board?>(null)
    val board: StateFlow<Board?> = _board
    private val _error = MutableStateFlow<String?>(null)
    val error: StateFlow<String?> = _error

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var follow: Job? = null

    private fun hub(context: Context): HubClient? = SyncService.credentials(context)?.let { (url, token) -> HubClient(url, token) }

    private fun parse(o: JSONObject): Board {
        fun JSONObject.str(k: String) = if (isNull(k)) null else optString(k)
        val tool = o.optJSONObject("tool") ?: JSONObject()
        val zones = o.optJSONArray("zones")
        val jobs = o.optJSONArray("jobs")
        val folders = o.optJSONArray("folders")
        return Board(
            enabled = o.optBoolean("enabled", true),
            toolVersion = tool.str("version"), toolProblem = tool.str("problem"), current = tool.str("current"),
            zones = (0 until (zones?.length() ?: 0)).map { zones!!.getJSONObject(it) }.map { Zone(it.getString("id"), it.getString("name"), it.optString("folder")) },
            items = (0 until (jobs?.length() ?: 0)).map { jobs!!.getJSONObject(it) }.map {
                Item(it.getString("id"), it.getString("label"), it.getString("query"), it.str("zone_id"), it.getString("status"),
                    it.str("error"), it.optString("added_by"), it.str("folder"), it.str("track_path"))
            },
            folders = (0 until (folders?.length() ?: 0)).map { folders!!.getString(it) },
        )
    }

    /** Runs [action] against the hub and keeps the board it answers; failures go to [error]. */
    private suspend fun act(context: Context, action: (HubClient) -> JSONObject?): Boolean = withContext(Dispatchers.IO) {
        val hub = hub(context) ?: return@withContext false
        try {
            action(hub)
            val before = _board.value
            val after = parse(hub.downloads())
            _board.value = after
            _error.value = null
            // A track has just been downloaded by the hub: fetch the catalogue now, so it shows up at once.
            val newlyDone = after.items.filter { it.done }.map { it.id }.toSet() - (before?.items?.filter { it.done }?.map { it.id }?.toSet() ?: emptySet())
            if (before != null && newlyDone.isNotEmpty()) SyncService.run(context)
            true
        } catch (e: Exception) {
            _error.value = e.message ?: e.javaClass.simpleName
            false
        }
    }

    suspend fun refresh(context: Context) = act(context) { null }
    suspend fun add(context: Context, query: String, label: String, zoneId: String?) = act(context) { it.addDownload(query, label, zoneId) }
    suspend fun move(context: Context, id: String, zoneId: String?) = act(context) { it.moveDownload(id, zoneId) }
    suspend fun retry(context: Context, id: String) = act(context) { it.retryDownload(id) }
    suspend fun remove(context: Context, id: String) = act(context) { it.removeDownload(id) }
    suspend fun clearDone(context: Context) = act(context) { it.clearDoneDownloads() }
    suspend fun renameZone(context: Context, id: String, name: String) = act(context) { it.renameZone(id, name) }
    suspend fun removeZone(context: Context, id: String) = act(context) { it.removeZone(id) }

    /** The zone for a folder (existing music folder or new one), created on the hub if needed; null for the root. */
    suspend fun zoneFor(context: Context, name: String?, folder: String?): String? = withContext(Dispatchers.IO) {
        val f = Downloader.sanitize(folder ?: name)
        if (f.isEmpty() && name.isNullOrBlank()) return@withContext null
        val hub = hub(context) ?: return@withContext null
        val id = hub.addZone(name?.takeIf { it.isNotBlank() }, f).getString("id")
        refresh(context)
        id
    }

    /**
     * Sends this phone's queued requests (every ⤓ of the app lands in the local queue first) to the hub's board,
     * each in the zone of its album. Sent ones are marked done here: the ✓ of the ⤓ buttons keeps working.
     * Throws when the hub does not answer: they stay queued and go at the next sync.
     */
    suspend fun send(context: Context, db: AkashicDatabase): Int = withContext(Dispatchers.IO) {
        val hub = hub(context) ?: return@withContext 0
        val queued = db.downloadItemDao().getAll().filter { it.Status == Downloader.QUEUED }
        if (queued.isEmpty()) return@withContext 0
        val zones = db.downloadFolderDao().getAll().associateBy { it.Id }
        val zoneIds = HashMap<String, String?>()
        for (item in queued) {
            val folder = Downloader.sanitize(item.FolderId?.let { zones[it]?.Subfolder })
            val zoneId = zoneIds.getOrPut(folder) { if (folder.isEmpty()) null else hub.addZone(folder, folder).getString("id") }
            hub.addDownload(item.Url, item.Title, zoneId)
            db.downloadItemDao().update(item.copy(Status = Downloader.DONE))
        }
        _board.value = parse(hub.downloads())
        followUntilDone(context)
        queued.size
    }

    /** While the hub has work, keeps an eye on the board (every few seconds), so a finished track is fetched at once. */
    fun followUntilDone(context: Context) {
        if (follow?.isActive == true) return
        val app = context.applicationContext
        follow = scope.launch {
            repeat(360) { // 30 min at most
                delay(5_000)
                if (!refresh(app)) return@launch
                if (_board.value?.busy != true) return@launch
            }
        }
    }
}
