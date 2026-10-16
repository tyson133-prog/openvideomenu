package com.ovmplayer.tv.plex

import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import com.ovmplayer.tv.BuildConfig
import com.ovmplayer.tv.attempt
import com.ovmplayer.tv.media.Item
import com.ovmplayer.tv.media.ItemType
import com.ovmplayer.tv.media.Letter
import com.ovmplayer.tv.media.Library
import com.ovmplayer.tv.media.LibraryKind
import com.ovmplayer.tv.media.MediaFile
import com.ovmplayer.tv.media.MediaSource
import com.ovmplayer.tv.media.Page
import com.ovmplayer.tv.player.Decoders
import com.ovmplayer.tv.player.PlayRequest
import com.ovmplayer.tv.player.PlaybackReporter

/** [MediaSource] for a Plex Media Server. */
class PlexSource(val server: PlexServer) : MediaSource {
    override val name get() = server.name
    override val kind = "Plex"
    override val assetServerAuth get() = mapOf("X-Plex-Token" to server.token)

    /** Progress reports outlive the screen that started them. */
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    override suspend fun libraries() = server.sections().mapNotNull { s ->
        when (s.type) {
            "movie" -> Library(s.key, s.title, LibraryKind.MOVIES)
            "show" -> Library(s.key, s.title, LibraryKind.SHOWS)
            else -> null
        }
    }

    override suspend fun continueWatching(library: Library?) = server.onDeck(library?.id).map(::toItem)
    override suspend fun recentlyAdded(library: Library?, size: Int) = server.recentlyAdded(library?.id, size).map(::toItem)
    override suspend fun recentEpisodes(library: Library, size: Int) = server.recentlyAdded(library.id, size, type = "4").map(::toItem)
    override suspend fun letters(library: Library) = server.letters(library.id).map { Letter(it.key, it.title) }
    override suspend fun byLetter(library: Library, letter: Letter) =
        server.byLetter(library.id, PlexLetter(letter.key, letter.title)).map(::toItem)
    override suspend fun search(query: String) = server.search(query).map(::toItem)
    override suspend fun children(item: Item) = server.children(item.id).map(::toItem)
    override suspend fun allEpisodes(show: Item) = server.allEpisodes(show.id).map(::toItem)
    override suspend fun showsTitled(library: Library, title: String) = server.showsTitled(library.id, title).map(::toItem)
    override suspend fun item(id: String) = server.item(id)?.let(::toItem)
    override suspend fun extras(item: Item) = server.extras(item.id).map(::toItem)
    override suspend fun moviesPage(library: Library, start: Int, size: Int) =
        server.moviesPage(library.id, start, size).let { c -> Page(c.metadata.map(::toItem), c.totalSize) }

    override fun imageUrl(image: String, width: Int, height: Int) = server.imageUrl(image, width, height)
    override fun imageHeaders() = server.authHeaders()

    /**
     * Plex's playback handshake: ask for a direct-play decision for this exact file, then stream the
     * part with the same session id, and report progress with it too. If Plex won't approve direct
     * play, fall back to fetching the raw file as a download (lower priority; can be forbidden for
     * shared users). A file this device can't decode is transcoded instead.
     */
    override suspend fun playRequest(item: Item, file: MediaFile, startSeconds: Double, audio: Int?, subtitle: Int?, transcode: Boolean): PlayRequest {
        val ref = PartRef.parse(file.ref)
        if (transcode || BuildConfig.FORCE_TRANSCODE || !Decoders.canPlay(file.videoCodec, file.container)) {
            return transcodeRequest(item, ref, startSeconds, audio, subtitle)
        }
        val session = UUID.randomUUID().toString()
        val decision = attempt { server.directPlayDecision(item.id, ref.mediaIndex, ref.partIndex, session) }
        val directPlay = decision.getOrNull()?.directPlayOk == true
        if (BuildConfig.DEBUG) {
            android.util.Log.d(
                "ovmplayer",
                "decision ${item.id} m${ref.mediaIndex} p${ref.partIndex}: " +
                    (decision.getOrNull()?.explanation ?: decision.exceptionOrNull()?.toString()) + if (directPlay) "" else " -> download fallback",
            )
        }
        return PlayRequest(
            url = server.partUrl(ref.key, download = !directPlay),
            headers = server.authHeaders() + ("X-Plex-Session-Identifier" to session),
            startSeconds = startSeconds,
            title = item.title,
            reporter = reporterFor(item, session),
            audioTrack = audio,
            subtitleTrack = subtitle,
            transcode = { at -> transcodeRequest(item, ref, at, audio, subtitle) },
        )
    }

