package org.menustream.tv.player

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

/** What to play: a direct-play URL plus the headers it needs (the Plex token). */
data class PlayRequest(val url: String, val headers: Map<String, String>, val startSeconds: Double, val title: String)

/**
 * Hardware/platform decoders first (and audio passthrough over HDMI when the TV or receiver supports it);
 * the bundled FFmpeg decoder only covers audio the device can't handle itself, such as DTS on many boxes.
 */
@OptIn(UnstableApi::class)
fun buildPlayer(context: Context, headers: Map<String, String>): ExoPlayer {
    val renderers = DefaultRenderersFactory(context).setExtensionRendererMode(DefaultRenderersFactory.EXTENSION_RENDERER_MODE_ON)
    val http = DefaultHttpDataSource.Factory().setDefaultRequestProperties(headers).setAllowCrossProtocolRedirects(false)
    return ExoPlayer.Builder(context, renderers).setMediaSourceFactory(DefaultMediaSourceFactory(http)).build()
}

class PlayerHandle(val player: ExoPlayer) {
    /** Current position in seconds, for "stopped" and resume. */
    fun positionSeconds(): Double = player.currentPosition / 1000.0
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
    val player = remember(request) {
        buildPlayer(context, request.headers).apply {
            setMediaItem(MediaItem.Builder().setUri(request.url).build(), (request.startSeconds * 1000).toLong())
            playWhenReady = true
            prepare()
        }
    }
    DisposableEffect(player) {
        val listener = object : Player.Listener {
            override fun onPlaybackStateChanged(state: Int) {
                if (state == Player.STATE_ENDED) onEnded()
            }
            override fun onPlayerError(error: PlaybackException) {
                onError(
                    when (error.errorCode) {
                        PlaybackException.ERROR_CODE_DECODER_INIT_FAILED,
                        PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED,
                        PlaybackException.ERROR_CODE_DECODING_FORMAT_EXCEEDS_CAPABILITIES,
                        -> "This file needs transcoding, which isn't supported yet."
                        PlaybackException.ERROR_CODE_IO_BAD_HTTP_STATUS,
                        PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED,
                        -> "Couldn't reach the media server."
                        else -> "Playback failed: ${error.errorCodeName}"
                    },
                )
            }
        }
        player.addListener(listener)
        onReady(PlayerHandle(player))
        onDispose {
            player.removeListener(listener)
            player.release()
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
                requestFocus()
            }
        },
    )
}
