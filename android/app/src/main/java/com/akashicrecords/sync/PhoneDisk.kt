package com.akashicrecords.sync

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.os.StatFs
import android.provider.Settings
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.time.Instant
import java.util.concurrent.atomic.AtomicLong

/**
 * This phone's own storage, folder by folder, for the Serveur screen's « Ce téléphone » (a TreeSize of the phone).
 * Same rules as the mini-PC's and the PC's: every folder of 1 MB or more is kept, smaller ones are summed up per
 * parent, each folder keeps its 20 largest files; symbolic links are not followed. Measured on demand, the last
 * measure kept in the app's files (disk-index.json). Reading the whole storage needs Android's « Accès à tous les
 * fichiers », given once in the settings. What Android keeps to itself (the system, the apps and their data) is
 * one line, so that the sizes add up to the phone's. Read-only: nothing is ever changed.
 *
 * The views have the hub's /server/disk shape (DiskBrowser shows both); files carry their full path in "label".
 */
object PhoneDisk {
    private const val KEEP_FOLDER = 1L shl 20
    private const val KEEP_FILES = 20
    const val UNSEEN = "Système, applications et leurs données"

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var job: Job? = null
    private val seen = AtomicLong()
    @Volatile private var doc: JSONObject? = null

    val measuring get() = job?.isActive == true
    val filesSeen get() = seen.get()

    private fun root(): File = Environment.getExternalStorageDirectory()
    private fun cache(context: Context) = File(context.filesDir, "disk-index.json")

