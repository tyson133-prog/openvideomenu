package com.ovmplayer.tv.player

import android.content.Context
import androidx.annotation.OptIn
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.viewinterop.AndroidView
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.ui.PlayerView
import kotlinx.coroutines.launch

/** Receives playback state for the media server: "playing", "paused" or "stopped", with times in ms. */
fun interface PlaybackReporter {
    fun report(state: String, positionMs: Long, durationMs: Long)
}

/** What to play: a direct-play URL plus the headers it needs (the Plex token). */
data class PlayRequest(
    val url: String,
    val headers: Map<String, String>,
    val startSeconds: Double,
    val title: String,
    val reporter: PlaybackReporter? = null,
    /** 1-based audio track to select (menus' soundtrack/commentary choices); null = default. */
    val audioTrack: Int? = null,
    /** 1-based subtitle track to show, 0 = subtitles off; null = default. */
    val subtitleTrack: Int? = null,
    /** 1-based video track (angles kept as video tracks of one file); null = default. */
    val videoTrack: Int? = null,
    /**
     * Level 2 (spec 4.2): a title made of segments, played in order as one title. [url] is then the first
     * clip's, and [startSeconds] and every reported position are on the title's own timeline.
     */
    val segments: List<Clip> = emptyList(),
    /** A stream the server transcodes (HLS); its position is still the file's time. */
    val transcoded: Boolean = false,
    /**
     * The same title as a transcode from a position, for when this device turns out not to decode the
     * file after all (a profile or size it can't handle). Null when there's no such fallback.
     */
    val transcode: (suspend (startSeconds: Double) -> PlayRequest?)? = null,
)

/** One segment of a title: a range of a file (seconds into it), with its angle's video track if any. */
data class Clip(
    val url: String,
    val startSeconds: Double,
    val endSeconds: Double?,
    val videoTrack: Int? = null,
    /** This clip's own request headers (a Plex stream must carry the session its play decision was made under). */
    val headers: Map<String, String> = emptyMap(),
)

/**
 * Hardware/platform decoders first (and audio passthrough over HDMI when the TV or receiver supports it);
 * the bundled FFmpeg decoder only covers audio the device can't handle itself, such as DTS on many boxes.
 */
@OptIn(UnstableApi::class)
fun buildPlayer(context: Context, headers: Map<String, String>, clipHeaders: Map<String, Map<String, String>> = emptyMap()): ExoPlayer {
    val renderers = DefaultRenderersFactory(context).setExtensionRendererMode(DefaultRenderersFactory.EXTENSION_RENDERER_MODE_ON)
    val http = DefaultHttpDataSource.Factory().setDefaultRequestProperties(headers).setAllowCrossProtocolRedirects(false)
    // Segments (Level 2): each file is requested with its own clip's headers.
    val source = if (clipHeaders.isEmpty()) http else androidx.media3.datasource.ResolvingDataSource.Factory(http) { spec ->
        clipHeaders[spec.uri.toString()]?.let { spec.withAdditionalHeaders(it) } ?: spec
    }
    return ExoPlayer.Builder(context, renderers).setMediaSourceFactory(DefaultMediaSourceFactory(source)).build()
}

/**
 * Track choices from a menu, as Media3 selection parameters: audio N = the Nth audio track of the file,
 * subtitle N = the Nth subtitle track the player can show, 0 = subtitles off.
 */
@OptIn(UnstableApi::class)
fun selectTracks(
    params: androidx.media3.common.TrackSelectionParameters,
    tracks: androidx.media3.common.Tracks,
    audio: Int?,
    subtitle: Int?,
    video: Int? = null,
): androidx.media3.common.TrackSelectionParameters {
    val b = params.buildUpon()
    val videoGroups = tracks.groups.filter { it.type == androidx.media3.common.C.TRACK_TYPE_VIDEO }
    if (video != null && video in 1..videoGroups.size && videoGroups[video - 1].isSupported) {
        b.setOverrideForType(androidx.media3.common.TrackSelectionOverride(videoGroups[video - 1].mediaTrackGroup, 0))
    }
    val audioGroups = tracks.groups.filter { it.type == androidx.media3.common.C.TRACK_TYPE_AUDIO }
    if (audio != null && audio in 1..audioGroups.size) {
        b.setOverrideForType(androidx.media3.common.TrackSelectionOverride(audioGroups[audio - 1].mediaTrackGroup, 0))
    }
    val textGroups = tracks.groups.filter { it.type == androidx.media3.common.C.TRACK_TYPE_TEXT && it.isSupported }
    when {
        subtitle == 0 -> b.setTrackTypeDisabled(androidx.media3.common.C.TRACK_TYPE_TEXT, true)
        subtitle != null && subtitle in 1..textGroups.size ->
            b.setTrackTypeDisabled(androidx.media3.common.C.TRACK_TYPE_TEXT, false)
                .setOverrideForType(androidx.media3.common.TrackSelectionOverride(textGroups[subtitle - 1].mediaTrackGroup, 0))
    }
    return b.build()
}

