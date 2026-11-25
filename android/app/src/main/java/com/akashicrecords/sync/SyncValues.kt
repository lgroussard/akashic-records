package com.akashicrecords.sync

import org.json.JSONObject
import java.security.MessageDigest
import java.util.SortedMap
import java.util.TreeMap

/**
 * The pure part of the sync engine: how a row's content is written down, hashed and compared.
 * No Android class here, so it is covered by plain JVM tests.
 * A row is a sorted map column -> Long / Double / String / null.
 */
internal object SyncValues {

    /** One text per content: keys sorted, strings JSON-quoted. Only ever compared with itself on this device. */
    fun canonical(values: SortedMap<String, Any?>): String = buildString {
        append('{')
        var first = true
        for ((k, v) in values) {
            if (!first) append(',')
            first = false
            append(JSONObject.quote(k)).append(':')
            when (v) {
                null -> append("null")
                is Long -> append(v)
                is Double -> append(v)
                else -> append(JSONObject.quote(v.toString()))
            }
        }
        append('}')
    }

    fun sha(text: String): String =
        MessageDigest.getInstance("SHA-256").digest(text.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }

    /** Derived from the content: sending the same change twice is the same mutation for the hub. */
    fun mutationId(entityType: String, syncId: String, baseRev: Long, hash: String?): String =
        sha("$entityType|$syncId|$baseRev|${hash ?: "deleted"}")

    /** What goes to the hub for a row. */
    fun toPayload(values: SortedMap<String, Any?>): JSONObject {
        val o = JSONObject()
        for ((k, v) in values) o.put(k, v ?: JSONObject.NULL)
        return o
    }

    /**
     * The part of a hub payload this database has columns for, in the types SQLite gives back for them
     * (or 19 and 19.0 would look different). A field this version does not know is ignored; a column the
     * payload does not carry is absent from the result and is left alone.
     * [columns]: name -> true when the column holds reals.
     */
    fun project(payload: JSONObject, columns: Map<String, Boolean>): SortedMap<String, Any?> {
        val out = TreeMap<String, Any?>()
        for ((name, isReal) in columns) {
            if (!payload.has(name)) continue
            val v = payload.get(name)
            out[name] = when {
                v == JSONObject.NULL -> null
                v is String -> v
                v is Boolean -> if (v) 1L else 0L
                v is Number -> when {
                    isReal -> v.toDouble()
                    v is Double || v is Float -> if (v.toDouble() == Math.rint(v.toDouble())) v.toLong() else v.toDouble()
                    else -> v.toLong()
                }
                else -> v.toString()
            }
        }
        return out
    }

    /** True when the local row holds exactly what the payload says, for every field the payload carries. */
    fun matches(local: Map<String, Any?>, payload: Map<String, Any?>): Boolean =
        payload.isNotEmpty() && payload.all { (k, v) -> local.containsKey(k) && local[k] == v }
}
