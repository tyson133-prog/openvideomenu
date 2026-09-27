package org.menustream.tv.plex

import android.os.Build
import java.io.IOException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withContext
import kotlinx.serialization.decodeFromString
import okhttp3.FormBody
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.OkHttpClient
import okhttp3.Request
import org.menustream.tv.BuildConfig
import org.menustream.tv.net.Http
import org.menustream.tv.net.json

/** Identifies this app to Plex on every request. */
class PlexIdentity(val clientId: String) {
    fun headers(token: String? = null): Map<String, String> = buildMap {
        put("Accept", "application/json")
        put("X-Plex-Product", "MenuStream")
        put("X-Plex-Version", BuildConfig.VERSION_NAME)
        put("X-Plex-Client-Identifier", clientId)
        put("X-Plex-Platform", "Android")
        put("X-Plex-Platform-Version", Build.VERSION.RELEASE)
        put("X-Plex-Device", Build.MODEL)
        put("X-Plex-Device-Name", "MenuStream (${Build.MODEL})")
        if (token != null) put("X-Plex-Token", token)
    }
}

private fun Request.Builder.headers(map: Map<String, String>) = apply { map.forEach { (k, v) -> header(k, v) } }

/** An HTTP error from Plex; [code] 401 means the sign-in is no longer valid. */
class HttpStatusException(val code: Int, url: String) : IOException("HTTP $code for ${url.substringBefore('?')}")

private suspend inline fun <reified T> OkHttpClient.getJson(url: String, headers: Map<String, String>): T =
    withContext(Dispatchers.IO) {
        newCall(Request.Builder().url(url).headers(headers).build()).execute().use { res ->
            if (!res.isSuccessful) throw HttpStatusException(res.code, url)
            json.decodeFromString<T>(res.body.string())
        }
    }

/** plex.tv account calls: sign-in PIN and the list of servers. */
class PlexAccount(private val identity: PlexIdentity, private val http: OkHttpClient = Http.client) {

    /** A short 4-character PIN the user enters at plex.tv/link. */
    suspend fun createPin(): PlexPin = withContext(Dispatchers.IO) {
        val req = Request.Builder()
            .url("https://plex.tv/api/v2/pins")
            .headers(identity.headers())
            .post(FormBody.Builder().build())
            .build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IOException("Couldn't start Plex sign-in (HTTP ${res.code})")
            json.decodeFromString<PlexPin>(res.body.string())
        }
    }

    /** The account token once the user has entered the code, otherwise null. */
    suspend fun checkPin(pin: PlexPin): String? =
        http.getJson<PlexPin>("https://plex.tv/api/v2/pins/${pin.id}", identity.headers()).authToken?.takeIf { it.isNotBlank() }

    suspend fun servers(accountToken: String): List<PlexResource> =
        http.getJson<List<PlexResource>>(
            "https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=1&includeIPv6=1",
            identity.headers(accountToken),
        ).filter { it.isServer }
}

/**
 * One Plex Media Server, reached at [baseUrl] with [token] (the server's own accessToken from plex.tv,
 * which also works for shared servers).
 */
