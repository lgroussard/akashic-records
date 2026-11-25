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
