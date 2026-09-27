package org.menustream.tv.jellyfin

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.menustream.tv.BuildConfig
import org.menustream.tv.attempt
import org.menustream.tv.media.Item
import org.menustream.tv.media.ItemType
import org.menustream.tv.media.Letter
import org.menustream.tv.media.Library
import org.menustream.tv.media.LibraryKind
import org.menustream.tv.media.MediaFile
import org.menustream.tv.media.MediaSource
import org.menustream.tv.media.Page
import org.menustream.tv.net.Http
import org.menustream.tv.net.json
import org.menustream.tv.player.PlayRequest
import org.menustream.tv.player.PlaybackReporter
import org.menustream.tv.plex.HttpStatusException

// --- API models (Jellyfin uses PascalCase JSON) ---------------------------------------------------

@Serializable
data class JfPublicInfo(
    @SerialName("ServerName") val serverName: String = "",
    @SerialName("Id") val id: String = "",
    @SerialName("Version") val version: String = "",
)

@Serializable
data class JfQuickConnect(
    @SerialName("Secret") val secret: String = "",
    @SerialName("Code") val code: String = "",
    @SerialName("Authenticated") val authenticated: Boolean = false,
)

@Serializable
data class JfUser(@SerialName("Id") val id: String, @SerialName("Name") val name: String = "")

@Serializable
data class JfAuthResult(
    @SerialName("AccessToken") val accessToken: String,
    @SerialName("User") val user: JfUser,
    @SerialName("ServerId") val serverId: String = "",
)

@Serializable
data class JfItems(@SerialName("Items") val items: List<JfItem> = emptyList(), @SerialName("TotalRecordCount") val total: Int? = null)

@Serializable
data class JfUserData(
    @SerialName("PlaybackPositionTicks") val positionTicks: Long = 0,
    @SerialName("Played") val played: Boolean = false,
)

@Serializable
data class JfMediaSource(@SerialName("Id") val id: String, @SerialName("Path") val path: String? = null)

@Serializable
data class JfItem(
    @SerialName("Id") val id: String,
    @SerialName("Name") val name: String = "",
    @SerialName("Type") val type: String = "",
    @SerialName("CollectionType") val collectionType: String? = null,
    @SerialName("ProductionYear") val year: Int? = null,
    @SerialName("SeriesId") val seriesId: String? = null,
    @SerialName("SeriesName") val seriesName: String? = null,
    @SerialName("SeasonId") val seasonId: String? = null,
    @SerialName("ParentIndexNumber") val parentIndex: Int? = null,
    @SerialName("IndexNumber") val index: Int? = null,
    @SerialName("ImageTags") val imageTags: Map<String, String> = emptyMap(),
    @SerialName("SeriesPrimaryImageTag") val seriesPrimaryImageTag: String? = null,
    @SerialName("RunTimeTicks") val runTimeTicks: Long? = null,
    @SerialName("UserData") val userData: JfUserData? = null,
    @SerialName("Path") val path: String? = null,
    @SerialName("MediaSources") val mediaSources: List<JfMediaSource> = emptyList(),
)

@Serializable
data class JfPlaybackInfo(@SerialName("PlaySessionId") val playSessionId: String? = null)

/** Jellyfin durations and positions are .NET ticks: 100 ns. */
private const val TICKS_PER_SECOND = 10_000_000.0

/** Optional fields every list asks for: file paths (for menu matching) and media sources (for playback). */
private const val FIELDS = "Path,MediaSources"

// --- HTTP ----------------------------------------------------------------------------------------

/**
 * The `Authorization: MediaBrowser ...` header Jellyfin requires on every call (Jellyfin 12 turned the
 * older X-Emby-Token header and api_key parameter off by default). [token] is added once signed in.
 */
fun jellyfinAuthorization(deviceId: String, token: String? = null): String {
    fun q(v: String) = "\"" + v.replace("\"", "'") + "\""
    return "MediaBrowser Client=${q("MenuStream")}, Device=${q(Build.MODEL ?: "Android TV")}, " +
        "DeviceId=${q(deviceId)}, Version=${q(BuildConfig.VERSION_NAME)}" + (token?.let { ", Token=${q(it)}" } ?: "")
}

private class JfHttp(val baseUrl: String, val deviceId: String, var token: String? = null, val http: OkHttpClient = Http.client) {
    val headers get() = mapOf("Authorization" to jellyfinAuthorization(deviceId, token), "Accept" to "application/json")

