package com.akashicrecords.sync

import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

/** A failure worth showing to the user as it is (French, no stack trace). */
class HubException(message: String, val status: Int? = null) : Exception(message)

/**
 * Talks to the sync hub over HTTP (same contract as the desktop's AkashicRecords.Sync.HubClient).
 * Blocking calls: always used from Dispatchers.IO.
 */
class HubClient(hubUrl: String, private val token: String? = null) {
    private val base = hubUrl.trim().trimEnd('/')

    fun health(): JSONObject = call("GET", "/health", null)

    /** Exchanges a one-time code (shown on the hub) for this device's token: returns (deviceId, token). */
    fun pair(code: String, deviceId: String, deviceName: String): Pair<String, String> {
        val body = JSONObject()
            .put("code", code.filter(Char::isDigit))
            .put("device_id", deviceId)
            .put("device_name", deviceName)
        val r = try {
            call("POST", "/auth/pair", body)
        } catch (e: HubException) {
            if (e.status == 403) throw HubException("Code d'appairage refusé : faux, expiré ou déjà utilisé.", 403)
            throw e
        }
        return r.getString("device_id") to r.getString("token")
    }

    fun push(mutations: JSONArray): JSONObject = call("POST", "/sync/push", JSONObject().put("mutations", mutations))

    fun pull(since: Long, limit: Int): JSONObject = call("GET", "/sync/pull?since=$since&limit=$limit", null)

    // ---- The download board shared by every device; the hub itself downloads (see the hub's Downloads.cs).

    fun downloads(): JSONObject = call("GET", "/downloads", null)

    fun addDownload(query: String, label: String, zoneId: String?): JSONObject =
        call("POST", "/downloads/jobs", JSONObject().put("query", query).put("label", label).put("zone_id", zoneId ?: JSONObject.NULL))

    fun moveDownload(id: String, zoneId: String?): JSONObject =
        call("POST", "/downloads/jobs/$id/zone", JSONObject().put("zone_id", zoneId ?: JSONObject.NULL))

    fun retryDownload(id: String): JSONObject = call("POST", "/downloads/jobs/$id/retry", JSONObject())
    fun removeDownload(id: String): JSONObject = call("DELETE", "/downloads/jobs/$id", null)
    fun clearDoneDownloads(): JSONObject = call("POST", "/downloads/clear-done", JSONObject())

    /** The zone for [folder] (an existing music folder, or a new one): the hub reuses the zone it may already have. */
    fun addZone(name: String?, folder: String?): JSONObject =
        call("POST", "/downloads/zones", JSONObject().put("name", name ?: JSONObject.NULL).put("folder", folder ?: JSONObject.NULL))

    fun renameZone(id: String, name: String): JSONObject = call("POST", "/downloads/zones/$id", JSONObject().put("name", name))
    fun removeZone(id: String): JSONObject = call("DELETE", "/downloads/zones/$id", null)

    // ---- Shared computing (the hub's Compute.cs). The phone only asks and looks: it never takes jobs, the PC or
    // the hub run them.

    fun compute(): JSONObject = call("GET", "/compute", null)

    /** Asks for an image already sent with [uploadStream] to be upscaled with [model]. */
    fun addComputeJob(model: String, inputHash: String, inputName: String): JSONObject =
        call("POST", "/compute/jobs", JSONObject().put("model", model).put("input_hash", inputHash).put("input_name", inputName))

    fun retryCompute(id: String): JSONObject = call("POST", "/compute/jobs/$id/retry", JSONObject())
    fun removeCompute(id: String): JSONObject = call("DELETE", "/compute/jobs/$id", null)

    // ---- Files (blobs addressed by the SHA-256 of their content). They can be large and slow to move, so these
    // calls stream and have a much longer time limit.

    private fun open(method: String, path: String): HttpURLConnection =
        (URL(base + path).openConnection() as HttpURLConnection).apply {
            requestMethod = method
            connectTimeout = 4_000
            readTimeout = 120_000
            token?.let { setRequestProperty("Authorization", "Bearer $it") }
        }

    private inline fun <T> transfer(block: () -> T): T =
        try {
            block()
        } catch (e: IOException) {
            throw HubException("Hub injoignable (${e.message ?: e.javaClass.simpleName}).")
        }

