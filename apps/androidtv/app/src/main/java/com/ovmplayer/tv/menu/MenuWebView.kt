package com.ovmplayer.tv.menu

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Color
import android.net.Uri
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import androidx.webkit.JavaScriptReplyProxy
import androidx.webkit.WebMessageCompat
import androidx.webkit.WebViewAssetLoader
import androidx.webkit.WebViewClientCompat
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import com.ovmplayer.tv.assets.AssetServerClient
import com.ovmplayer.tv.net.json

private const val ORIGIN = "https://appassets.androidplatform.net"

/** Messages from the menu page (see packages/renderer-web/src/host.ts). */
sealed interface PageMessage {
    data object Ready : PageMessage
    /** [audio]: 1-based audio track; [subtitle]: 1-based, 0 = off; null = player default. */
    data class Play(
        val mediaId: String,
        val start: Double,
        val label: String,
        val audio: Int? = null,
        val subtitle: Int? = null,
        /** Level 2: which angle of media with angles; [videoTrack] when its angles are video tracks. */
        val angle: Int? = null,
        val videoTrack: Int? = null,
        /** Level 2: stop here (seconds) as if the title ended, and cues to report while it plays. */
        val end: Double? = null,
        val cues: List<com.ovmplayer.tv.player.CueWatch> = emptyList(),
        /** Level 2: the title is made of these pieces of other media (spec 4.2); [start], [end], cues are on its timeline. */
        val segments: List<PageSegment> = emptyList(),
    ) : PageMessage
    /** Level 2: stop playback without reporting it (a cue opened a menu). */
    data object Stop : PageMessage
    /** Level 2: an overlay's buttons show over the video: show the page over it and give it the keys. */
    data class Overlay(val showing: Boolean) : PageMessage
    data object Exit : PageMessage
    data class Error(val message: String) : PageMessage
}

/** A piece of a title made of segments: a range (seconds) of a manifest media id, with its angle. */
data class PageSegment(val mediaId: String, val start: Double, val end: Double?, val angle: Int? = null, val videoTrack: Int? = null)

@Serializable
data class LastStop(val media: String, val position: Double)

/**
 * A web view running the shared menu renderer (app assets /assets/menu/host.html).
 *
 * - The page talks to native code only through a message port ("openVideoMenuHost") restricted to the
 *   app-assets origin; there is no @JavascriptInterface.
 * - Requests to https://appassets.androidplatform.net/asset-server/... are proxied natively to the
 *   asset server with the Plex token added, so the token never enters the page and menu art is
 *   same-origin (no mixed content).
 * - The page can reach nothing else: all other network requests are blocked.
 */
