package com.ovmplayer.tv.player

import android.media.MediaCodecList

/**
 * Which files this device can play as they are, by the codec and container names Plex and Jellyfin
 * use. Only video needs checking: the bundled FFmpeg decoder plays any audio the device can't.
 * Anything else is transcoded by the server.
 */
object Decoders {
    private val videoMimes = mapOf(
        "h264" to "video/avc",
        "hevc" to "video/hevc",
        "vp8" to "video/x-vnd.on2.vp8",
        "vp9" to "video/x-vnd.on2.vp9",
        "av1" to "video/av01",
        "mpeg2video" to "video/mpeg2",
        "mpeg4" to "video/mp4v-es",
        "h263" to "video/3gpp",
    )
    private val aliases = mapOf("avc" to "h264", "h265" to "hevc", "mpeg2" to "mpeg2video")

    /** Containers Media3's extractors read. */
    val containers = setOf("mp4", "m4v", "mov", "mkv", "matroska", "webm", "ts", "mpegts", "m2ts", "mpeg", "mpg", "avi", "flv", "3gp")

    /** Video codecs this device has a decoder for. */
    val videoCodecs: Set<String> by lazy {
        val types = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
            .filter { !it.isEncoder }
            .flatMap { it.supportedTypes.map(String::lowercase) }
            .toSet()
        videoMimes.filterValues { it in types }.keys
    }

    /** False when the file's video codec or container is one this device can't play. Unknown means try it. */
    fun canPlay(videoCodec: String?, container: String?): Boolean {
        val codec = videoCodec?.lowercase()?.let { aliases[it] ?: it }
        if (codec != null && codec !in videoCodecs) return false
        // A container may come as a list ("mov,mp4,m4a"): any one Media3 reads will do.
        if (container != null && container.lowercase().split(',').none { it.trim() in containers }) return false
        return true
    }
}
