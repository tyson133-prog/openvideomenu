package org.menustream.tv.assets

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import java.io.IOException
import java.util.concurrent.Executors
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import org.menustream.tv.net.Http
import org.menustream.tv.net.json

@Serializable
data class CatalogIndex(val api: String, val format: String, val manifests: List<CatalogEntry>)

@Serializable
data class CatalogEntry(
    val id: String,
    val root: Int,
    val path: String,
    val url: String,
    val valid: Boolean,
    val errors: List<String> = emptyList(),
    val title: CatalogTitle? = null,
    val hasMenus: Boolean = false,
    val media: List<CatalogMedia> = emptyList(),
)

@Serializable
data class CatalogTitle(val name: String, val year: Int? = null)

@Serializable
data class CatalogMedia(val id: String, val kind: String? = null, val label: String? = null, val ref: JsonElement, val path: String? = null)

/** Talks to the MenuStream asset server. The Plex token proves this viewer may use the server. */
class AssetServerClient(val baseUrl: String, private val token: String, private val http: OkHttpClient = Http.client) {

    suspend fun index(refresh: Boolean = false): CatalogIndex = withContext(Dispatchers.IO) {
        get("/v1/index" + if (refresh) "?refresh" else "").use { res ->
            if (res.code == 401) throw IOException("The asset server didn't accept this Plex sign-in.")
            if (!res.isSuccessful) throw IOException("Asset server answered HTTP ${res.code}")
            json.decodeFromString<CatalogIndex>(res.body.string())
        }
    }

    /** Raw GET used by the menu web view's proxy (keeps the token out of the page). */
    fun get(path: String, range: String? = null): Response {
        val req = Request.Builder().url(baseUrl.trimEnd('/') + path).header("X-Plex-Token", token)
        if (range != null) req.header("Range", range)
        return http.newCall(req.build()).execute()
    }
}

data class DiscoveredServer(val name: String, val url: String)

/** Find asset servers announced as _menustream._tcp on the home network. */
fun discoverAssetServers(context: Context): Flow<List<DiscoveredServer>> = callbackFlow {
    val nsd = context.getSystemService(Context.NSD_SERVICE) as NsdManager
    val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
    val lock = wifi.createMulticastLock("menustream-discovery").apply { setReferenceCounted(false); acquire() }
    val found = linkedMapOf<String, DiscoveredServer>()
    val executor = Executors.newSingleThreadExecutor()

    // A server may advertise several addresses (machines with VMware/WSL/Docker/VPN adapters);
    // use the first one whose health check answers from here. Runs on the discovery executor.
    fun publish(info: NsdServiceInfo) {
        val hosts = (if (Build.VERSION.SDK_INT >= 34) info.hostAddresses else listOfNotNull(@Suppress("DEPRECATION") info.host))
            .mapNotNull { it.hostAddress }
            .sortedBy { it.contains(':') } // IPv4 first
        val url = hosts.map { h -> if (h.contains(':')) "http://[${h.substringBefore('%')}]:${info.port}" else "http://$h:${info.port}" }
            .firstOrNull { url -> runCatching { Http.probe.newCall(Request.Builder().url("$url/v1/health").build()).execute().use { it.isSuccessful } }.getOrDefault(false) }
            ?: return
        synchronized(found) {
            found[info.serviceName] = DiscoveredServer(info.serviceName, url)
            trySend(found.values.toList())
        }
    }

    val listener = object : NsdManager.DiscoveryListener {
        override fun onServiceFound(info: NsdServiceInfo) {
            if (Build.VERSION.SDK_INT >= 34) {
                nsd.registerServiceInfoCallback(info, executor, object : NsdManager.ServiceInfoCallback {
                    override fun onServiceUpdated(updated: NsdServiceInfo) = executor.execute { publish(updated) }
                    override fun onServiceInfoCallbackRegistrationFailed(errorCode: Int) {}
                    override fun onServiceLost() {}
                    override fun onServiceInfoCallbackUnregistered() {}
                })
            } else {
                @Suppress("DEPRECATION")
                nsd.resolveService(info, object : NsdManager.ResolveListener {
                    override fun onServiceResolved(resolved: NsdServiceInfo) = executor.execute { publish(resolved) }
                    override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {}
                })
            }
        }
        override fun onServiceLost(info: NsdServiceInfo) {
            synchronized(found) {
                found.remove(info.serviceName)
                trySend(found.values.toList())
            }
        }
        override fun onDiscoveryStarted(serviceType: String) {}
        override fun onDiscoveryStopped(serviceType: String) {}
        override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {}
        override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
    }
    nsd.discoverServices("_menustream._tcp", NsdManager.PROTOCOL_DNS_SD, listener)
    trySend(emptyList())
    awaitClose {
        runCatching { nsd.stopServiceDiscovery(listener) }
        lock.release()
        executor.shutdown()
    }
}
