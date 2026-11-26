package com.akashicrecords.sync

import android.content.Context
import android.os.Build
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.akashicrecords.data.AkashicDatabase
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.withContext
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.UUID
import java.util.concurrent.TimeUnit

/**
 * The phone's link to the sync hub (MOBILITE-SYNC.md): holds the pairing, runs the engine in the background,
 * and tells the screens when rows arrived. Not paired = does nothing at all.
 *
 * When it runs: when the app comes to the front, a few seconds after leaving it (what was just written goes
 * out), every 15 minutes while the phone has a network, and on demand from the settings. Away from home the
 * hub is simply unreachable: the run fails fast and the next one tries again.
 */
object SyncService {

    // What is synchronised. A recipe's cover is a file of this phone: its path stays here.
    // What is synchronised: every table of the catalog. File paths travel as they are (same relative form on the
    // PC and the phone); the files themselves are another matter.
    private val TABLES = AkashicCatalog.tables + FileSync.indexTable

    data class State(val paired: Boolean, val hubUrl: String, val busy: Boolean, val text: String, val isError: Boolean)

    private val mutex = Mutex()
    private val _state = MutableStateFlow(State(false, "", false, "", false))
    private val _dataVersion = MutableStateFlow(0)
    private val _conflicts = MutableStateFlow<List<SyncConflict>>(emptyList())

    /** Versions set aside by field-level conflicts, for the settings block. */
    val conflicts: StateFlow<List<SyncConflict>> get() = _conflicts

    /** Reloads [conflicts] from the database. */
    suspend fun refreshConflicts(context: Context) {
        _conflicts.value = withContext(Dispatchers.IO) {
            runCatching { SyncConflicts.list(AkashicDatabase.get(context.applicationContext).openHelper.writableDatabase) }.getOrDefault(emptyList())
        }
    }

    /** The user has read a set-aside version: forget it. */
    suspend fun dismissConflict(context: Context, id: Long) {
        withContext(Dispatchers.IO) { SyncConflicts.dismiss(AkashicDatabase.get(context.applicationContext).openHelper.writableDatabase, id) }
        refreshConflicts(context)
    }

    /** For the settings block. */
    val state: StateFlow<State> get() = _state

    /** Bumped each time a sync wrote rows here: screens keyed on it reload from the database. */
    val dataVersion: StateFlow<Int> get() = _dataVersion

    private fun prefs(context: Context) = context.applicationContext.getSharedPreferences("sync", Context.MODE_PRIVATE)

    /** Hub address and this phone's token, or null when not paired. */
    fun credentials(context: Context): Pair<String, String>? {
        val p = prefs(context)
        val url = p.getString("hubUrl", "").orEmpty()
        val token = p.getString("token", "").orEmpty()
        return if (url.isEmpty() || token.isEmpty()) null else url to token
    }

    private var lastSuccess: String? = null
    private var lastWhat: String? = null
    private var lastError: String? = null
    private var lastFiles: FileSyncReport? = null

    /** Reads the saved pairing; called once at start so the settings show the right state. */
    fun init(context: Context) = publish(context, busy = false)

    private fun publish(context: Context, busy: Boolean) {
        val p = prefs(context)
        val url = p.getString("hubUrl", "").orEmpty()
        val paired = url.isNotEmpty() && !p.getString("token", "").isNullOrEmpty()
        val text = when {
            !paired -> "Non appairé : ce téléphone ne synchronise rien."
            busy -> "Synchronisation…"
            lastError != null -> "Appairé avec $url. Dernier essai : $lastError"
            lastSuccess == null -> "Appairé avec $url. Pas encore synchronisé."
            else -> "Appairé avec $url. Dernière synchro à $lastSuccess : $lastWhat."
        }
        _state.value = State(paired, url, busy, text, paired && !busy && lastError != null)
    }

