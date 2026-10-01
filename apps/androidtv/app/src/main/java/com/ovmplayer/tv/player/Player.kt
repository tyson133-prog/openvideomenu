package com.ovmplayer.tv.player

import android.content.Context
import androidx.annotation.OptIn
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
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
    // Segments play as clipped items in one playlist (gapless where the formats allow); each clip's
    // place on the title's timeline is the sum of the lengths before it.
    val offsets = remember(request) {
        request.segments.runningFold(0.0) { at, clip -> at + ((clip.endSeconds ?: Double.MAX_VALUE) - clip.startSeconds) }.dropLast(1)
    }
    val player = remember(request) {
        buildPlayer(context, request.headers, request.segments.associate { it.url to it.headers }).apply {
            if (request.segments.isEmpty()) {
                setMediaItem(MediaItem.Builder().setUri(request.url).build(), (request.startSeconds * 1000).toLong())
            } else {
                val items = request.segments.map { clip ->
                    val clipping = MediaItem.ClippingConfiguration.Builder().setStartPositionMs((clip.startSeconds * 1000).toLong())
                    clip.endSeconds?.let { clipping.setEndPositionMs((it * 1000).toLong()) }
                    MediaItem.Builder().setUri(clip.url).setClippingConfiguration(clipping.build()).build()
                }
                val index = offsets.indexOfLast { it <= request.startSeconds }.coerceAtLeast(0)
                setMediaItems(items, index, ((request.startSeconds - offsets[index]) * 1000).toLong())
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
            if (started) request.reporter?.report(state, player.currentPosition.coerceAtLeast(0), player.duration.takeIf { it > 0 } ?: 0)
        }
        val listener = object : Player.Listener {
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
                tracksApplied = true
                val video = request.segments.getOrNull(player.currentMediaItemIndex)?.videoTrack ?: request.videoTrack
                player.trackSelectionParameters = selectTracks(player.trackSelectionParameters, tracks, request.audioTrack, request.subtitleTrack, video)
                if (com.ovmplayer.tv.BuildConfig.DEBUG) android.util.Log.d(
                    "ovmplayer",
                    "tracks: audio=${request.audioTrack} subtitle=${request.subtitleTrack} video=${request.videoTrack} of ${tracks.groups.count { it.type == androidx.media3.common.C.TRACK_TYPE_AUDIO }} audio / ${tracks.groups.count { it.type == androidx.media3.common.C.TRACK_TYPE_TEXT }} text groups",
                )
            }

            override fun onIsPlayingChanged(isPlaying: Boolean) {
                if (isPlaying) started = true
                if (player.playbackState != Player.STATE_ENDED) report(if (isPlaying) "playing" else "paused")
            }
            override fun onPlayerError(error: PlaybackException) {
                onError(
                    when (error.errorCode) {
                        PlaybackException.ERROR_CODE_DECODER_INIT_FAILED,
                        PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED,
                        PlaybackException.ERROR_CODE_DECODING_FORMAT_EXCEEDS_CAPABILITIES,
                        -> "This file needs transcoding, which isn't supported yet."
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
        onReady(PlayerHandle(player, if (request.segments.isEmpty()) listOf(0.0) else offsets))
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
            if (player.isPlaying) request.reporter?.report("playing", player.currentPosition, player.duration.coerceAtLeast(0))
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
    )
}
