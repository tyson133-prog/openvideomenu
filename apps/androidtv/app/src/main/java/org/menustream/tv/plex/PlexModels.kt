package org.menustream.tv.plex

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class PlexPin(val id: Long, val code: String, val authToken: String? = null, val expiresIn: Int? = null)

@Serializable
data class PlexResource(
    val name: String,
    val clientIdentifier: String,
    val provides: String = "",
    val owned: Boolean = false,
    val accessToken: String? = null,
    val connections: List<PlexConnection> = emptyList(),
) {
    val isServer get() = provides.split(',').contains("server")
}

@Serializable
data class PlexConnection(
    val uri: String,
    val protocol: String = "",
    val address: String = "",
    val port: Int = 0,
    val local: Boolean = false,
    val relay: Boolean = false,
    @SerialName("IPv6") val ipv6: Boolean = false,
)

@Serializable
data class MediaContainerResponse(@SerialName("MediaContainer") val container: MediaContainer)

@Serializable
data class MediaContainer(
    val size: Int = 0,
    val totalSize: Int? = null,
    val machineIdentifier: String? = null,
    @SerialName("Directory") val directories: List<PlexDirectory> = emptyList(),
    @SerialName("Metadata") val metadata: List<PlexItem> = emptyList(),
    @SerialName("Hub") val hubs: List<PlexHub> = emptyList(),
)

@Serializable
data class PlexHub(val type: String = "", val title: String = "", @SerialName("Metadata") val metadata: List<PlexItem> = emptyList())

/** An entry of /library/sections/{id}/firstCharacter: a letter (or "#") and how many titles start with it. */
@Serializable
data class PlexLetter(val key: String, val title: String, val size: Int = 0)

@Serializable
data class LettersResponse(@SerialName("MediaContainer") val container: LettersContainer)

@Serializable
data class LettersContainer(@SerialName("Directory") val letters: List<PlexLetter> = emptyList())

@Serializable
data class PlexDirectory(val key: String, val title: String, val type: String)

@Serializable
data class PlexItem(
    val ratingKey: String,
    val key: String = "",
    val type: String = "",
    val title: String = "",
    /** Episodes: show and season titles, season and episode numbers. */
    val grandparentTitle: String? = null,
    val grandparentRatingKey: String? = null,
    val grandparentThumb: String? = null,
    val parentTitle: String? = null,
    val parentIndex: Int? = null,
    val index: Int? = null,
    val leafCount: Int? = null,
    val childCount: Int? = null,
    val year: Int? = null,
    val thumb: String? = null,
    val art: String? = null,
    val duration: Long? = null,
    val viewOffset: Long? = null,
    val viewCount: Int? = null,
    /** "Trailer", "Featurette", ... for extras. */
    val subtype: String? = null,
    val extraType: Int? = null,
    @SerialName("Media") val media: List<PlexMedia> = emptyList(),
    @SerialName("Chapter") val chapters: List<PlexChapter> = emptyList(),
) {
    /** The first playable file (direct play uses a single part). */
    val part: PlexPart? get() = media.firstOrNull()?.parts?.firstOrNull()

    /**
     * Every file of every version. A title can have several versions (e.g. a 4K and an HD file, or a
     * stray copy Plex grouped with it); menus must match against all of them, not just the first.
     */
    val allParts: List<PlexPart> get() = media.flatMap { it.parts }
}

@Serializable
data class DecisionResponse(@SerialName("MediaContainer") val container: DecisionContainer)

/** Result of /video/:/transcode/universal/decision. 1000 = OK (e.g. "Direct play OK."). */
@Serializable
data class DecisionContainer(
    val mdeDecisionCode: Int? = null,
    val mdeDecisionText: String? = null,
    val generalDecisionCode: Int? = null,
    val generalDecisionText: String? = null,
    val directPlayDecisionCode: Int? = null,
    val directPlayDecisionText: String? = null,
) {
    val directPlayOk: Boolean get() = mdeDecisionCode == 1000 || directPlayDecisionCode == 1000
    val explanation: String get() = listOfNotNull(mdeDecisionText, directPlayDecisionText, generalDecisionText).joinToString("; ")
}

/** A specific file of a Plex item: what a menu button actually plays. */
data class PlayablePart(val item: PlexItem, val part: PlexPart)

/** Every file of [items], keyed by its path, for matching against manifest media paths. */
fun filesOf(items: List<PlexItem>): List<Pair<String, PlayablePart>> =
    items.flatMap { item -> item.allParts.mapNotNull { p -> p.file?.let { it to PlayablePart(item, p) } } }


@Serializable
data class PlexMedia(
    val videoCodec: String? = null,
    val audioCodec: String? = null,
    val container: String? = null,
    @SerialName("Part") val parts: List<PlexPart> = emptyList(),
)

@Serializable
data class PlexPart(val id: Long, val key: String, val file: String? = null, val duration: Long? = null, val container: String? = null)

@Serializable
data class PlexChapter(val index: Int? = null, val startTimeOffset: Long = 0, val tag: String? = null)
