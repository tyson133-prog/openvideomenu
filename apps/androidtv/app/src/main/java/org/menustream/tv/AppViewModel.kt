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
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import org.menustream.tv.assets.AssetServerClient
import org.menustream.tv.assets.CatalogEntry
import org.menustream.tv.assets.discoverAssetServers
import org.menustream.tv.menu.LastStop
import org.menustream.tv.menu.MenuMatcher
import org.menustream.tv.player.PlayRequest
import org.menustream.tv.plex.PlexAccount
import org.menustream.tv.plex.PlexDirectory
import org.menustream.tv.plex.PlexIdentity
import org.menustream.tv.plex.PlexItem
import org.menustream.tv.plex.PlexLetter
import org.menustream.tv.plex.PlexResource
import org.menustream.tv.plex.PlexServer
import org.menustream.tv.store.Settings

/** Something on screen you can pick: a movie, show or episode, or a disc (a TV disc manifest). */
sealed interface Tile {
    val key: String

    data class Media(val item: PlexItem, val menu: CatalogEntry?) : Tile {
        override val key get() = item.ratingKey
    }

    data class Disc(val entry: CatalogEntry, val show: PlexItem?) : Tile {
        override val key get() = "disc:" + entry.id
    }
}

data class TileRow(val title: String, val tiles: List<Tile>)

/** A title opened into its disc menu: manifest media ids resolved to Plex items. */
data class OpenMenu(val title: String, val entry: CatalogEntry, val media: Map<String, PlexItem>, val lastStop: LastStop?)

data class ShowState(val show: PlexItem, val seasons: List<PlexItem>, val season: PlexItem?, val episodes: List<PlexItem>, val discs: List<Tile.Disc>)

sealed interface Screen {
    data class Busy(val message: String) : Screen
    data class SignIn(val code: String?, val error: String? = null) : Screen
    data class PickServer(val servers: List<PlexResource>) : Screen
    data object Browse : Screen
    data class Show(val state: ShowState) : Screen
    data class Menu(val open: OpenMenu) : Screen
    data class Play(val request: PlayRequest) : Screen
    data class Failed(val message: String) : Screen
}

/** The tabs across the top of the browse screen. */
sealed interface BrowseTab {
    val id: String
    val title: String

    data object Discs : BrowseTab {
        override val id = "discs"
        override val title = "Disc Menus"
    }

    data class Library(val section: PlexDirectory) : BrowseTab {
        override val id get() = "lib:" + section.key
        override val title get() = section.title
    }

    data object Search : BrowseTab {
        override val id = "search"
        override val title = "Search"
    }
}

/** Where menus come from: shown in the browse header. */
data class MenuSource(val url: String?, val status: String)

/** Browse A–Z within a library: the letters, the chosen one, and its titles. */
data class LetterState(val letters: List<PlexLetter> = emptyList(), val selected: PlexLetter? = null, val tiles: List<Tile> = emptyList())

class AppViewModel(app: Application) : AndroidViewModel(app) {
    private val settings = Settings(app)
    private val identity = PlexIdentity(settings.clientId)
    private val account = PlexAccount(identity)

    private val _screen = MutableStateFlow<Screen>(Screen.Busy("Starting…"))
    val screen: StateFlow<Screen> = _screen.asStateFlow()
    private val stack = ArrayDeque<Screen>()

    private val _menuSource = MutableStateFlow(MenuSource(settings.assetServer, "Looking for a menu server…"))
    val menuSource: StateFlow<MenuSource> = _menuSource.asStateFlow()

    private val _tabs = MutableStateFlow<List<BrowseTab>>(emptyList())
    val tabs: StateFlow<List<BrowseTab>> = _tabs.asStateFlow()
    private val _rows = MutableStateFlow<Map<String, List<TileRow>>>(emptyMap())
    val rows: StateFlow<Map<String, List<TileRow>>> = _rows.asStateFlow()
    private val _letters = MutableStateFlow<Map<String, LetterState>>(emptyMap())
    val letters: StateFlow<Map<String, LetterState>> = _letters.asStateFlow()
    private val _searchResults = MutableStateFlow<List<Tile>>(emptyList())
    val searchResults: StateFlow<List<Tile>> = _searchResults.asStateFlow()

