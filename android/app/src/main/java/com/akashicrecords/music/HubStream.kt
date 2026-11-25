package com.akashicrecords.music

import android.content.Context
import android.net.Uri
import androidx.media3.datasource.DataSource
import androidx.media3.datasource.DataSpec
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.datasource.ResolvingDataSource
import com.akashicrecords.sync.SyncService
import java.io.IOException

// A track of the catalogue whose file is not on this phone is played straight from the hub.
//
// Its media item carries "akashic://music?k=<catalogue key>" rather than the hub's address: the address, the
// token and the file's hash are looked up when the player actually opens it. So a saved queue survives a change
// of hub address, the token is never written in the queue, and a track fetched onto the phone since ("Garder")
// is read from the phone instead of the network.
internal object HubStream {
    private const val SCHEME = "akashic"

    fun uri(key: String): Uri = Uri.Builder().scheme(SCHEME).authority("music").appendQueryParameter("k", key).build()

    fun isStream(uri: Uri?) = uri?.scheme == SCHEME

    /** The player's data source: everything it could already open, plus the hub's tracks. */
    fun factory(context: Context): DataSource.Factory {
        val app = context.applicationContext
        val http = DefaultHttpDataSource.Factory().setConnectTimeoutMs(10_000).setReadTimeoutMs(20_000)
        return ResolvingDataSource.Factory(DefaultDataSource.Factory(app, http)) { spec -> resolve(app, spec) }
    }

    private fun resolve(context: Context, spec: DataSpec): DataSpec {
        if (spec.uri.scheme != SCHEME) return spec
        val key = spec.uri.getQueryParameter("k") ?: throw IOException("titre inconnu")
        val library = MusicLibrary.get(context)
        library.available[key]?.let { return spec.withUri(it) }
        val (url, token) = SyncService.credentials(context) ?: throw IOException("Ce téléphone n'est pas relié au serveur.")
        val hash = library.hashOf(key) ?: throw IOException("Ce titre n'est pas encore sur le serveur.")
        return spec.buildUpon()
            .setUri(Uri.parse("$url/media/$hash"))
            .setHttpRequestHeaders(spec.httpRequestHeaders + ("Authorization" to "Bearer $token"))
            .build()
    }
}