/** [offsets]: where each clip starts on the title's timeline (a single file is one clip at 0). */
class PlayerHandle(val player: ExoPlayer, private val offsets: List<Double> = listOf(0.0)) {
    /** Current position in seconds on the title's timeline, for "stopped", resume and cues. */
    fun positionSeconds(): Double = (offsets.getOrNull(player.currentMediaItemIndex) ?: 0.0) + player.currentPosition / 1000.0

    /** Level 2: move to [seconds] on the title's timeline, in whichever clip holds it (a seek action). */
    fun seekTo(seconds: Double) {
        val clip = offsets.indexOfLast { it <= seconds }.coerceAtLeast(0)
        player.seekTo(clip, ((seconds - offsets[clip]) * 1000).toLong())
    }
}

@OptIn(UnstableApi::class)
@Composable
fun VideoPlayer(
    request: PlayRequest,
    onReady: (PlayerHandle) -> Unit,
    onEnded: () -> Unit,
    onError: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    // What plays: the request, or the transcode that replaced it when this device couldn't decode the file.
    var active by remember(request) { androidx.compose.runtime.mutableStateOf(request) }
    // Segments play as clipped items in one playlist (gapless where the formats allow); each clip's
    // place on the title's timeline is the sum of the lengths before it.
    val offsets = remember(active) {
        active.segments.runningFold(0.0) { at, clip -> at + ((clip.endSeconds ?: Double.MAX_VALUE) - clip.startSeconds) }.dropLast(1)
    }
    val player = remember(active) {
        buildPlayer(context, active.headers, active.segments.associate { it.url to it.headers }).apply {
            if (active.segments.isEmpty()) {
                val item = MediaItem.Builder().setUri(active.url)
                if (active.transcoded) item.setMimeType(androidx.media3.common.MimeTypes.APPLICATION_M3U8)
                setMediaItem(item.build(), (active.startSeconds * 1000).toLong())
            } else {
                val items = active.segments.map { clip ->
                    val clipping = MediaItem.ClippingConfiguration.Builder().setStartPositionMs((clip.startSeconds * 1000).toLong())
                    clip.endSeconds?.let { clipping.setEndPositionMs((it * 1000).toLong()) }
                    MediaItem.Builder().setUri(clip.url).setClippingConfiguration(clipping.build()).build()
                }
                val index = offsets.indexOfLast { it <= active.startSeconds }.coerceAtLeast(0)
                setMediaItems(items, index, ((active.startSeconds - offsets[index]) * 1000).toLong())
            }
            playWhenReady = true
            prepare()
        }
    }
    val lifecycle = androidx.lifecycle.compose.LocalLifecycleOwner.current.lifecycle
    DisposableEffect(player, lifecycle) {
        // Pause when the app goes to the background (Home); the viewer resumes with play.
        val lifecycleObserver = androidx.lifecycle.LifecycleEventObserver { _, event ->
            if (event == androidx.lifecycle.Lifecycle.Event.ON_STOP) player.pause()
        }
        lifecycle.addObserver(lifecycleObserver)
        // Only report to the server once playback has really started: a file that fails to open must
        // not overwrite the title's resume point (e.g. with a chapter start that never played).
        var started = false
        val report = { state: String ->
            if (started) active.reporter?.report(state, player.currentPosition.coerceAtLeast(0), player.duration.takeIf { it > 0 } ?: 0)
        }
        val listener = object : Player.Listener {
            private var fallingBack = false

            /**
             * This device can't decode the file after all (a profile, bit depth or size its decoder refuses):
             * play it again as the server's transcode, from where it was. False when there's no such fallback.
             */
            fun transcodeInstead(why: String): Boolean {
                val transcode = active.transcode ?: return false
                if (active.transcoded || active.segments.isNotEmpty() || fallingBack) return false
                fallingBack = true
                val at = if (player.currentPosition > 0) player.currentPosition / 1000.0 else active.startSeconds
                android.util.Log.i("ovmplayer", "$why: asking the server for a transcode from ${at}s")
                player.stop()
                scope.launch {
                    val next = runCatching { transcode(at) }.getOrNull()
                    if (next != null) active = next else onError("This TV can't play this video's format, and the server couldn't convert it.")
                }
                return true
            }

            override fun onPlaybackStateChanged(state: Int) {
                if (state == Player.STATE_ENDED) {
                    report("stopped")
                    onEnded()
                }
            }
            private var tracksApplied = false

            /** Each segment is its own file: its tracks are chosen again. */
            override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
                tracksApplied = false
            }

            /** Apply the menu's track choices once the file's tracks are known. */
            override fun onTracksChanged(tracks: androidx.media3.common.Tracks) {
                if (tracksApplied || tracks.groups.isEmpty()) return
                val video = tracks.groups.filter { it.type == androidx.media3.common.C.TRACK_TYPE_VIDEO }
                if (video.isNotEmpty() && video.none { it.isSupported }) {
                    // Media3 would play the sound alone.
                    if (!transcodeInstead("no video track this device can decode")) {
                        player.stop()
                        onError("This TV can't play this video's format.")
                    }
                    return
                }
                tracksApplied = true
                val angle = active.segments.getOrNull(player.currentMediaItemIndex)?.videoTrack ?: active.videoTrack
                player.trackSelectionParameters = selectTracks(player.trackSelectionParameters, tracks, active.audioTrack, active.subtitleTrack, angle)
                if (com.ovmplayer.tv.BuildConfig.DEBUG) android.util.Log.d(
                    "ovmplayer",
                    "tracks: audio=${active.audioTrack} subtitle=${active.subtitleTrack} video=${active.videoTrack} of ${tracks.groups.count { it.type == androidx.media3.common.C.TRACK_TYPE_AUDIO }} audio / ${tracks.groups.count { it.type == androidx.media3.common.C.TRACK_TYPE_TEXT }} text groups",
                )
            }

            override fun onIsPlayingChanged(isPlaying: Boolean) {
                if (isPlaying) started = true
                if (player.playbackState != Player.STATE_ENDED) report(if (isPlaying) "playing" else "paused")
            }
            // Playback that breaks off partway (the server closed the connection during a long pause)
            // is opened again where it was, twice at most in a row, before the error is shown.
            private var reopens = 0
            private var reopenedAt = 0L

            override fun onPlayerError(error: PlaybackException) {
                val decoding = error.errorCode in PlaybackException.ERROR_CODE_DECODER_INIT_FAILED..PlaybackException.ERROR_CODE_DECODING_RESOURCES_RECLAIMED
                if (decoding && transcodeInstead("decoder error ${error.errorCodeName}")) return
                val network = error.errorCode in PlaybackException.ERROR_CODE_IO_UNSPECIFIED..PlaybackException.ERROR_CODE_IO_READ_POSITION_OUT_OF_RANGE
                val now = android.os.SystemClock.elapsedRealtime()
                if (now - reopenedAt > 60_000) reopens = 0
                if (started && network && reopens < 2) {
                    reopens++
                    reopenedAt = now
                    android.util.Log.i("ovmplayer", "playback broke off (${error.errorCodeName}) at ${player.currentPosition} ms: opening it again (try $reopens)")
                    // The player keeps its item and position after an error: preparing again reconnects there.
                    player.prepare()
                    player.play()
                    return
                }
                onError(
                    when (error.errorCode) {
                        PlaybackException.ERROR_CODE_DECODER_INIT_FAILED,
                        PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED,
                        PlaybackException.ERROR_CODE_DECODING_FORMAT_EXCEEDS_CAPABILITIES,
                        -> "This TV can't play this video's format."
                        PlaybackException.ERROR_CODE_IO_BAD_HTTP_STATUS -> when (
                            (error.cause as? androidx.media3.datasource.HttpDataSource.InvalidResponseCodeException)?.responseCode
                        ) {
                            503, 404 -> "Plex says this file isn't available right now. Rescan the library in Plex, then try again."
                            401 -> "Plex refused access to this file. Try signing in again."
                            403 -> "The server owner hasn't allowed downloads for your account, which this app needs for now."
                            else -> "The media server couldn't send this file (${error.errorCodeName})."
                        }
                        PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED,
                        PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT,
                        -> "Couldn't reach the media server."
                        else -> "Playback failed: ${error.errorCodeName}"
                    },
                )
            }
        }
        player.addListener(listener)
        onReady(PlayerHandle(player, if (active.segments.isEmpty()) listOf(0.0) else offsets))
        onDispose {
            if (player.playbackState != Player.STATE_ENDED) report("stopped")
            lifecycle.removeObserver(lifecycleObserver)
            player.removeListener(listener)
            player.release()
        }
    }
    // While playing, keep the server's progress current (Plex expects about every 10 s).
    androidx.compose.runtime.LaunchedEffect(player) {
        while (true) {
            kotlinx.coroutines.delay(10_000)
            if (player.isPlaying) active.reporter?.report("playing", player.currentPosition, player.duration.coerceAtLeast(0))
        }
    }
    AndroidView(
        modifier = modifier,
        factory = { ctx ->
            PlayerView(ctx).apply {
                this.player = player
                useController = true
                setShowSubtitleButton(true)
                keepScreenOn = true
                // PlayerView isn't focusable by default: requestFocus() then only works if a control
                // button happens to be showing, and remote keys go elsewhere. Make it focusable, and
                // ask once it is attached (a request before that can be dropped).
                isFocusable = true
                isFocusableInTouchMode = true
                // When the controls auto-hide, the focused button disappears and Android hands focus to
                // the app's root view, so the next key press would show nothing. Take focus back.
                setControllerVisibilityListener(
                    PlayerView.ControllerVisibilityListener { visibility ->
                        if (visibility != android.view.View.VISIBLE) post { requestFocus() }
                    },
                )
                addOnAttachStateChangeListener(object : android.view.View.OnAttachStateChangeListener {
                    override fun onViewAttachedToWindow(v: android.view.View) {
                        v.post { v.requestFocus() }
                    }
                    override fun onViewDetachedFromWindow(v: android.view.View) {}
                })
            }
        },
        // A transcode replacing the file is a new player in the same view.
        update = { view -> if (view.player !== player) view.player = player },
    )
}