    var server: PlexServer? = null
        private set
    var assets: AssetServerClient? = null
        private set
    private var catalog: List<CatalogEntry> = emptyList()
    private var sections: List<PlexDirectory> = emptyList()
    private var signInJob: Job? = null

    init {
        start()
    }

    // --- navigation ---------------------------------------------------------------------------------

    private fun go(screen: Screen) {
        val current = _screen.value
        if (current is Screen.Browse || current is Screen.Show) stack.addLast(current)
        _screen.value = screen
    }

    /** Back from a menu, player or show screen to where the viewer came from. */
    fun back() {
        _screen.value = stack.removeLastOrNull() ?: Screen.Browse
    }

    // --- sign-in and server -------------------------------------------------------------------------

    fun start(): Job = viewModelScope.launch {
        val token = settings.plexToken ?: return@launch signIn()
        if (_screen.value !is Screen.Busy) _screen.value = Screen.Busy("Connecting to Plex…")
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
            _screen.value = Screen.Busy("Signed in. Finding your Plex servers…")
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
        loadBrowse()
    }

    // --- browsing ----------------------------------------------------------------------------------

    fun loadBrowse(): Job = viewModelScope.launch {
        val plex = server ?: run { start(); return@launch }
        _screen.value = Screen.Busy("Loading your libraries…")
        sections = attempt { plex.sections().filter { it.type == "movie" || it.type == "show" } }.getOrElse {
            _screen.value = Screen.Failed("Couldn't load your libraries: ${it.message}")
            return@launch
        }
        _tabs.value = listOf(BrowseTab.Discs) + sections.map { BrowseTab.Library(it) } + BrowseTab.Search
        stack.clear()
        _screen.value = Screen.Browse
        loadMenuServer()
        loadDiscsTab()
    }

    private fun tile(item: PlexItem): Tile.Media =
        Tile.Media(item, if (item.type == "movie") item.part?.file?.let { MenuMatcher.manifestFor(it, catalog) } else null)

    /** Rows for a library tab, loaded when the tab is first shown. */
    fun loadTab(tab: BrowseTab): Job = viewModelScope.launch {
        val plex = server ?: return@launch
        if (tab !is BrowseTab.Library || _rows.value.containsKey(tab.id)) return@launch
        val key = tab.section.key
        _rows.update { it + (tab.id to emptyList()) } // mark as loading
        val rows = listOf(
            TileRow("Continue Watching", attempt { plex.onDeck(key) }.getOrDefault(emptyList()).map(::tile)),
            TileRow("Recently Added", attempt { plex.recentlyAdded(key) }.getOrDefault(emptyList()).map(::tile)),
        ).filter { it.tiles.isNotEmpty() }
        _rows.update { it + (tab.id to rows) }
        val letters = attempt { plex.letters(key) }.getOrDefault(emptyList())
        _letters.update { it + (tab.id to LetterState(letters)) }
        letters.firstOrNull()?.let { selectLetter(tab, it) }
    }

    fun selectLetter(tab: BrowseTab.Library, letter: PlexLetter): Job = viewModelScope.launch {
        val plex = server ?: return@launch
        _letters.update { it + (tab.id to (it[tab.id] ?: LetterState()).copy(selected = letter, tiles = emptyList())) }
        val items = attempt { plex.byLetter(tab.section.key, letter) }.getOrDefault(emptyList())
        _letters.update { it + (tab.id to (it[tab.id] ?: LetterState()).copy(selected = letter, tiles = items.map(::tile))) }
    }

    fun search(query: String): Job = viewModelScope.launch {
        val plex = server ?: return@launch
        if (query.isBlank()) {
            _searchResults.value = emptyList()
            return@launch
        }
        _searchResults.value = attempt { plex.search(query) }.getOrDefault(emptyList()).map(::tile)
    }

