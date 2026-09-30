package com.ovmplayer.tv.player

/** A cue the page asked us to watch for while a title plays (spec 6.3): a point cue's [at], or an overlay's window. */
data class CueWatch(val id: String, val at: Double? = null, val from: Double? = null, val to: Double? = null)

/**
 * Turns playback positions into cue reports, like CueClock in packages/renderer-web/src/adapters.ts.
 * Point cues fire when playback passes their time in ordinary play (not at the start position, not when
 * seeking past); overlays are reported while the position is inside their window, however it got
 * there; [end] reports the item as ended.
 */
class CueClock(
    private val cues: List<CueWatch>,
    private val end: Double?,
    private val onCue: (String) -> Unit,
    private val onCueEnd: (String) -> Unit,
    private val onEnd: () -> Unit,
) {
    private var last: Double? = null
    /** Where playback was asked to start: readings before it (a player that starts at the keyframe before) are ignored. */
    private var floor: Double? = null
    private val inside = mutableSetOf<String>()
    private var ended = false

    /** Where playback starts: a cue exactly there doesn't fire, nor any before it the player shows first. */
    fun start(position: Double) {
        floor = position
        last = position
    }

    /** Feed the current position (seconds). */
    fun tick(position: Double) {
        if (ended) return
        floor?.let {
            if (position < it) return
            floor = null
        }
        val previous = last
        last = position
        val played = previous != null && position >= previous && position - previous <= SEEK_SECONDS
        for (c in cues) {
            if (c.at != null) {
                if (played && previous!! < c.at && c.at <= position) onCue(c.id)
            } else if (c.from != null && c.to != null) {
                val inWindow = c.from <= position && position < c.to
                if (inWindow && inside.add(c.id)) onCue(c.id)
                else if (!inWindow && inside.remove(c.id)) onCueEnd(c.id)
            }
        }
        if (end != null && position >= end) {
            ended = true
            onEnd()
        }
    }

    companion object {
        /** A jump larger than this between two readings is a seek, not play. */
        const val SEEK_SECONDS = 2.0
    }
}