    /** The file as Plex's transcode, with the menu's soundtrack and subtitles set on the part first. */
    private suspend fun transcodeRequest(item: Item, ref: PartRef, startSeconds: Double, audio: Int?, subtitle: Int?): PlayRequest {
        val session = UUID.randomUUID().toString()
        if (audio != null || subtitle != null) {
            // Lists don't carry a part's streams: its full metadata does.
            val part = attempt { server.item(item.id) }.getOrNull()?.media?.getOrNull(ref.mediaIndex)?.parts?.getOrNull(ref.partIndex)
            if (part != null) attempt { server.selectStreams(part, audio, subtitle) }
        }
        val url = server.transcodeUrl(item.id, ref.mediaIndex, ref.partIndex, session, startSeconds)
        val report = reporterFor(item, session)
        return PlayRequest(
            url = url,
            headers = server.authHeaders() + ("X-Plex-Session-Identifier" to session),
            startSeconds = startSeconds,
            title = item.title,
            reporter = PlaybackReporter { state, positionMs, durationMs ->
                report.report(state, positionMs, durationMs)
                if (state == "stopped") scope.launch { attempt { server.stopTranscode(session) } }
            },
            // The stream carries only the chosen soundtrack, with the chosen subtitles burned in.
            subtitleTrack = subtitle?.takeIf { it == 0 },
            transcoded = true,
        )
    }

    /**
     * Sends playback state to Plex (timeline), so resume points and watched status match other Plex apps.
     * Uses the playback's session id; failures are ignored (playback matters more than the report).
     */
    private fun reporterFor(item: Item, session: String) = PlaybackReporter { state, positionMs, durationMs ->
        val duration = durationMs.takeIf { it > 0 } ?: ((item.durationSeconds ?: 0.0) * 1000).toLong()
        scope.launch { attempt { server.timeline(item.id, state, positionMs, duration, session) } }
    }

    /** Which version (media) and part of a Plex item a [MediaFile] is, and the part's URL key. */
    private data class PartRef(val mediaIndex: Int, val partIndex: Int, val key: String) {
        override fun toString() = "$mediaIndex:$partIndex:$key"

        companion object {
            fun parse(ref: String): PartRef {
                val (m, p, key) = ref.split(':', limit = 3)
                return PartRef(m.toInt(), p.toInt(), key)
            }
        }
    }

    private fun toItem(p: PlexItem) = Item(
        id = p.ratingKey,
        type = when (p.type) {
            "movie" -> ItemType.MOVIE
            "show" -> ItemType.SHOW
            "season" -> ItemType.SEASON
            "episode" -> ItemType.EPISODE
            else -> ItemType.OTHER
        },
        title = p.title,
        year = p.year,
        showId = p.grandparentRatingKey,
        showTitle = p.grandparentTitle,
        seasonNumber = if (p.type == "season") p.index else p.parentIndex,
        episodeNumber = if (p.type == "episode") p.index else null,
        image = p.thumb,
        showImage = p.grandparentThumb,
        durationSeconds = p.duration?.let { it / 1000.0 },
        resumeSeconds = (p.viewOffset ?: 0) / 1000.0,
        watched = (p.viewCount ?: 0) > 0,
        lastPlayedMs = p.lastViewedAt?.let { it * 1000 },
        files = p.media.flatMapIndexed { mi, m ->
            m.parts.mapIndexed { pi, part -> MediaFile(PartRef(mi, pi, part.key).toString(), part.file, m.videoCodec, part.container ?: m.container) }
        },
    )
}
