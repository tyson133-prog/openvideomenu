package org.menustream.tv.menu

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
import org.menustream.tv.assets.AssetServerClient
import org.menustream.tv.net.json

private const val ORIGIN = "https://appassets.androidplatform.net"

/** Messages from the menu page (see packages/renderer-web/src/host.ts). */
sealed interface PageMessage {
    data object Ready : PageMessage
    /** [audio]: 1-based audio track; [subtitle]: 1-based, 0 = off; null = player default. */
    data class Play(val mediaId: String, val start: Double, val label: String, val audio: Int? = null, val subtitle: Int? = null) : PageMessage
    data object Exit : PageMessage
    data class Error(val message: String) : PageMessage
}

@Serializable
data class LastStop(val media: String, val position: Double)

/**
 * A web view running the shared menu renderer (app assets /assets/menu/host.html).
 *
 * - The page talks to native code only through a message port ("menustreamHost") restricted to the
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
        if (org.menustream.tv.BuildConfig.DEBUG) setWebContentsDebuggingEnabled(true)
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
        WebViewCompat.addWebMessageListener(this, "menustreamHost", setOf(ORIGIN)) { _, message: WebMessageCompat, _, _, replyProxy ->
            reply = replyProxy
            message.data?.let(::handle)
        }
    }

    fun open() = loadUrl("$ORIGIN/assets/menu/host.html")

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
                ),
            )
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