    /** Does the hub already hold this content? */
    fun hasFile(hash: String): Boolean = transfer {
        val conn = open("HEAD", "/media/$hash")
        try {
            when (val status = conn.responseCode) {
                in 200..299 -> true
                404 -> false
                401 -> throw HubException("Le hub refuse ce jeton : appareil non appairé ou révoqué.", 401)
                else -> throw HubException("Le hub a répondu $status.", status)
            }
        } finally {
            conn.disconnect()
        }
    }

    fun uploadFile(file: java.io.File, hash: String) = uploadStream(file.length(), hash) { file.inputStream() }

    /** Sends [length] bytes read from [open] (a file, or a content of the shared storage). */
    fun uploadStream(length: Long, hash: String, open: () -> java.io.InputStream) = transfer {
        val conn = open("POST", "/media?hash=$hash")
        try {
            conn.doOutput = true
            conn.setFixedLengthStreamingMode(length) // streamed, never held in memory
            conn.setRequestProperty("Content-Type", "application/octet-stream")
            open().use { input -> conn.outputStream.use { input.copyTo(it) } }
            val status = conn.responseCode
            if (status !in 200..299) throw HubException("Envoi d'un fichier refusé par le hub ($status).", status)
        } finally {
            conn.disconnect()
        }
    }

    /** Downloads a content to [target] (written beside it first, moved once complete and verified). False when
     * the hub does not have it (yet). */
    fun downloadFile(hash: String, target: java.io.File): Boolean = transfer {
        val conn = open("GET", "/media/$hash")
        try {
            val status = conn.responseCode
            if (status == 404) return@transfer false
            if (status !in 200..299) throw HubException("Téléchargement d'un fichier refusé par le hub ($status).", status)
            target.parentFile?.mkdirs()
            val temp = java.io.File(target.path + ".part")
            try {
                val sha = java.security.MessageDigest.getInstance("SHA-256")
                // A file this phone cannot write is not the hub's fault: said as such (not "hub unreachable").
                val output = try { temp.outputStream() } catch (e: IOException) { throw IllegalStateException("impossible d'écrire ${target.name} sur ce téléphone (${e.message})") }
                conn.inputStream.use { input ->
                    output.use { out ->
                        val buffer = ByteArray(81920)
                        while (true) {
                            val n = input.read(buffer)
                            if (n < 0) break
                            sha.update(buffer, 0, n)
                            out.write(buffer, 0, n)
                        }
                    }
                }
                val actual = sha.digest().joinToString("") { "%02x".format(it) }
                if (actual != hash) throw HubException("Fichier reçu différent de son empreinte : ignoré.")
                if (!temp.renameTo(target)) throw IllegalStateException("impossible d'écrire ${target.name} sur ce téléphone")
                true
            } finally {
                temp.delete()
            }
        } finally {
            conn.disconnect()
        }
    }

    private fun call(method: String, path: String, body: JSONObject?): JSONObject {
        val conn = try {
            (URL(base + path).openConnection() as HttpURLConnection).apply {
                requestMethod = method
                connectTimeout = 4_000 // away from home the hub is simply not there: fail fast
                readTimeout = 20_000
                setRequestProperty("Accept", "application/json")
                token?.let { setRequestProperty("Authorization", "Bearer $it") }
                if (body != null) {
                    doOutput = true
                    setRequestProperty("Content-Type", "application/json; charset=utf-8")
                    outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
                }
            }
        } catch (e: IOException) {
            throw HubException("Hub injoignable (${e.message ?: e.javaClass.simpleName}).")
        } catch (e: IllegalArgumentException) {
            throw HubException("Adresse du hub invalide.")
        }

        try {
            val status = try {
                conn.responseCode
            } catch (e: IOException) {
                throw HubException("Hub injoignable (${e.message ?: e.javaClass.simpleName}).")
            }
            if (status == 401) throw HubException("Le hub refuse ce jeton : appareil non appairé ou révoqué.", 401)
            val text = (if (status in 200..299) conn.inputStream else conn.errorStream)
                ?.use { it.readBytes().toString(Charsets.UTF_8) }.orEmpty()
            if (status !in 200..299) throw HubException("Le hub a répondu $status : ${text.take(200)}", status)
            return try {
                JSONObject(text)
            } catch (e: org.json.JSONException) {
                throw HubException("Réponse illisible du hub.")
            }
        } finally {
            conn.disconnect()
        }
    }
}