    fun url(path: String, vararg params: Pair<String, Any?>): String =
        baseUrl.toHttpUrl().newBuilder().encodedPath(path).apply {
            params.forEach { (k, v) -> if (v != null) addQueryParameter(k, v.toString()) }
        }.build().toString()

    suspend fun call(url: String, body: String? = null): String = withContext(Dispatchers.IO) {
        val req = Request.Builder().url(url).apply {
            headers.forEach { (k, v) -> header(k, v) }
            if (body != null) post(body.toRequestBody("application/json".toMediaType()))
        }.build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw HttpStatusException(res.code, url)
            res.body.string()
        }
    }

    suspend inline fun <reified T> get(path: String, vararg params: Pair<String, Any?>): T =
        json.decodeFromString<T>(call(url(path, *params)))

    suspend inline fun <reified T> post(path: String, body: String, vararg params: Pair<String, Any?>): T =
        json.decodeFromString<T>(call(url(path, *params), body))
}

// --- sign-in -------------------------------------------------------------------------------------

/**
 * Signing in to one Jellyfin server with Quick Connect: the TV shows a code, the viewer approves it
 * from another Jellyfin app (profile → Quick Connect), and the server hands this device a token.
 */
class JellyfinAccount(baseUrl: String, deviceId: String) {
    private val http = JfHttp(baseUrl.trimEnd('/'), deviceId, http = Http.probe)
    val baseUrl get() = http.baseUrl

    suspend fun publicInfo(): JfPublicInfo = http.get("/System/Info/Public")
    suspend fun quickConnectEnabled(): Boolean = http.call(http.url("/QuickConnect/Enabled")).trim() == "true"
    suspend fun initiate(): JfQuickConnect = http.post("/QuickConnect/Initiate", "")
    suspend fun check(secret: String): JfQuickConnect = http.get("/QuickConnect/Connect", "secret" to secret)
    suspend fun authenticate(secret: String): JfAuthResult =
        http.post("/Users/AuthenticateWithQuickConnect", buildJsonObject { put("Secret", secret) }.toString())
}

/** A Jellyfin server found on the home network. */
data class FoundJellyfin(val name: String, val url: String, val id: String)

/**
 * Jellyfin's LAN discovery: broadcast "who is JellyfinServer?" to UDP 7359 and collect answers for
 * [timeoutMs]. The server's own idea of its address can be one the TV can't reach (e.g. a Docker
 * container address), so the address used is where the answer came from, with the port it reports.
 */
suspend fun discoverJellyfin(context: Context, timeoutMs: Int = 2500): List<FoundJellyfin> = withContext(Dispatchers.IO) {
    val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
    val lock = wifi.createMulticastLock("menustream-jellyfin").apply { setReferenceCounted(false); acquire() }
    val found = linkedMapOf<String, FoundJellyfin>()
    try {
        DatagramSocket().use { socket ->
            socket.broadcast = true
            socket.soTimeout = 400
            val ask = "who is JellyfinServer?".toByteArray()
            socket.send(DatagramPacket(ask, ask.size, InetAddress.getByName("255.255.255.255"), 7359))
            val deadline = System.currentTimeMillis() + timeoutMs
            val buf = ByteArray(4096)
            while (System.currentTimeMillis() < deadline) {
                val packet = DatagramPacket(buf, buf.size)
                try {
                    socket.receive(packet)
                } catch (_: SocketTimeoutException) {
                    continue
                }
                val reply = attempt { json.decodeFromString<DiscoveryReply>(String(packet.data, 0, packet.length)) }.getOrNull() ?: continue
                val port = attempt { reply.address.toHttpUrl().port }.getOrDefault(8096)
                val host = packet.address.hostAddress ?: continue
                found[reply.id] = FoundJellyfin(reply.name, "http://$host:$port", reply.id)
            }
        }
    } finally {
        lock.release()
    }
    found.values.toList()
}

@Serializable
private data class DiscoveryReply(
    @SerialName("Address") val address: String = "",
    @SerialName("Id") val id: String = "",
    @SerialName("Name") val name: String = "",
)

// --- the media source ----------------------------------------------------------------------------

