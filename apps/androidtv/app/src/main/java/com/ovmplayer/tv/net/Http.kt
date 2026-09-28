package com.ovmplayer.tv.net

import java.io.IOException
import java.net.InetAddress
import java.util.concurrent.TimeUnit
import kotlinx.serialization.json.Json
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.Response

val json = Json {
    ignoreUnknownKeys = true
    explicitNulls = false
    coerceInputValues = true
}

/**
 * True for home-network / VPN addresses: loopback, RFC 1918, link-local, IPv6 unique-local, and
 * 100.64.0.0/10 (Tailscale). Mirrors the asset server's rule.
 */
fun isLocalHost(host: String): Boolean {
    val h = host.trim('[', ']').lowercase()
    if (h == "localhost" || h == "::1") return true
    if (h.contains(':')) return h.matches(Regex("^f[cd][0-9a-f]{2}:.*")) || h.matches(Regex("^fe[89ab][0-9a-f]:.*"))
    val parts = h.split('.').mapNotNull { it.toIntOrNull() }
    if (parts.size != 4 || parts.any { it !in 0..255 }) return false
    val (a, b) = parts
    return a == 127 || a == 10 || (a == 172 && b in 16..31) || (a == 192 && b == 168) ||
        (a == 169 && b == 254) || (a == 100 && b in 64..127)
}

/**
 * Cleartext is enabled app-wide (Android can't scope it to IP ranges), so enforce the real rule here:
 * plain http:// only to home-network addresses. Everything else must be https.
 */
private class LocalCleartextOnly : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val url = chain.request().url
        if (!url.isHttps && !isLocalHost(url.host) && !isLocalName(url.host)) {
            throw IOException("Refusing plain http to non-local host ${url.host}")
        }
        return chain.proceed(chain.request())
    }

    /** mDNS names like "nas.local" resolve to LAN addresses; check what they resolve to. */
    private fun isLocalName(host: String): Boolean =
        runCatching { InetAddress.getAllByName(host).all { isLocalHost(it.hostAddress ?: "") } }.getOrDefault(false)
}

object Http {
    val client: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(8, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .addInterceptor(LocalCleartextOnly())
        .build()

    /** Short timeouts for probing which server address answers. */
    val probe: OkHttpClient = client.newBuilder()
        .connectTimeout(3, TimeUnit.SECONDS)
        .readTimeout(3, TimeUnit.SECONDS)
        .build()

    @Volatile private var assetClient: OkHttpClient? = null

    /**
     * For the asset server: a disk cache for the index and menu art. The index is revalidated with its
     * ETag on every launch, so an unchanged catalog costs one small 304.
     */
    fun assets(context: android.content.Context): OkHttpClient = assetClient ?: synchronized(this) {
        assetClient ?: client.newBuilder()
            .cache(okhttp3.Cache(java.io.File(context.cacheDir, "asset-server"), 64L * 1024 * 1024))
            .build()
            .also { assetClient = it }
    }
}