    /** The Disc Menus tab: every title with a menu, plus what's in progress and new. */
    private suspend fun loadDiscsTab() {
        val plex = server ?: return
        val rows = listOf(
            TileRow("Disc Menus", resolveCatalogTiles()),
            TileRow("Continue Watching", attempt { plex.onDeck() }.getOrDefault(emptyList()).map(::tile)),
            TileRow("Recently Added", attempt { plex.recentlyAdded() }.getOrDefault(emptyList()).map(::tile)),
        ).filter { it.tiles.isNotEmpty() }
        _rows.update { it + (BrowseTab.Discs.id to rows) }
    }

    /**
     * Find the Plex title for each manifest: a movie whose file matches, or else the show a TV disc
     * belongs to (by the manifest's title).
     */
    private suspend fun resolveCatalogTiles(): List<Tile> {
        val plex = server ?: return emptyList()
        val movieSections = sections.filter { it.type == "movie" }
        val showSections = sections.filter { it.type == "show" }
        return catalog.filter { it.valid && it.hasMenus }.mapNotNull { entry ->
            val name = entry.title?.name ?: return@mapNotNull null
            val movie = movieSections.firstNotNullOfOrNull { s ->
                attempt { plex.moviesTitled(s.key, name) }.getOrDefault(emptyList()).firstOrNull { m ->
                    m.part?.file?.let { f -> MenuMatcher.manifestFor(f, listOf(entry)) != null } == true
                }
            }
            if (movie != null) return@mapNotNull Tile.Media(movie, entry)
            val show = showSections.firstNotNullOfOrNull { s -> attempt { plex.showsTitled(s.key, name) }.getOrDefault(emptyList()).firstOrNull() }
            Tile.Disc(entry, show)
        }
    }

    // --- the menu server ---------------------------------------------------------------------------