    /** One sync now. Returns null when not paired or when a run is already going. */
    suspend fun run(context: Context): SyncReport? {
        val app = context.applicationContext
        val p = prefs(app)
        val url = p.getString("hubUrl", "").orEmpty()
        val token = p.getString("token", "").orEmpty()
        if (url.isEmpty() || token.isEmpty()) return null
        if (!mutex.tryLock()) return null
        var report: SyncReport? = null
        try {
            publish(app, busy = true)
            // What is typed on the open page and not saved yet goes to the database first.
            withContext(Dispatchers.Main) { com.akashicrecords.journaux.PendingEdits.flushAll() }
            report = withContext(Dispatchers.IO) {
                val db = AkashicDatabase.get(app).openHelper.writableDatabase
                val hub = HubClient(url, token)
                // The files the rows point at: sent before the rows, fetched after (see FileSync).
                FileSync.ensureSchema(db)
                // Music lives in the phone's shared Music folder, and only the chosen tracks are fetched.
                val music = MusicFiles(app, com.akashicrecords.data.ConfigStore(app).musicFolder())
                val fileSync = FileSync(db, app.filesDir, app.cacheDir, hub, AkashicCatalog.fileColumns, music)
                val files = FileSyncReport()
                fileSync.publish(files)
                val rows = SyncEngine(db, hub, TABLES).run()
                fileSync.fetch(files)
                lastFiles = files
                rows
            }
            lastSuccess = SimpleDateFormat("HH:mm:ss", Locale.FRANCE).format(Date())
            lastWhat = (if (report.nothingToDo) "rien à échanger" else "${report.pushed} envoyé(s), ${report.received} reçu(s)") +
                (lastFiles?.takeIf { it.uploaded + it.downloaded > 0 }?.let { " ; fichiers : ${it.uploaded} envoyé(s), ${it.downloaded} reçu(s)" } ?: "")
            lastError = null
        } catch (e: HubException) {
            lastError = e.message
        } catch (e: SyncSafetyException) {
            lastError = e.message
        } catch (e: Exception) {
            lastError = "${e.javaClass.simpleName} : ${e.message}"
        } finally {
            mutex.unlock()
            publish(app, busy = false)
        }
        if (report != null && report.received + (lastFiles?.downloaded ?: 0) > 0) _dataVersion.value++
        if (report != null && report.setAside > 0) refreshConflicts(app)
        // Download requests made while the hub could not be reached: it can now.
        if (report != null) runCatching { com.akashicrecords.music.HubDownloads.send(app, AkashicDatabase.get(app)) }
        lastFiles?.let { f ->
            // A kept track has arrived: the music library rescans. And a run that stopped at its time budget
            // after moving something goes on right away, so a batch of tracks does not wait for the next period.
            if (f.downloadedMusic > 0) com.akashicrecords.music.Downloader.notifyLibraryChanged()
            if (report != null && f.uploaded + f.downloaded > 0 && f.toSend + f.toFetch > 0) requestSoon(app, 1)
        }
        return report
    }

    /** Pairs this phone with a hub (one-time code shown on the hub), then syncs once.
     * Throws HubException with a message fit for the user when the hub or the code is wrong. */
    suspend fun pair(context: Context, hubUrl: String, code: String) {
        val app = context.applicationContext
        var url = hubUrl.trim().trimEnd('/')
        if (!url.startsWith("http://", true) && !url.startsWith("https://", true)) url = "http://$url"
        val p = prefs(app)
        val deviceId = p.getString("deviceId", null) ?: UUID.randomUUID().toString()
        val (id, token) = withContext(Dispatchers.IO) {
            val hub = HubClient(url)
            hub.health() // a clear "unreachable" before spending the code
            hub.pair(code, deviceId, "Téléphone ${Build.MODEL}")
        }
        p.edit().putString("hubUrl", url).putString("deviceId", id).putString("token", token).apply()
        lastError = null
        lastSuccess = null
        publish(app, busy = false)
        schedule(app)
        run(app)
    }

    /** Forgets the hub on this phone. Nothing is deleted, here or on the hub; the device id is kept so that
     * pairing again resumes where it stopped. */
    fun unpair(context: Context) {
        prefs(context).edit().remove("hubUrl").remove("token").apply()
        lastError = null
        lastSuccess = null
        publish(context, busy = false)
    }

    private val online = Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()

    /** The steady background pace (survives the app being closed and the phone restarting). */
    fun schedule(context: Context) {
        WorkManager.getInstance(context).enqueueUniquePeriodicWork(
            "akashic-sync",
            ExistingPeriodicWorkPolicy.KEEP,
            PeriodicWorkRequestBuilder<SyncWorker>(15, TimeUnit.MINUTES).setConstraints(online).build(),
        )
    }

    /** A sync soon, outside the periodic pace: app brought to the front (no delay) or left (what was just
     * written, a few seconds later so the last auto-save has landed). Collapses repeated requests. */
    fun requestSoon(context: Context, delaySeconds: Long = 0) {
        if (!_state.value.paired) return
        WorkManager.getInstance(context).enqueueUniqueWork(
            "akashic-sync-now",
            ExistingWorkPolicy.REPLACE,
            OneTimeWorkRequestBuilder<SyncWorker>().setConstraints(online).setInitialDelay(delaySeconds, TimeUnit.SECONDS).build(),
        )
    }
}

class SyncWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        SyncService.run(applicationContext)
        return Result.success() // an unreachable hub is not a failure to retry hard: the next period tries again
    }
}