class PlexServer(
    val name: String,
    val machineId: String,
    val baseUrl: String,
    val token: String,
    private val identity: PlexIdentity,
    private val http: OkHttpClient = Http.client,
) {
    private fun url(path: String, vararg params: Pair<String, String>): String =
        baseUrl.toHttpUrl().newBuilder().encodedPath(path.substringBefore('?')).apply {
            path.substringAfter('?', "").split('&').filter { it.isNotEmpty() }.forEach {
                addEncodedQueryParameter(it.substringBefore('='), it.substringAfter('=', ""))
            }
            params.forEach { (k, v) -> addQueryParameter(k, v) }
        }.build().toString()

    private suspend fun container(path: String, vararg params: Pair<String, String>): MediaContainer =
        http.getJson<MediaContainerResponse>(url(path, *params), identity.headers(token)).container

    suspend fun sections(): List<PlexDirectory> = container("/library/sections").directories

    suspend fun movies(sectionKey: String): List<PlexItem> =
        container("/library/sections/$sectionKey/all", "type" to "1").metadata

    /** A limited page of a list (Plex accepts the container range as query parameters too). */
    private suspend fun page(path: String, size: Int, vararg params: Pair<String, String>): List<PlexItem> =
        container(path, "X-Plex-Container-Start" to "0", "X-Plex-Container-Size" to "$size", *params).metadata

    suspend fun onDeck(sectionKey: String? = null, size: Int = 30): List<PlexItem> =
        page(if (sectionKey == null) "/library/onDeck" else "/library/sections/$sectionKey/onDeck", size)

    /** Recently added titles; [type] narrows a section to e.g. episodes ("4") instead of shows. */
    suspend fun recentlyAdded(sectionKey: String? = null, size: Int = 30, type: String? = null): List<PlexItem> =
        page(
            if (sectionKey == null) "/library/recentlyAdded" else "/library/sections/$sectionKey/recentlyAdded",
            size,
            *(if (type != null) arrayOf("type" to type) else emptyArray()),
        )

    /** The first letters that titles in a section start with, with counts ("#" for digits/symbols). */
    suspend fun letters(sectionKey: String): List<PlexLetter> =
        http.getJson<LettersResponse>(url("/library/sections/$sectionKey/firstCharacter"), identity.headers(token)).container.letters

    /** All titles in a section starting with [letter] (the key from [letters]). */
    suspend fun byLetter(sectionKey: String, letter: PlexLetter): List<PlexItem> =
        container("/library/sections/$sectionKey/firstCharacter/${letter.key}").metadata

    suspend fun search(query: String): List<PlexItem> =
        container("/hubs/search", "query" to query, "limit" to "20").hubs
            .filter { it.type == "movie" || it.type == "show" || it.type == "episode" }
            .flatMap { it.metadata }

    /** Seasons of a show, or episodes of a season. */
    suspend fun children(ratingKey: String): List<PlexItem> = container("/library/metadata/$ratingKey/children").metadata

    /** Every episode of a show, with file paths. */
    suspend fun allEpisodes(showKey: String): List<PlexItem> = container("/library/metadata/$showKey/allLeaves").metadata

    /** Movies in a section whose title contains [title] (Plex matches substrings). */
    suspend fun moviesTitled(sectionKey: String, title: String): List<PlexItem> =
        container("/library/sections/$sectionKey/all", "type" to "1", "title" to title).metadata

    /** Shows in a TV section with this exact title. */
    suspend fun showsTitled(sectionKey: String, title: String): List<PlexItem> =
        container("/library/sections/$sectionKey/all", "type" to "2", "title" to title).metadata
            .filter { it.title.equals(title, ignoreCase = true) }

    /** Full metadata for one item, including chapters and file paths. */
    suspend fun item(ratingKey: String): PlexItem? =
        container("/library/metadata/$ratingKey", "includeChapters" to "1").metadata.firstOrNull()

    /** Local extras (Trailers/, Featurettes/, Other/, ...) with their file paths. */
    suspend fun extras(ratingKey: String): List<PlexItem> = container("/library/metadata/$ratingKey/extras").metadata

    /**
     * Report playback to Plex so resume points and watched status stay in sync with other Plex apps.
     * Send on every state change and about every 10 s while playing; past Plex's watched threshold
     * (90% by default) a "stopped" report marks the item watched.
     */
    suspend fun timeline(ratingKey: String, state: String, positionMs: Long, durationMs: Long, sessionId: String) {
        val u = url(
            "/:/timeline",
            "ratingKey" to ratingKey,
            "key" to "/library/metadata/$ratingKey",
            "state" to state,
            "time" to "$positionMs",
            "duration" to "$durationMs",
        )
        withContext(Dispatchers.IO) {
            http.newCall(Request.Builder().url(u).headers(identity.headers(token) + ("X-Plex-Session-Identifier" to sessionId)).build())
                .execute().use { res ->
                    if (BuildConfig.DEBUG) android.util.Log.d("MenuStream", "timeline $state ${positionMs / 1000}s -> HTTP ${res.code}")
                }
        }
    }

    /**
     * Ask Plex to approve direct play of one file before streaming it. Plex's API docs require a
     * playback decision before a part is requested; without one the server may make an "ad-hoc"
     * decision, which clients "should not rely on" — PMS 1.43.4 answers such requests with 503.
     * The part must then be requested with the same X-Plex-Session-Identifier.
     */
    suspend fun directPlayDecision(ratingKey: String, mediaIndex: Int, partIndex: Int, sessionId: String): DecisionContainer =
        http.getJson<DecisionResponse>(
            url(
                "/video/:/transcode/universal/decision",
                "path" to "/library/metadata/$ratingKey",
                "mediaIndex" to "$mediaIndex",
                "partIndex" to "$partIndex",
                "directPlay" to "1",
                "directStream" to "1",
                "directStreamAudio" to "1",
                "protocol" to "http",
                "location" to "lan",
                // Decide on the file itself, not a guessed client profile (needs directPlay=1 and both indices).
                "hasMDE" to "1",
                "session" to sessionId,
            ),
            identity.headers(token) + ("X-Plex-Session-Identifier" to sessionId),
        ).container

    /**
     * URL for a file part. The token travels as a header, not in the URL. Request it with the session
     * id of a direct-play decision ([directPlayDecision]). [download] is the fallback when Plex won't
     * give a decision: it serves the raw file at download priority, and servers can forbid it for
     * shared users (403).
     */
    fun partUrl(part: PlexPart, download: Boolean = false): String =
        if (download) url(part.key, "download" to "1") else url(part.key)

    /** Headers needed to fetch [partUrl] and images. */
    fun authHeaders(): Map<String, String> = identity.headers(token)

    fun imageUrl(path: String, width: Int, height: Int): String =
        url("/photo/:/transcode", "url" to path, "width" to "$width", "height" to "$height", "minSize" to "1", "upscale" to "1")

    companion object {
        /**
         * Reconnect straight to a server used before, without plex.tv (e.g. the internet is down but the
         * home network isn't). Checks that the address still answers as the same server.
         */
        suspend fun reconnect(last: org.menustream.tv.store.LastServer, identity: PlexIdentity, http: OkHttpClient = Http.probe): PlexServer {
            val id = http.getJson<MediaContainerResponse>("${last.url}/identity", identity.headers(last.token)).container.machineIdentifier
            if (id != last.machineId) throw IOException("${last.url} is no longer ${last.name}")
            return PlexServer(last.name, last.machineId, last.url, last.token, identity)
        }

        /**
         * Try the server's addresses in the order python-plexapi uses (local before remote before relay,
         * https before http) and keep the first that answers /identity.
         */
        suspend fun connect(resource: PlexResource, identity: PlexIdentity, http: OkHttpClient = Http.probe): PlexServer {
            val token = resource.accessToken ?: throw IOException("No access token for ${resource.name}")
            val ordered = resource.connections.sortedWith(
                compareBy<PlexConnection>({ !it.local }, { it.relay }, { it.protocol != "https" }, { it.ipv6 }),
            )
            val working = coroutineScope {
                ordered.map { c ->
                    async(Dispatchers.IO) {
                        runCatching {
                            http.newCall(Request.Builder().url("${c.uri}/identity").headers(identity.headers(token)).build())
                                .execute().use { if (it.isSuccessful) c else null }
                        }.getOrNull()
                    }
                }.awaitAll()
            }
            val best = ordered.firstOrNull { it in working }
                ?: throw IOException("Couldn't reach ${resource.name} at any of its ${ordered.size} addresses")
            return PlexServer(resource.name, resource.clientIdentifier, best.uri, token, identity)
        }
    }
}
