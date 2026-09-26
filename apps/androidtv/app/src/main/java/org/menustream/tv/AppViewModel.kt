package org.menustream.tv

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import org.menustream.tv.assets.AssetServerClient
import org.menustream.tv.assets.CatalogEntry
import org.menustream.tv.assets.discoverAssetServers
import org.menustream.tv.menu.LastStop
import org.menustream.tv.menu.MenuMatcher
import org.menustream.tv.player.PlayRequest
import org.menustream.tv.plex.PlexAccount
import org.menustream.tv.plex.PlexIdentity
import org.menustream.tv.plex.PlexItem
import org.menustream.tv.plex.PlexResource
import org.menustream.tv.plex.PlexServer
import org.menustream.tv.store.Settings

data class MovieRow(val item: PlexItem, val menu: CatalogEntry?)

/** A title opened into its disc menu: manifest media ids resolved to Plex items. */
data class OpenMenu(val movie: PlexItem, val entry: CatalogEntry, val media: Map<String, PlexItem>, val lastStop: LastStop?)

sealed interface Screen {
    data class Busy(val message: String) : Screen
    data class SignIn(val code: String?, val error: String? = null) : Screen
    data class PickServer(val servers: List<PlexResource>) : Screen
    data class Library(val movies: List<MovieRow>) : Screen
    data class Menu(val open: OpenMenu) : Screen
    data class Play(val request: PlayRequest) : Screen
    data class Failed(val message: String) : Screen
}

/** Where menus come from: shown in the library header. */
data class MenuSource(val url: String?, val status: String)

class AppViewModel(app: Application) : AndroidViewModel(app) {
    private val settings = Settings(app)
    private val identity = PlexIdentity(settings.clientId)
    private val account = PlexAccount(identity)

    private val _screen = MutableStateFlow<Screen>(Screen.Busy("Starting…"))
    val screen: StateFlow<Screen> = _screen.asStateFlow()
    private val _menuSource = MutableStateFlow(MenuSource(settings.assetServer, "Looking for a menu server…"))
    val menuSource: StateFlow<MenuSource> = _menuSource.asStateFlow()

    var server: PlexServer? = null
        private set
    var assets: AssetServerClient? = null
        private set
    private var catalog: List<CatalogEntry> = emptyList()
    private var rows: List<MovieRow> = emptyList()
    private var signInJob: Job? = null

    init {
        start()
    }

    fun start(): Job = viewModelScope.launch {
        val token = settings.plexToken ?: return@launch signIn()
        _screen.value = Screen.Busy("Connecting to Plex…")
        val servers = attempt { account.servers(token) }.getOrElse {
            _screen.value = Screen.Failed("Couldn't reach plex.tv: ${it.message}")
            return@launch
        }
        val chosen = servers.firstOrNull { it.clientIdentifier == settings.serverId } ?: servers.singleOrNull()
        if (chosen == null) _screen.value = if (servers.isEmpty()) Screen.Failed("No Plex servers on this account.") else Screen.PickServer(servers)
        else connect(chosen)
    }

    fun signIn() {
        signInJob?.cancel()
        signInJob = viewModelScope.launch {
            _screen.value = Screen.SignIn(null)
            val pin = attempt { account.createPin() }.getOrElse {
                _screen.value = Screen.SignIn(null, "Couldn't reach plex.tv: ${it.message}")
                return@launch
            }
            _screen.value = Screen.SignIn(pin.code)
            val token = withTimeoutOrNull(15 * 60_000L) {
                var t: String? = null
                while (t == null) {
                    delay(2_000)
                    t = attempt { account.checkPin(pin) }.getOrNull()
                }
                t
            }
            if (token == null) return@launch signIn() // code expired: show a new one
            settings.plexToken = token
            start()
        }
    }

    fun signOut() {
        settings.signOut()
        server = null
        signIn()
    }

    fun connect(resource: PlexResource): Job = viewModelScope.launch {
        _screen.value = Screen.Busy("Connecting to ${resource.name}…")
        server = attempt { PlexServer.connect(resource, identity) }.getOrElse {
            _screen.value = Screen.Failed(it.message ?: "Couldn't connect to ${resource.name}")
            return@launch
        }
        settings.serverId = resource.clientIdentifier
        loadLibrary()
    }

    fun loadLibrary(): Job = viewModelScope.launch {
        val plex = server ?: run { start(); return@launch }
        _screen.value = Screen.Busy("Loading your library…")
        val movies = attempt {
            plex.sections().filter { it.type == "movie" }.flatMap { plex.movies(it.key) }.sortedBy { it.title.lowercase() }
        }.getOrElse {
            _screen.value = Screen.Failed("Couldn't load the library: ${it.message}")
            return@launch
        }
        rows = movies.map { MovieRow(it, null) }
        _screen.value = Screen.Library(rows)
        refreshMenus(movies)
    }

