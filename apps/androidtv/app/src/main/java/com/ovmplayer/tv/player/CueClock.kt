package com.ovmplayer.tv.player

/** A cue the page asked us to watch for while a title plays (spec 6.3): a point cue's [at], or an overlay's window. */
data class CueWatch(val id: String, val at: Double? = null, val from: Double? = null, val to: Double? = null)

/** Level 2: a frame held at [at] for [seconds], or until the viewer goes on when [seconds] is null (spec 6.3). */
data class HoldWatch(val at: Double, val seconds: Double?)

/**
 * Turns playback positions into cue reports, like CueClock in packages/renderer-web/src/adapters.ts.
 * Point cues fire when playback passes their time in ordinary play (not at the start position, not when
 * seeking past); overlays are reported while the position is inside their window, however it got
 * there; [end] reports the item as ended.
 *
 * Holds: when ordinary play reaches a hold's time, [onHold] is called (the player pauses on that frame)
 * and positions are ignored until [release]. Cues before it have fired by then, and an overlay whose
 * window ends there is still up; cues at its time fire on release. Seeking past a hold skips it.
 */
class CueClock(
    private val cues: List<CueWatch>,
    private val end: Double?,
    private val onCue: (String) -> Unit,
    private val onCueEnd: (String) -> Unit,
    private val onEnd: () -> Unit,
    private val holds: List<HoldWatch> = emptyList(),
    private val onHold: (HoldWatch) -> Unit = {},
) {
    private var last: Double? = null
    /** Where playback was asked to start: readings before it (a player that starts at the keyframe before) are ignored. */
    private var floor: Double? = null
    private val inside = mutableSetOf<String>()
    private var ended = false
    /** The hold in progress, if any. */
    var holding: HoldWatch? = null
        private set
    /** The title ran out during or into a hold: it ends when the hold is released. */
    private var endAfterHold = false

    /** Where playback starts: a cue exactly there doesn't fire, nor any before it the player shows first. */
    fun start(position: Double) {
        floor = position
        last = position
        holding = null
        endAfterHold = false
    }

    /** Feed the current position (seconds). */
    fun tick(position: Double) {
        if (ended || holding != null) return
        floor?.let {
            if (position < it) return
            floor = null
        }
        val previous = last
        val played = previous != null && position >= previous && position - previous <= SEEK_SECONDS
        // Ordinary play reached a hold: play up to just before it, then hold there.
        val hold = if (played) holds.firstOrNull { previous!! < it.at && it.at <= position } else null
        if (hold != null) {
            advance(previous, hold.at - BEFORE, true)
            holding = hold
            onHold(hold)
            return
        }
        advance(previous, position, played)
    }

    /**
     * The title ran out: a hold not reached yet happens first, as a disc shows its last still. Returns
     * true when the title ends now, false when it ends on [release].
     */
    fun finish(): Boolean {
        if (holding != null) {
            endAfterHold = true
            return false
        }
        val from = last ?: 0.0
        val hold = holds.firstOrNull { it.at > from } ?: return true
        advance(from, hold.at - BEFORE, true)
        holding = hold
        endAfterHold = true
        onHold(hold)
        return false
    }

    /** The hold is over: cues at its time fire. Returns true when the title ran out into it (it has ended). */
    fun release(): Boolean {
        val hold = holding ?: return false
        holding = null
        advance(hold.at - BEFORE, hold.at, true)
        if (endAfterHold) {
            endAfterHold = false
            if (!ended) {
                ended = true
                onEnd()
            }
            return true
        }
        return false
    }

    /** The playback moved elsewhere during a hold (a seek): drop it without its cues. */
    fun cancelHold() {
        holding = null
        endAfterHold = false
    }

    private fun advance(previous: Double?, position: Double, played: Boolean) {
        last = position
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
        /** Just before a hold's time: where the clock stands while it holds. */
        private const val BEFORE = 1e-6
    }
}