    /** Find the asset server (saved, else discovered) and load its catalog of menus. */
    private suspend fun loadMenuServer() {
        val plex = server ?: return
        val url = settings.assetServer ?: run {
            _menuSource.value = MenuSource(null, "Looking for a menu server…")
            withTimeoutOrNull(8_000) { discoverAssetServers(getApplication()).first { it.isNotEmpty() } }?.first()?.url
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
        _menuSource.value = MenuSource(url, "Menus from $url · ${catalog.count { it.valid && it.hasMenus }} available")
    }

    fun useAssetServer(url: String?) {
        settings.assetServer = url?.trim()?.trimEnd('/')?.let { if (it.startsWith("http")) it else "http://$it" }
        _rows.value = emptyMap()
        _letters.value = emptyMap()
        loadBrowse()
    }

    // --- opening things ----------------------------------------------------------------------------

    fun open(tile: Tile): Job = viewModelScope.launch {
        when (tile) {
            is Tile.Disc -> openDisc(tile)
            is Tile.Media -> when (tile.item.type) {
                "show" -> openShow(tile.item)
                "movie" -> if (tile.menu != null) openMovieMenu(tile.item, tile.menu) else play(tile.item, resumeAt(tile.item))
                else -> play(tile.item, resumeAt(tile.item))
            }
        }
    }

    private fun resumeAt(item: PlexItem) = (item.viewOffset ?: 0) / 1000.0

    private suspend fun openMovieMenu(movie: PlexItem, entry: CatalogEntry) {
        val plex = server ?: return
        val full = plex.item(movie.ratingKey) ?: movie
        val extras = attempt { plex.extras(movie.ratingKey) }.getOrDefault(emptyList())
        val files = (listOf(full) + extras).mapNotNull { item -> item.part?.file?.let { it to item } }
        val media = MenuMatcher.resolveMedia(entry, files)
        val featureId = media.entries.firstOrNull { it.value.ratingKey == full.ratingKey }?.key
        val lastStop = full.viewOffset?.takeIf { it > 0 && featureId != null }?.let { LastStop(featureId!!, it / 1000.0) }
        go(Screen.Menu(OpenMenu(full.title, entry, media, lastStop)))
    }

    private suspend fun openShow(show: PlexItem) {
        val plex = server ?: return
        val seasons = attempt { plex.children(show.ratingKey) }.getOrDefault(emptyList())
        val season = seasons.firstOrNull { (it.index ?: 0) > 0 } ?: seasons.firstOrNull()
        val episodes = season?.let { attempt { plex.children(it.ratingKey) }.getOrDefault(emptyList()) } ?: emptyList()
        go(Screen.Show(ShowState(show, seasons, season, episodes, emptyList())))
        // Discs: manifests whose media are this show's episode files.
        val files = attempt { plex.allEpisodes(show.ratingKey) }.getOrDefault(emptyList()).mapNotNull { it.part?.file }
        val discs = catalog
            .filter { e -> e.valid && e.hasMenus && e.media.any { m -> m.path != null && files.any { f -> MenuMatcher.matches(m.path, f) } } }
            .sortedBy { it.path.lowercase() }
            .map { Tile.Disc(it, show) }
        (_screen.value as? Screen.Show)?.takeIf { it.state.show.ratingKey == show.ratingKey }?.let {
            _screen.value = Screen.Show(it.state.copy(discs = discs))
        }
    }

    fun selectSeason(season: PlexItem): Job = viewModelScope.launch {
        val plex = server ?: return@launch
        val current = (_screen.value as? Screen.Show)?.state ?: return@launch
        val episodes = attempt { plex.children(season.ratingKey) }.getOrDefault(emptyList())
        _screen.value = Screen.Show(current.copy(season = season, episodes = episodes))
    }

    private suspend fun openDisc(disc: Tile.Disc) {
        val plex = server ?: return
        val show = disc.show
        val episodes = show?.let { attempt { plex.allEpisodes(it.ratingKey) }.getOrDefault(emptyList()) } ?: emptyList()
        val extras = show?.let { attempt { plex.extras(it.ratingKey) }.getOrDefault(emptyList()) } ?: emptyList()
        val files = (episodes + extras).mapNotNull { item -> item.part?.file?.let { it to item } }
        val media = MenuMatcher.resolveMedia(disc.entry, files)
        go(Screen.Menu(OpenMenu(disc.entry.title?.name ?: show?.title ?: "Disc", disc.entry, media, null)))
    }

    fun play(item: PlexItem, startSeconds: Double) {
        val plex = server ?: return
        val part = item.part ?: return
        go(Screen.Play(PlayRequest(plex.partUrl(part), plex.authHeaders(), startSeconds, item.title, reporterFor(item))))
    }

    /**
     * Sends playback state to Plex (timeline), so resume points and watched status match other Plex apps.
     * One session id per playback; failures are ignored (playback matters more than the report).
     */
    private fun reporterFor(item: PlexItem): org.menustream.tv.player.PlaybackReporter {
        val plex = server
        val session = java.util.UUID.randomUUID().toString()
        return org.menustream.tv.player.PlaybackReporter { state, positionMs, durationMs ->
            if (plex == null) return@PlaybackReporter
            viewModelScope.launch {
                attempt { plex.timeline(item.ratingKey, state, positionMs, durationMs.takeIf { it > 0 } ?: (item.duration ?: 0), session) }
            }
        }
    }

    /** Resolve a menu button's media to something the native player can play. */
    fun requestFor(open: OpenMenu, mediaId: String, start: Double): PlayRequest? {
        val plex = server ?: return null
        val item = open.media[mediaId] ?: return null
        val part = item.part ?: return null
        return PlayRequest(plex.partUrl(part), plex.authHeaders(), start, item.title, reporterFor(item))
    }

    fun imageUrl(path: String?, width: Int, height: Int): String? = path?.let { server?.imageUrl(it, width, height) }

    fun imageHeaders(): Map<String, String> = server?.authHeaders() ?: emptyMap()

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
        _screen.value = Screen.Menu(OpenMenu(entry.title?.name ?: "Debug", entry, emptyMap(), null))
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
