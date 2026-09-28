package com.ovmplayer.tv.media

import com.ovmplayer.tv.player.PlayRequest

/**
 * The app's view of a media server (Plex, Jellyfin, ...). Screens, menus and matching only use these
 * types, so supporting another server means writing one more [MediaSource], nothing else.
 */
interface MediaSource {
    /** Server name shown to the viewer. */
    val name: String

    /** Short name of the kind of server, for messages ("Plex", "Jellyfin"). */
    val kind: String

    /** Headers proving to a Open Video Menu asset server that this viewer may use it. */
    val assetServerAuth: Map<String, String>

    /**
     * Base URL of the Open Video Menu asset API if this media server serves menus itself (e.g. Jellyfin
     * with the Open Video Menu plugin), else null. Apps ask this first, so no separate asset server is
     * needed once a server supports the format.
     */
    suspend fun nativeMenuServer(): String? = null

    suspend fun libraries(): List<Library>

    /** Titles in progress, in [library] or across all libraries. */
    suspend fun continueWatching(library: Library? = null): List<Item>

    /** Newest titles (movies and shows), in [library] or across all libraries. */
    suspend fun recentlyAdded(library: Library? = null, size: Int = 30): List<Item>

    /** Newest episodes of a TV library, with their files. */
    suspend fun recentEpisodes(library: Library, size: Int): List<Item>

    /** Letters to browse a library by, and the titles under one. */
    suspend fun letters(library: Library): List<Letter>
    suspend fun byLetter(library: Library, letter: Letter): List<Item>

    suspend fun search(query: String): List<Item>

    /** A show's seasons, or a season's episodes. */
    suspend fun children(item: Item): List<Item>

    /** Every episode of a show, with its files. */
    suspend fun allEpisodes(show: Item): List<Item>

    /** Shows in [library] with exactly this title. */
    suspend fun showsTitled(library: Library, title: String): List<Item>

    /** One item in full: files, resume position. */
    suspend fun item(id: String): Item?

    /** The title's local extras (trailers, featurettes, ...), with their files. */
    suspend fun extras(item: Item): List<Item>

    /** One page of a movie library, with files. Used to match hundreds of menus in a few requests. */
    suspend fun moviesPage(library: Library, start: Int, size: Int): Page

    /** URL of an item image (an [Item.image] value), and headers needed to fetch it. */
    fun imageUrl(image: String, width: Int, height: Int): String
    fun imageHeaders(): Map<String, String>

    /**
     * Everything the player needs to play [file] of [item]: stream URL, headers and progress reporting
     * back to the server. [audio] is a 1-based audio track, [subtitle] 1-based with 0 = off.
     */
    suspend fun playRequest(item: Item, file: MediaFile, startSeconds: Double, audio: Int? = null, subtitle: Int? = null): PlayRequest
}

enum class LibraryKind { MOVIES, SHOWS }

data class Library(val id: String, val title: String, val kind: LibraryKind)

data class Letter(val key: String, val title: String)

enum class ItemType { MOVIE, SHOW, SEASON, EPISODE, OTHER }

/**
 * One file of an item (a version, or a part of one). [path] is the file path as the server sees it,
 * matched against manifest media paths; [ref] is the source's own handle for playing it.
 */
data class MediaFile(val ref: String, val path: String?)

data class Item(
    val id: String,
    val type: ItemType,
    val title: String,
    val year: Int? = null,
    /** Episodes and seasons: their show; episodes: season and episode numbers. */
    val showId: String? = null,
    val showTitle: String? = null,
    val seasonId: String? = null,
    val seasonNumber: Int? = null,
    val episodeNumber: Int? = null,
    /** Opaque image handles for [MediaSource.imageUrl]. */
    val image: String? = null,
    val showImage: String? = null,
    val durationSeconds: Double? = null,
    val resumeSeconds: Double = 0.0,
    val watched: Boolean = false,
    /** When the viewer last played it (epoch ms), for "most recent" orderings. */
    val lastPlayedMs: Long? = null,
    /** Every file of every version: menus must match against all of them, not just the first. */
    val files: List<MediaFile> = emptyList(),
)

data class Page(val items: List<Item>, val total: Int?)

/** A specific file of an item: what a menu button actually plays. */
data class Playable(val item: Item, val file: MediaFile)

/** Every file of [items], keyed by its path, for matching against manifest media paths. */
fun filesOf(items: List<Item>): List<Pair<String, Playable>> =
    items.flatMap { item -> item.files.mapNotNull { f -> f.path?.let { it to Playable(item, f) } } }