/** [MediaSource] for a Jellyfin server the viewer has signed in to. */
class JellyfinSource(
    baseUrl: String,
    deviceId: String,
    token: String,
    private val userId: String,
    override val name: String,
) : MediaSource {
    private val http = JfHttp(baseUrl.trimEnd('/'), deviceId, token)
    override val kind = "Jellyfin"
    override val assetServerAuth get() = mapOf("Authorization" to jellyfinAuthorization(http.deviceId, http.token))
    val baseUrl get() = http.baseUrl

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    override suspend fun libraries(): List<Library> =
        http.get<JfItems>("/UserViews", "userId" to userId).items.mapNotNull { v ->
            when (v.collectionType) {
                "movies" -> Library(v.id, v.name, LibraryKind.MOVIES)
                "tvshows" -> Library(v.id, v.name, LibraryKind.SHOWS)
                else -> null
            }
        }

    override suspend fun continueWatching(library: Library?) =
        http.get<JfItems>("/UserItems/Resume", "userId" to userId, "parentId" to library?.id, "limit" to 30, "mediaTypes" to "Video", "fields" to FIELDS)
            .items.map(::toItem)

    override suspend fun recentlyAdded(library: Library?, size: Int) =
        http.get<List<JfItem>>("/Items/Latest", "userId" to userId, "parentId" to library?.id, "limit" to size, "fields" to FIELDS).map(::toItem)

    override suspend fun recentEpisodes(library: Library, size: Int) =
        http.get<List<JfItem>>(
            "/Items/Latest", "userId" to userId, "parentId" to library.id, "includeItemTypes" to "Episode",
            "groupItems" to false, "limit" to size, "fields" to FIELDS,
        ).map(::toItem)

    /** Jellyfin has no letter index endpoint: offer # and A–Z, filtered by name. */
    override suspend fun letters(library: Library) = listOf(Letter("#", "#")) + ('A'..'Z').map { Letter("$it", "$it") }

    override suspend fun byLetter(library: Library, letter: Letter) =
        http.get<JfItems>(
            "/Items", "userId" to userId, "parentId" to library.id, "includeItemTypes" to typesOf(library), "recursive" to true,
            "sortBy" to "SortName", "fields" to FIELDS,
            *(if (letter.key == "#") arrayOf("nameLessThan" to "A") else arrayOf("nameStartsWith" to letter.key)),
        ).items.map(::toItem)

    override suspend fun search(query: String) =
        http.get<JfItems>(
            "/Items", "userId" to userId, "searchTerm" to query, "includeItemTypes" to "Movie,Series,Episode",
            "recursive" to true, "limit" to 20, "fields" to FIELDS,
        ).items.map(::toItem)

    override suspend fun children(item: Item) = when (item.type) {
        ItemType.SHOW -> http.get<JfItems>("/Shows/${item.id}/Seasons", "userId" to userId).items.map(::toItem)
        ItemType.SEASON -> http.get<JfItems>(
            "/Shows/${item.showId}/Episodes", "userId" to userId, "seasonId" to item.id, "fields" to FIELDS,
        ).items.map(::toItem)
        else -> emptyList()
    }

    override suspend fun allEpisodes(show: Item) =
        http.get<JfItems>("/Shows/${show.id}/Episodes", "userId" to userId, "fields" to FIELDS).items.map(::toItem)

    override suspend fun showsTitled(library: Library, title: String) =
        http.get<JfItems>(
            "/Items", "userId" to userId, "parentId" to library.id, "includeItemTypes" to "Series",
            "recursive" to true, "searchTerm" to title,
        ).items.filter { it.name.equals(title, ignoreCase = true) }.map(::toItem)

    override suspend fun item(id: String): Item? = attempt { toItem(http.get<JfItem>("/Items/$id", "userId" to userId)) }.getOrNull()

    /** Local extras: special features (featurettes, behind the scenes, ...) plus local trailers. */
    override suspend fun extras(item: Item): List<Item> {
        val special = http.get<List<JfItem>>("/Items/${item.id}/SpecialFeatures", "userId" to userId)
        val trailers = attempt { http.get<List<JfItem>>("/Items/${item.id}/LocalTrailers", "userId" to userId) }.getOrDefault(emptyList())
        // Extras lists may leave out file paths; fetch those items in full so menus can match them.
        return (special + trailers).map { e -> if (e.mediaSources.isEmpty() && e.path == null) item(e.id) ?: toItem(e) else toItem(e) }
    }

    override suspend fun moviesPage(library: Library, start: Int, size: Int): Page =
        http.get<JfItems>(
            "/Items", "userId" to userId, "parentId" to library.id, "includeItemTypes" to "Movie", "recursive" to true,
            "sortBy" to "SortName", "startIndex" to start, "limit" to size, "fields" to FIELDS, "enableTotalRecordCount" to true,
        ).let { Page(it.items.map(::toItem), it.total) }

    override fun imageUrl(image: String, width: Int, height: Int): String {
        val (id, tag) = image.split('|', limit = 2)
        return http.url("/Items/$id/Images/Primary", "tag" to tag, "fillWidth" to width, "fillHeight" to height, "quality" to 90)
    }

    override fun imageHeaders() = http.headers

    /**
     * Direct play: ask the server for a play session for this file (PlaybackInfo), then stream the file
     * as-is (static=true) with that session id, and report progress under it. Transcoding comes later.
     */
    override suspend fun playRequest(item: Item, file: MediaFile, startSeconds: Double, audio: Int?, subtitle: Int?): PlayRequest {
        val body = buildJsonObject {
            put("UserId", userId)
            put("MediaSourceId", file.ref)
            put("StartTimeTicks", (startSeconds * TICKS_PER_SECOND).toLong())
            put("EnableDirectPlay", true)
            put("EnableDirectStream", true)
            put("EnableTranscoding", false)
            put("AutoOpenLiveStream", false)
            put("IsPlayback", true)
        }.toString()
        val session = attempt { http.post<JfPlaybackInfo>("/Items/${item.id}/PlaybackInfo", body).playSessionId }.getOrNull()
            ?: java.util.UUID.randomUUID().toString().replace("-", "")
        return PlayRequest(
            url = http.url("/Videos/${item.id}/stream", "static" to true, "mediaSourceId" to file.ref, "playSessionId" to session),
            headers = http.headers,
            startSeconds = startSeconds,
            title = item.title,
            reporter = reporterFor(item, file, session),
            audioTrack = audio,
            subtitleTrack = subtitle,
        )
    }

    /**
     * Playback reports (start, progress, stopped), sent in order by one worker so a late "progress"
     * never lands after "stopped". Failures are ignored: playback matters more than the report.
     */
    private fun reporterFor(item: Item, file: MediaFile, session: String): PlaybackReporter {
        data class Report(val state: String, val positionMs: Long)
        val queue = Channel<Report>(Channel.UNLIMITED)
        scope.launch {
            var started = false
            for (r in queue) {
                val body = buildJsonObject {
                    put("ItemId", item.id)
                    put("MediaSourceId", file.ref)
                    put("PlaySessionId", session)
                    put("PositionTicks", (r.positionMs / 1000.0 * TICKS_PER_SECOND).toLong())
                    put("IsPaused", r.state == "paused")
                    put("CanSeek", true)
                    put("PlayMethod", "DirectPlay")
                }.toString()
                val path = when {
                    r.state == "stopped" -> "/Sessions/Playing/Stopped"
                    !started -> "/Sessions/Playing"
                    else -> "/Sessions/Playing/Progress"
                }
                started = true
                val result = attempt { http.call(http.url(path), body) }
                if (BuildConfig.DEBUG) android.util.Log.d("MenuStream", "jellyfin ${r.state} ${r.positionMs / 1000}s -> $path ${result.exceptionOrNull() ?: "ok"}")
                if (r.state == "stopped") break
            }
        }
        return PlaybackReporter { state, positionMs, _ -> queue.trySend(Report(state, positionMs)) }
    }

    private fun typesOf(library: Library) = if (library.kind == LibraryKind.MOVIES) "Movie" else "Series"

    private fun toItem(j: JfItem): Item {
        val type = when (j.type) {
            "Movie" -> ItemType.MOVIE
            "Series" -> ItemType.SHOW
            "Season" -> ItemType.SEASON
            "Episode" -> ItemType.EPISODE
            else -> ItemType.OTHER
        }
        // Every version is a media source with its own file; a plain item has one, matching Path.
        val files = j.mediaSources.map { MediaFile(it.id, it.path) }
            .ifEmpty { if (j.path != null && type != ItemType.SHOW && type != ItemType.SEASON) listOf(MediaFile(j.id, j.path)) else emptyList() }
        return Item(
            id = j.id,
            type = type,
            title = j.name,
            year = j.year,
            showId = if (type == ItemType.SHOW) null else j.seriesId,
            showTitle = j.seriesName,
            seasonId = j.seasonId,
            seasonNumber = if (type == ItemType.SEASON) j.index else j.parentIndex,
            episodeNumber = if (type == ItemType.EPISODE) j.index else null,
            image = j.imageTags["Primary"]?.let { "${j.id}|$it" },
            showImage = j.seriesPrimaryImageTag?.let { tag -> j.seriesId?.let { "$it|$tag" } },
            durationSeconds = j.runTimeTicks?.let { it / TICKS_PER_SECOND },
            resumeSeconds = (j.userData?.positionTicks ?: 0) / TICKS_PER_SECOND,
            watched = j.userData?.played == true,
            files = files,
        )
    }
}