    fun hasAccess(context: Context): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) Environment.isExternalStorageManager()
        else context.checkSelfPermission(Manifest.permission.READ_EXTERNAL_STORAGE) == PackageManager.PERMISSION_GRANTED

    /** Opens the settings page where the access is given (this app's own page). */
    fun askAccess(context: Context) {
        val uri = Uri.parse("package:" + context.packageName)
        val intent = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R)
            Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, uri)
        else Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, uri)
        runCatching { context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
            .onFailure { runCatching { context.startActivity(Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) } }
    }

    /** Starts measuring in the background (nothing when one is already running). */
    @Synchronized
    fun startMeasure(context: Context) {
        if (measuring) return
        val app = context.applicationContext
        seen.set(0)
        job = scope.launch { measure(app) }
    }

    private fun measure(context: Context) {
        val started = System.currentTimeMillis()
        val root = root()
        val tree = folder(root)
        tree.put("n", "Ce téléphone")
        val stat = StatFs(root.path)
        val total = stat.totalBytes
        val free = stat.availableBytes
        // What Android does not let an app read: the system, the apps, their data.
        val unseen = total - free - tree.getLong("s")
        if (unseen > KEEP_FOLDER) {
            val items = tree.optJSONArray("d")?.let { a -> (0 until a.length()).map { a.getJSONObject(it) } }.orEmpty() +
                JSONObject().put("n", UNSEEN).put("s", unseen).put("u", true)
            tree.put("d", JSONArray(items.sortedByDescending { it.getLong("s") }))
            tree.put("s", tree.getLong("s") + unseen)
        }
        val d = JSONObject()
            .put("at", Instant.now().toString())
            .put("seconds", (System.currentTimeMillis() - started) / 1000)
            .put("total", total).put("free", free)
            .put("tree", tree)
        runCatching {
            val f = cache(context)
            val part = File(f.path + ".part")
            part.writeText(d.toString())
            part.renameTo(f)
        }
        doc = d
    }

    private fun isLink(f: File) = runCatching { java.nio.file.Files.isSymbolicLink(f.toPath()) }.getOrDefault(false)

    private fun folder(dir: File): JSONObject {
        val node = JSONObject().put("n", dir.name)
        val entries = dir.listFiles()
        if (entries == null) return node.put("s", 0L).put("x", true) // not readable (Android/data…)
        var size = 0L; var files = 0L; var smallBytes = 0L; var smallCount = 0L
        val children = mutableListOf<JSONObject>()
        val top = mutableListOf<Pair<String, Long>>()
        for (e in entries) {
            if (isLink(e)) continue
            if (e.isDirectory) {
                val child = folder(e)
                val s = child.getLong("s")
                size += s
                files += child.optLong("f")
                if (s >= KEEP_FOLDER || child.has("x")) children += child else { smallBytes += s; smallCount++ }
            } else {
                val len = e.length()
                size += len; files++
                seen.incrementAndGet()
                top += e.name to len
            }
        }
        node.put("s", size).put("f", files)
        if (children.isNotEmpty()) node.put("d", JSONArray(children.sortedByDescending { it.getLong("s") }))
        val kept = top.sortedByDescending { it.second }.take(KEEP_FILES)
        if (kept.isNotEmpty()) node.put("t", JSONArray(kept.map { JSONArray().put(it.first).put(it.second) }))
        if (smallCount > 0) node.put("o", smallBytes).put("oc", smallCount)
        return node
    }

    private fun load(context: Context): JSONObject? {
        doc?.let { return it }
        return runCatching { JSONObject(cache(context).readText()) }.getOrNull()?.also { doc = it }
    }

    private fun kindOf(name: String) = when (name.substringAfterLast('.', "").lowercase()) {
        "jpg", "jpeg", "png", "webp", "heic", "heif", "gif", "bmp", "avif" -> "image"
        "mp3", "flac", "m4a", "ogg", "opus", "wav", "aac" -> "music"
        "epub" -> "book"
        "pdf", "doc", "docx", "odt", "txt" -> "document"
        "mp4", "mkv", "avi", "mov", "webm", "3gp" -> "video"
        else -> "other"
    }

    /** One folder of the last measure; "/" = the whole phone. */
    fun browse(context: Context, path: String): JSONObject {
        val d = load(context)
            ?: return JSONObject().put("state", "none").put("refreshing", measuring).put("path", "/").put("root", "Ce téléphone")
        var node = d.getJSONObject("tree")
        val walked = mutableListOf<String>()
        for (part in path.split('/').filter { it.isNotEmpty() }) {
            val list = node.optJSONArray("d") ?: break
            val next = (0 until list.length()).map { list.getJSONObject(it) }.firstOrNull { it.optString("n") == part && !it.has("u") } ?: break
            node = next
            walked += part
        }
        val here = "/" + walked.joinToString("/")
        val dir = File(root(), walked.joinToString("/"))
        val folders = JSONArray()
        var folderBytes = 0L
        node.optJSONArray("d")?.let { a ->
            for (i in 0 until a.length()) {
                val c = a.getJSONObject(i)
                folderBytes += c.getLong("s")
                folders.put(
                    JSONObject().put("name", c.getString("n")).put("size", c.getLong("s")).put("files", c.optLong("f"))
                        .put("denied", c.has("x") || c.has("u")).put("mount", false)
                        .put("has_children", c.has("d") || c.has("t")),
                )
            }
        }
        val largest = JSONArray()
        var largestBytes = 0L
        node.optJSONArray("t")?.let { a ->
            for (i in 0 until a.length()) {
                val t = a.getJSONArray(i)
                val name = t.getString(0)
                largestBytes += t.getLong(1)
                largest.put(JSONObject().put("name", name).put("size", t.getLong(1)).put("kind", kindOf(name)).put("label", File(dir, name).path))
            }
        }
        val size = node.getLong("s")
        val small = node.optLong("o")
        return JSONObject()
            .put("state", "ready").put("at", d.optString("at")).put("seconds", d.optLong("seconds"))
            .put("refreshing", measuring).put("root", "Ce téléphone").put("path", here)
            .put("size", size).put("files", node.optLong("f"))
            .put("folders", folders).put("largest", largest)
            .put("other_files_bytes", maxOf(0L, size - folderBytes - small - largestBytes))
            .put("small_folders_bytes", small).put("small_folders", node.optLong("oc"))
            .put("local", true).put("disk_total", d.optLong("total")).put("disk_free", d.optLong("free"))
    }
}