    /** Find the asset server (saved, else discovered) and mark which movies have menus. */
    private suspend fun refreshMenus(movies: List<PlexItem>) {
        val plex = server ?: return
        val url = settings.assetServer ?: run {
            _menuSource.value = MenuSource(null, "Looking for a menu server…")
            withTimeoutOrNull(6_000) { discoverAssetServers(getApplication()).first { it.isNotEmpty() } }?.first()?.url
        }
        if (url == null) {
            _menuSource.value = MenuSource(null, "No menu server found. Titles play without menus.")
            return
        }
        val client = AssetServerClient(url, plex.token)
        catalog = attempt { client.index().manifests }.getOrElse {
            _menuSource.value = MenuSource(url, "Menu server at $url: ${it.message}")
            return
        }
        assets = client
        settings.assetServer = url
        rows = movies.map { m -> MovieRow(m, m.part?.file?.let { MenuMatcher.manifestFor(it, catalog) }) }
        _menuSource.value = MenuSource(url, "Menus from $url · ${rows.count { it.menu != null }} of ${catalog.size} matched")
        if (_screen.value is Screen.Library) _screen.value = Screen.Library(rows)
    }

    fun useAssetServer(url: String?) {
        settings.assetServer = url?.trim()?.trimEnd('/')?.let { if (it.startsWith("http")) it else "http://$it" }
        loadLibrary()
    }

    /** Open a title: into its disc menu when it has one, otherwise play it. */
    fun open(row: MovieRow): Job = viewModelScope.launch {
        val plex = server ?: return@launch
        val entry = row.menu
        if (entry == null) {
            play(row.item, (row.item.viewOffset ?: 0) / 1000.0)
            return@launch
        }
        _screen.value = Screen.Busy("Opening ${row.item.title}…")
        val full = plex.item(row.item.ratingKey) ?: row.item
        val extras = attempt { plex.extras(row.item.ratingKey) }.getOrDefault(emptyList())
        val files = (listOf(full) + extras).mapNotNull { item -> item.part?.file?.let { it to item } }
        val media = MenuMatcher.resolveMedia(entry, files)
        val featureId = media.entries.firstOrNull { it.value.ratingKey == full.ratingKey }?.key
        val lastStop = full.viewOffset?.takeIf { it > 0 && featureId != null }?.let { LastStop(featureId!!, it / 1000.0) }
        _screen.value = Screen.Menu(OpenMenu(full, entry, media, lastStop))
    }

    fun play(item: PlexItem, startSeconds: Double) {
        val plex = server ?: return
        val part = item.part ?: return
        _screen.value = Screen.Play(PlayRequest(plex.partUrl(part), plex.authHeaders(), startSeconds, item.title))
    }

    /** Resolve a menu button's media to something the native player can play. */
    fun requestFor(open: OpenMenu, mediaId: String, start: Double): PlayRequest? {
        val plex = server ?: return null
        val item = open.media[mediaId] ?: return null
        val part = item.part ?: return null
        return PlayRequest(plex.partUrl(part), plex.authHeaders(), start, item.title)
    }

    /**
     * Debug builds only: open a manifest straight from an asset server, without Plex (menu buttons then
     * report that the media isn't in the library). Used to test menus on a device before Plex is set up:
     *   adb shell am start -n org.menustream.tv/.MainActivity --es debugAssetServer http://10.0.2.2:8790 --es debugManifest sintel
     */
    fun debugOpen(assetServer: String, manifestMatch: String): Job = viewModelScope.launch {
        if (!BuildConfig.DEBUG) return@launch
        signInJob?.cancel()
        _screen.value = Screen.Busy("Debug: loading $manifestMatch from $assetServer…")
        val client = AssetServerClient(assetServer, "debug")
        val manifests = attempt { client.index().manifests }.getOrElse {
            _screen.value = Screen.Failed("Debug: ${it.message}")
            return@launch
        }
        val entry = manifests.firstOrNull { it.valid && it.path.contains(manifestMatch, ignoreCase = true) }
        if (entry == null) {
            _screen.value = Screen.Failed("Debug: no manifest matching '$manifestMatch'")
            return@launch
        }
        assets = client
        _screen.value = Screen.Menu(OpenMenu(PlexItem(ratingKey = "debug", title = entry.title?.name ?: "Debug"), entry, emptyMap(), null))
    }

    fun backToLibrary() {
        if (rows.isEmpty()) loadLibrary() else _screen.value = Screen.Library(rows)
    }
}

/** Like runCatching, but lets coroutine cancellation through instead of treating it as a failure. */
inline fun <T> attempt(block: () -> T): Result<T> = try {
    Result.success(block())
} catch (e: kotlin.coroutines.cancellation.CancellationException) {
    throw e
} catch (e: Throwable) {
    Result.failure(e)
}