@SuppressLint("SetJavaScriptEnabled")
class MenuWebView(
    context: Context,
    private val assets: AssetServerClient,
    private val onMessage: (PageMessage) -> Unit,
) : WebView(context) {
    private var reply: JavaScriptReplyProxy? = null

    private val loader = WebViewAssetLoader.Builder()
        .addPathHandler("/assets/", WebViewAssetLoader.AssetsPathHandler(context))
        .build()

    init {
        if (com.ovmplayer.tv.BuildConfig.DEBUG) setWebContentsDebuggingEnabled(true)
        setBackgroundColor(Color.BLACK)
        isFocusable = true
        isFocusableInTouchMode = true
        settings.apply {
            javaScriptEnabled = true
            mediaPlaybackRequiresUserGesture = false
            allowFileAccess = false
            allowContentAccess = false
            domStorageEnabled = false
            setSupportZoom(false)
        }
        webViewClient = object : WebViewClientCompat() {
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
                val url = request.url
                if (url.host == "appassets.androidplatform.net" && url.path?.startsWith("/asset-server/") == true) {
                    val range = request.requestHeaders.entries.firstOrNull { it.key.equals("Range", ignoreCase = true) }?.value
                    return proxy(url.path!!.removePrefix("/asset-server"), range)
                }
                return loader.shouldInterceptRequest(url)
                    ?: WebResourceResponse("text/plain", "utf-8", 403, "Forbidden", emptyMap(), null)
            }

            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest) = true

            /** The web engine crashed or was killed for memory: report it; the screen closes the menu. */
            override fun onRenderProcessGone(view: WebView, detail: android.webkit.RenderProcessGoneDetail): Boolean {
                onMessage(PageMessage.Error("The menu stopped unexpectedly."))
                return true
            }
        }
        webChromeClient = object : android.webkit.WebChromeClient() {
            /**
             * WebView draws this over a <video> until its first frame (the start of a motion menu or
             * transition). The default is a grey play icon; a transparent pixel lets the black page show.
             */
            override fun getDefaultVideoPoster(): android.graphics.Bitmap =
                android.graphics.Bitmap.createBitmap(1, 1, android.graphics.Bitmap.Config.ARGB_8888)
        }
        check(WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)) { "This device's WebView is too old." }
        WebViewCompat.addWebMessageListener(this, "openVideoMenuHost", setOf(ORIGIN)) { _, message: WebMessageCompat, _, _, replyProxy ->
            reply = replyProxy
            message.data?.let(::handle)
        }
    }

    fun open() = loadUrl("$ORIGIN/assets/menu/host.html")

    /**
     * While a title plays on top of the menu, take the page out of the focus order. Otherwise remote
     * keys can land on the hidden page: when the player's controls auto-hide, Android moves focus to
     * the next focusable view, which would be this one.
     */
    fun setBehindPlayer(behind: Boolean) {
        isFocusable = !behind
        isFocusableInTouchMode = !behind
        descendantFocusability = if (behind) android.view.ViewGroup.FOCUS_BLOCK_DESCENDANTS else android.view.ViewGroup.FOCUS_AFTER_DESCENDANTS
        if (behind) clearFocus() else requestFocus()
    }

    /** Tell the page which manifest to show. [manifestPath] is the asset server path, e.g. /v1/files/0/... */
    fun init(manifestPath: String, lastStop: LastStop?) = send(buildJsonObject {
        put("type", "init")
        put("manifestUrl", "$ORIGIN/asset-server$manifestPath")
        if (lastStop != null) put("lastStop", json.encodeToJsonElement(LastStop.serializer(), lastStop))
    })

    fun ended() = send(buildJsonObject { put("type", "ended") })
    fun stopped(position: Double) = send(buildJsonObject { put("type", "stopped"); put("position", position) })
    fun playError(message: String) = send(buildJsonObject { put("type", "playError"); put("message", message) })
    fun back() = send(buildJsonObject { put("type", "key"); put("key", "back") })
    /** [position]: for a menu key during playback (Level 2), where the title was (seconds). */
    fun key(name: String, position: Double? = null) = send(buildJsonObject {
        put("type", "key")
        put("key", name)
        if (position != null) put("position", position)
    })
    fun cue(id: String) = send(buildJsonObject { put("type", "cue"); put("id", id) })
    fun cueEnd(id: String) = send(buildJsonObject { put("type", "cueEnd"); put("id", id) })

    /** Level 2: over the video while an overlay's buttons show (see-through), hidden behind it otherwise. */
    fun setOverVideo(over: Boolean) {
        setBackgroundColor(if (over) Color.TRANSPARENT else Color.BLACK)
    }

    private fun send(msg: JsonObject) {
        reply?.postMessage(msg.toString())
    }

    private fun handle(text: String) {
        val msg = runCatching { json.parseToJsonElement(text).jsonObject }.getOrNull() ?: return
        fun str(key: String) = (msg[key] as? JsonElement)?.jsonPrimitive?.content
        when (str("type")) {
            "ready" -> onMessage(PageMessage.Ready)
            "play" -> onMessage(
                PageMessage.Play(
                    str("mediaId") ?: return,
                    str("start")?.toDoubleOrNull() ?: 0.0,
                    str("label") ?: "",
                    str("audio")?.toDoubleOrNull()?.toInt(),
                    str("subtitle")?.toDoubleOrNull()?.toInt(),
                    str("angle")?.toDoubleOrNull()?.toInt(),
                    str("videoTrack")?.toDoubleOrNull()?.toInt(),
                    str("end")?.toDoubleOrNull(),
                    (msg["cues"] as? kotlinx.serialization.json.JsonArray).orEmpty().mapNotNull { c ->
                        val o = c as? JsonObject ?: return@mapNotNull null
                        fun num(key: String) = (o[key] as? kotlinx.serialization.json.JsonPrimitive)?.content?.toDoubleOrNull()
                        val id = (o["id"] as? kotlinx.serialization.json.JsonPrimitive)?.content ?: return@mapNotNull null
                        com.ovmplayer.tv.player.CueWatch(id, num("at"), num("from"), num("to"))
                    },
                    (msg["segments"] as? kotlinx.serialization.json.JsonArray).orEmpty().mapNotNull { e ->
                        val o = e as? JsonObject ?: return@mapNotNull null
                        fun prim(key: String) = (o[key] as? kotlinx.serialization.json.JsonPrimitive)?.content
                        PageSegment(
                            prim("mediaId") ?: return@mapNotNull null,
                            prim("start")?.toDoubleOrNull() ?: 0.0,
                            prim("end")?.toDoubleOrNull(),
                            prim("angle")?.toDoubleOrNull()?.toInt(),
                            prim("videoTrack")?.toDoubleOrNull()?.toInt(),
                        )
                    },
                ),
            )
            "stop" -> onMessage(PageMessage.Stop)
            "overlay" -> onMessage(PageMessage.Overlay(str("showing") == "true"))
            "exit" -> onMessage(PageMessage.Exit)
            "error" -> onMessage(PageMessage.Error(str("message") ?: "Menu error"))
        }
    }

    /** Proxy /asset-server/<path> to the asset server, passing Range through for menu video. */
    private fun proxy(path: String, range: String?): WebResourceResponse {
        return try {
            val res = assets.get(Uri.encode(path, "/"), range)
            val type = res.header("Content-Type")?.substringBefore(';') ?: "application/octet-stream"
            val headers = buildMap {
                res.header("Content-Length")?.let { put("Content-Length", it) }
                res.header("Accept-Ranges")?.let { put("Accept-Ranges", it) }
                res.header("Content-Range")?.let { put("Content-Range", it) }
                put("Cache-Control", "no-cache")
            }
            WebResourceResponse(type, null, res.code, res.message.ifBlank { "OK" }, headers, res.body.byteStream())
        } catch (e: Exception) {
            WebResourceResponse("text/plain", "utf-8", 502, "Bad Gateway", emptyMap(), null)
        }
    }
}
