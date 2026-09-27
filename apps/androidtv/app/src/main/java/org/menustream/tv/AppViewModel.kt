package org.menustream.tv

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.joinAll
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withTimeoutOrNull
import org.menustream.tv.assets.AssetServerClient
import org.menustream.tv.assets.CatalogEntry
import org.menustream.tv.assets.discoverAssetServers
import org.menustream.tv.jellyfin.FoundJellyfin
import org.menustream.tv.jellyfin.JellyfinAccount
import org.menustream.tv.jellyfin.JellyfinSource
import org.menustream.tv.jellyfin.discoverJellyfin
import org.menustream.tv.media.Item
import org.menustream.tv.media.ItemType
import org.menustream.tv.media.Letter
import org.menustream.tv.media.Library
import org.menustream.tv.media.LibraryKind
import org.menustream.tv.media.MediaSource
import org.menustream.tv.media.Playable
import org.menustream.tv.media.filesOf
import org.menustream.tv.menu.LastStop
import org.menustream.tv.menu.MenuIndex
import org.menustream.tv.menu.MenuMatcher
import org.menustream.tv.net.Http
import org.menustream.tv.player.PlayRequest
import org.menustream.tv.plex.HttpStatusException
import org.menustream.tv.plex.PlexAccount
import org.menustream.tv.plex.PlexIdentity
import org.menustream.tv.plex.PlexResource
import org.menustream.tv.plex.PlexServer
import org.menustream.tv.plex.PlexSource
import org.menustream.tv.store.JellyfinLogin
import org.menustream.tv.store.Settings

/** Something on screen you can pick: a movie, show or episode, or a disc (a TV disc manifest). */
sealed interface Tile {
    val key: String

    /** [rowKey] keeps a tile's identity (and focus) when it replaces a placeholder in the Disc Menus row. */
    data class Media(val item: Item, val menu: CatalogEntry?, val rowKey: String? = null) : Tile {
        override val key get() = rowKey ?: item.id
    }

    /** [pending]: a Disc Menus placeholder whose title is still being looked up. */
    data class Disc(val entry: CatalogEntry, val show: Item?, val pending: Boolean = false) : Tile {
        override val key get() = "disc:" + entry.id
    }
}

data class TileRow(val title: String, val tiles: List<Tile>)

/** A title opened into its disc menu: manifest media ids resolved to the server's files. */
data class OpenMenu(val title: String, val entry: CatalogEntry, val media: Map<String, Playable>, val lastStop: LastStop?)

data class ShowState(val show: Item, val seasons: List<Item>, val season: Item?, val episodes: List<Item>, val discs: List<Tile.Disc>)

sealed interface Screen {
    data class Busy(val message: String) : Screen
    /** First run (or after signing out): Plex or Jellyfin? */
    data object ChooseSource : Screen
    data class SignIn(val code: String?, val error: String? = null) : Screen
    data class PickServer(val servers: List<PlexResource>) : Screen
    /** Jellyfin servers found on the network, plus typing an address. */
    data class FindJellyfin(val servers: List<FoundJellyfin>, val searching: Boolean, val error: String? = null) : Screen
    /** Quick Connect: the code to approve in another Jellyfin app. */
    data class QuickConnect(val server: String, val code: String?, val error: String? = null) : Screen
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

    data class Libraries(val library: Library) : BrowseTab {
        override val id get() = "lib:" + library.id
        override val title get() = library.title
    }

    data object Search : BrowseTab {
        override val id = "search"
        override val title = "Search"
    }
}

/** Where menus come from: shown in the browse header. */
data class MenuSource(val url: String?, val status: String)

/** Browse A–Z within a library: the letters, the chosen one, and its titles. */
data class LetterState(val letters: List<Letter> = emptyList(), val selected: Letter? = null, val tiles: List<Tile> = emptyList())

/** Movies per request when paging a library for the Disc Menus row. */
private const val MOVIE_PAGE = 300

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

    /** The media server this TV is signed in to (Plex or Jellyfin). */
    var source: MediaSource? = null
        private set
    var assets: AssetServerClient? = null
        private set
    private var catalog: List<CatalogEntry> = emptyList()
        set(value) {
            field = value
            menuIndex = MenuIndex(value)
        }
    private var menuIndex = MenuIndex(emptyList())
    private var libraries: List<Library> = emptyList()
    private var signInJob: Job? = null

    private var begun = false

    /** Start once the app may use the network (see [LocalNetwork]); later calls do nothing. */
    fun begin() {
        if (begun) return
        begun = true
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

    // --- choosing a server --------------------------------------------------------------------------

    fun start(): Job = viewModelScope.launch {
        // Installs from before Jellyfin support have a Plex token but no kind saved.
        when (settings.sourceKind ?: if (settings.plexToken != null) "plex" else null) {
            "plex" -> startPlex()
            "jellyfin" -> settings.jellyfin?.let(::startJellyfin) ?: findJellyfin()
            else -> _screen.value = Screen.ChooseSource
        }
    }

    fun choose(kind: String) {
        settings.sourceKind = kind
        if (kind == "plex") signIn() else findJellyfin()
    }

    fun signOut() {
        signInJob?.cancel()
        settings.signOut()
        source = null
        _rows.value = emptyMap()
        _letters.value = emptyMap()
        _screen.value = Screen.ChooseSource
    }

    // --- Plex ---------------------------------------------------------------------------------------

    private suspend fun startPlex() {
        val token = settings.plexToken ?: return signIn()
        if (_screen.value !is Screen.Busy) _screen.value = Screen.Busy("Connecting to Plex…")
        val servers = attempt { account.servers(token) }.getOrElse { e ->
            if (e is HttpStatusException && e.code == 401) {
                // The sign-in was revoked or expired: ask for a new one instead of showing an error.
                settings.plexToken = null
                return signIn()
            }
            // plex.tv unreachable (e.g. internet down): the home network may still work.
            val last = settings.lastServer
            val direct = last?.let { attempt { PlexServer.reconnect(it, identity) }.getOrNull() }
            if (direct != null) {
                source = PlexSource(direct)
                loadBrowse()
            } else {
                _screen.value = Screen.Failed("Couldn't reach plex.tv (${e.message}), and your Plex server didn't answer directly either.")
            }
            return
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
            settings.sourceKind = "plex"
            _screen.value = Screen.Busy("Signed in. Finding your Plex servers…")
            startPlex()
        }
    }

    fun connect(resource: PlexResource): Job = viewModelScope.launch {
        _screen.value = Screen.Busy("Connecting to ${resource.name}…")
        val server = attempt { PlexServer.connect(resource, identity) }.getOrElse {
            _screen.value = Screen.Failed(it.message ?: "Couldn't connect to ${resource.name}")
            return@launch
        }
        settings.serverId = resource.clientIdentifier
        settings.lastServer = org.menustream.tv.store.LastServer(server.name, server.machineId, server.baseUrl, server.token)
        source = PlexSource(server)
        loadBrowse()
    }

    // --- Jellyfin -----------------------------------------------------------------------------------

    /** Look for Jellyfin servers on the network (the viewer can also type an address). */
    fun findJellyfin(): Job = viewModelScope.launch {
        signInJob?.cancel()
        _screen.value = Screen.FindJellyfin(emptyList(), searching = true)
        val found = attempt { discoverJellyfin(getApplication()) }.getOrDefault(emptyList())
        if (_screen.value is Screen.FindJellyfin) _screen.value = Screen.FindJellyfin(found, searching = false)
    }

    /**
     * Sign in to the Jellyfin server at [address] with Quick Connect: show a code, wait for the viewer
     * to approve it in another Jellyfin app, then keep the token this device is given.
     */
    fun useJellyfin(address: String) {
        signInJob?.cancel()
        signInJob = viewModelScope.launch {
            val url = address.trim().trimEnd('/').let { if (it.startsWith("http")) it else "http://$it" }
                .let { if (Regex(":\\d+$").containsMatchIn(it.substringAfter("//"))) it else "$it:8096" }
            val jf = JellyfinAccount(url, settings.clientId)
            _screen.value = Screen.QuickConnect(url, null)
            val info = attempt { jf.publicInfo() }.getOrElse {
                _screen.value = Screen.FindJellyfin(emptyList(), searching = false, error = "No Jellyfin server answered at $url (${it.message}).")
                return@launch
            }
            if (!attempt { jf.quickConnectEnabled() }.getOrDefault(false)) {
                _screen.value = Screen.QuickConnect(info.serverName, null, "Quick Connect is turned off on ${info.serverName}. An admin can turn it on in Dashboard → General.")
                return@launch
            }
            val start = attempt { jf.initiate() }.getOrElse {
                _screen.value = Screen.QuickConnect(info.serverName, null, "Couldn't start Quick Connect: ${it.message}")
                return@launch
            }
            _screen.value = Screen.QuickConnect(info.serverName, start.code)
            val approved = withTimeoutOrNull(10 * 60_000L) {
                while (attempt { jf.check(start.secret).authenticated }.getOrDefault(false).not()) delay(3_000)
                true
            }
            if (approved == null) return@launch useJellyfin(url) // code expired: show a new one
            val auth = attempt { jf.authenticate(start.secret) }.getOrElse {
                _screen.value = Screen.QuickConnect(info.serverName, null, "Sign-in failed: ${it.message}")
                return@launch
            }
            val login = JellyfinLogin(url, info.id, info.serverName, auth.user.id, auth.accessToken)
            settings.sourceKind = "jellyfin"
            settings.jellyfin = login
            startJellyfin(login)
        }
    }

    private fun startJellyfin(login: JellyfinLogin) = viewModelScope.launch {
        // The server's name is a server setting and can change after sign-in: use the current one.
        val current = withTimeoutOrNull(3_000) {
            attempt { JellyfinAccount(login.url, settings.clientId).publicInfo() }.getOrNull()?.serverName
        }
        val fresh = if (!current.isNullOrBlank() && current != login.name) login.copy(name = current).also { settings.jellyfin = it } else login
        source = JellyfinSource(fresh.url, settings.clientId, fresh.token, fresh.userId, fresh.name)
        loadBrowse()
    }

    // --- browsing ----------------------------------------------------------------------------------

    fun loadBrowse(): Job = viewModelScope.launch {
        val src = source ?: run { start(); return@launch }
        _screen.value = Screen.Busy("Loading your libraries…")
        libraries = attempt { src.libraries() }.getOrElse {
            if (it is HttpStatusException && it.code == 401) {
                // Signed out on the server (e.g. the device was removed in Jellyfin): sign in again.
                settings.jellyfin = null
                settings.plexToken = null
                start()
                return@launch
            }
            _screen.value = Screen.Failed("Couldn't load your libraries from ${src.name}: ${it.message}")
            return@launch
        }
        _tabs.value = listOf(BrowseTab.Discs) + libraries.map { BrowseTab.Libraries(it) } + BrowseTab.Search
        stack.clear()
        _screen.value = Screen.Browse
        loadMenuServer()
        loadDiscsTab()
    }

    private fun tile(item: Item): Tile.Media =
        Tile.Media(item, if (item.type == ItemType.MOVIE) item.files.firstNotNullOfOrNull { f -> f.path?.let(menuIndex::manifestFor) } else null)

    /** Rows for a library tab, loaded when the tab is first shown. */
    fun loadTab(tab: BrowseTab): Job = viewModelScope.launch {
        val src = source ?: return@launch
        if (tab !is BrowseTab.Libraries || _rows.value.containsKey(tab.id)) return@launch
        _rows.update { it + (tab.id to emptyList()) } // mark as loading
        val rows = listOf(
            TileRow("Continue Watching", attempt { src.continueWatching(tab.library) }.getOrDefault(emptyList()).map(::tile)),
            TileRow("Recently Added", attempt { src.recentlyAdded(tab.library) }.getOrDefault(emptyList()).map(::tile)),
        ).filter { it.tiles.isNotEmpty() }
        _rows.update { it + (tab.id to rows) }
        val letters = attempt { src.letters(tab.library) }.getOrDefault(emptyList())
        _letters.update { it + (tab.id to LetterState(letters)) }
        letters.firstOrNull()?.let { selectLetter(tab, it) }
    }

    fun selectLetter(tab: BrowseTab.Libraries, letter: Letter): Job = viewModelScope.launch {
        val src = source ?: return@launch
        _letters.update { it + (tab.id to (it[tab.id] ?: LetterState()).copy(selected = letter, tiles = emptyList())) }
        val items = attempt { src.byLetter(tab.library, letter) }.getOrDefault(emptyList())
        _letters.update { it + (tab.id to (it[tab.id] ?: LetterState()).copy(selected = letter, tiles = items.map(::tile))) }
    }

    fun search(query: String): Job = viewModelScope.launch {
        val src = source ?: return@launch
        if (query.isBlank()) {
            _searchResults.value = emptyList()
            return@launch
        }
        _searchResults.value = attempt { src.search(query) }.getOrDefault(emptyList()).map(::tile)
    }

    /**
     * The Disc Menus tab: every title with a menu, plus what's in progress and new. The row appears at
     * once with a placeholder per manifest (named from the manifest), and each fills in with its
     * poster as its title is found, so hundreds of menus never hold the screen up.
     */
    private suspend fun loadDiscsTab() = coroutineScope {
        val src = source ?: return@coroutineScope
        val menus = catalog.filter { it.valid && it.hasMenus }.sortedBy { (it.title?.name ?: it.path).lowercase() }
        val found = HashMap<String, Tile>() // manifest id → its tile, once found
        var continueWatching = emptyList<Tile>()
        var recentlyAdded = emptyList<Tile>()
        // Everything below runs on the main thread (network calls switch threads inside), so the
        // state here needs no locking.
        fun publish() {
            val rows = listOf(
                TileRow("Disc Menus", menus.map { found[it.id] ?: Tile.Disc(it, null, pending = true) }),
                TileRow("Continue Watching", continueWatching),
                TileRow("Recently Added", recentlyAdded),
            ).filter { it.tiles.isNotEmpty() }
            _rows.update { it + (BrowseTab.Discs.id to rows) }
        }
        publish()
        launch {
            continueWatching = attempt { src.continueWatching() }.getOrDefault(emptyList()).map(::tile)
            publish()
        }
        launch {
            recentlyAdded = attempt { src.recentlyAdded() }.getOrDefault(emptyList()).map(::tile)
            publish()
        }
        val started = System.currentTimeMillis()
        findCatalogTitles(menus) { id, tile ->
            found[id] = tile
            publish()
        }
        if (BuildConfig.DEBUG) android.util.Log.d("MenuStream", "disc menus: ${menus.size} placed in ${System.currentTimeMillis() - started} ms")
    }

    /**
     * Find the title for each manifest and report it with [onFound] as soon as it's known:
     *  1. Movies: page through each movie library once (a few requests, even for thousands of films)
     *     and match every file locally against the manifests.
     *  2. The rest are TV discs (or titles the server doesn't have): look for their show, a few at a time.
     */
    private suspend fun findCatalogTitles(menus: List<CatalogEntry>, onFound: (String, Tile) -> Unit) = coroutineScope {
        val src = source ?: return@coroutineScope
        val remaining = menus.associateBy { it.id }.toMutableMap()
        val index = MenuIndex(menus)
        for (lib in libraries.filter { it.kind == LibraryKind.MOVIES }) {
            var start = 0
            while (remaining.isNotEmpty()) {
                val page = attempt { src.moviesPage(lib, start, MOVIE_PAGE) }.getOrElse {
                    if (BuildConfig.DEBUG) android.util.Log.d("MenuStream", "movies page ${lib.title} @$start failed: $it")
                    null
                } ?: break
                for (movie in page.items) {
                    // Every manifest naming one of its files (two manifests for one film is a setup
                    // mistake, but both should still find it).
                    for (entry in movie.files.mapNotNull { it.path }.flatMap(index::manifestsFor).distinct()) {
                        if (remaining.remove(entry.id) != null) onFound(entry.id, Tile.Media(movie, entry, rowKey = "disc:" + entry.id))
                    }
                }
                start += page.items.size
                if (page.items.isEmpty() || start >= (page.total ?: Int.MAX_VALUE)) break
            }
        }
        if (BuildConfig.DEBUG) android.util.Log.d("MenuStream", "disc menus: movie libraries done, ${menus.size - remaining.size} found, ${remaining.size} left for TV lookups")
        val showLibraries = libraries.filter { it.kind == LibraryKind.SHOWS }
        val recent = HashMap<String, List<Item>>() // per TV library, fetched once for all discs
        suspend fun recentEpisodes(lib: Library): List<Item> = recent[lib.id]
            ?: attempt { src.recentEpisodes(lib, 100) }.getOrDefault(emptyList()).also { recent[lib.id] = it }
        val gate = Semaphore(4)
        remaining.values.map { entry ->
            launch {
                val show = gate.withPermit {
                    entry.title?.name?.let { findShowForDisc(entry, it, showLibraries, ::recentEpisodes) }
                }
                onFound(entry.id, Tile.Disc(entry, show))
            }
        }.joinAll()
    }

    /**
     * The show a TV disc manifest belongs to. Titles are unreliable (metadata agents rename shows), so
     * candidates come from the exact title, a search, and recently added episodes, and a candidate only
     * counts if one of its episode files matches a file the disc references.
     */
    private suspend fun findShowForDisc(
        entry: CatalogEntry,
        name: String,
        showLibraries: List<Library>,
        recentEpisodes: suspend (Library) -> List<Item>,
    ): Item? {
        val src = source ?: return null
        val discFiles = entry.media.mapNotNull { it.path }
        if (discFiles.isEmpty()) return null
        fun episodeMatches(ep: Item) = ep.files.any { f -> f.path?.let { p -> discFiles.any { MenuMatcher.matches(it, p) } } == true }
        suspend fun confirmed(show: Item): Boolean =
            attempt { src.allEpisodes(show) }.getOrDefault(emptyList()).any(::episodeMatches)

        // 1. Exact title.
        for (lib in showLibraries) for (show in attempt { src.showsTitled(lib, name) }.getOrDefault(emptyList())) {
            if (confirmed(show)) return show
        }
        // 2. Search (catches small title differences).
        val searched = attempt { src.search(name) }.getOrDefault(emptyList())
        for (show in searched.filter { it.type == ItemType.SHOW }) if (confirmed(show)) return show
        // 3. Recently added episodes, matched by file; their show is the one.
        for (lib in showLibraries) {
            recentEpisodes(lib).firstOrNull(::episodeMatches)?.showId?.let { id ->
                attempt { src.item(id) }.getOrNull()?.let { return it }
            }
        }
        if (BuildConfig.DEBUG) android.util.Log.d("MenuStream", "no show found for disc '$name' (${entry.path})")
        return null
    }

    // --- the menu server ---------------------------------------------------------------------------

    /** Find the asset server (saved, else discovered) and load its catalog of menus. */
    private suspend fun loadMenuServer() {
        val src = source ?: return
        suspend fun discover(): String? {
            _menuSource.value = MenuSource(null, "Looking for a menu server…")
            return withTimeoutOrNull(8_000) { discoverAssetServers(getApplication()).first { it.isNotEmpty() } }?.first()?.url
        }
        fun client(url: String) = AssetServerClient(url, src.assetServerAuth, src.kind, Http.assets(getApplication()))
        // 1. The media server itself, if it serves menus natively (no separate asset server needed).
        src.nativeMenuServer()?.let { native ->
            val result = attempt { client(native).index().manifests }
            if (result.isSuccess) {
                catalog = result.getOrThrow()
                assets = client(native)
                _menuSource.value = MenuSource(native, "Menus from ${src.name} (built in) · ${catalog.count { it.valid && it.hasMenus }} available")
                return
            }
            if (BuildConfig.DEBUG) android.util.Log.d("MenuStream", "native menus at $native failed: ${result.exceptionOrNull()}")
        }
        // 2. A MenuStream asset server: the saved one, else one found on the network.
        var url = settings.assetServer ?: discover()
        if (url == null) {
            _menuSource.value = MenuSource(null, "No menu server found. Titles play without menus.")
            return
        }
        var result = attempt { client(url!!).index().manifests }
        // The saved address stopped answering (server moved to a new IP, or is off): look for it again.
        if (result.isFailure && result.exceptionOrNull()?.message?.contains("didn't accept") != true) {
            val found = discover()
            if (found != null && found != url) {
                url = found
                result = attempt { client(found).index().manifests }
            }
        }
        catalog = result.getOrElse {
            _menuSource.value = MenuSource(url, "Menu server at $url: ${it.message} Titles play without menus.")
            return
        }
        assets = client(url!!)
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

    /** The tile last opened, so the library focuses it again when the viewer comes back. */
    var returnFocusKey: String? = null
        private set

    fun open(tile: Tile): Job = viewModelScope.launch {
        returnFocusKey = tile.key
        if (tile is Tile.Disc && tile.pending) {
            // Picked before its title was found: wait for it (it's being looked up now).
            val ready = withTimeoutOrNull(30_000) {
                rows.map { r -> r[BrowseTab.Discs.id]?.flatMap { it.tiles }?.firstOrNull { it.key == tile.key } }
                    .first { it != null && !(it is Tile.Disc && it.pending) }
            } ?: return@launch
            open(ready).join()
            return@launch
        }
        when (tile) {
            is Tile.Disc -> openDisc(tile)
            is Tile.Media -> when (tile.item.type) {
                ItemType.SHOW -> openShow(tile.item)
                ItemType.MOVIE -> if (tile.menu != null) openMovieMenu(tile.item, tile.menu) else play(tile.item, tile.item.resumeSeconds)
                else -> play(tile.item, tile.item.resumeSeconds)
            }
        }
    }

    private suspend fun openMovieMenu(movie: Item, entry: CatalogEntry) {
        val src = source ?: return
        val full = attempt { src.item(movie.id) }.getOrNull() ?: movie
        val extras = attempt { src.extras(movie) }.getOrDefault(emptyList())
        val files = filesOf(listOf(full) + extras)
        val media = MenuMatcher.resolveMedia(entry, files)
        if (BuildConfig.DEBUG) {
            android.util.Log.d("MenuStream", "open ${movie.id} -> ${full.id}; files=${files.map { it.first }}")
            android.util.Log.d("MenuStream", "manifest media=${entry.media.map { it.id + "=" + it.path }}; resolved=${media.mapValues { it.value.item.id + ":" + it.value.file.path }}")
        }
        val featureId = media.entries.firstOrNull { it.value.item.id == full.id }?.key
        val lastStop = full.resumeSeconds.takeIf { it > 0 && featureId != null }?.let { LastStop(featureId!!, it) }
        go(Screen.Menu(OpenMenu(full.title, entry, media, lastStop)))
    }

    private suspend fun openShow(show: Item) {
        val src = source ?: return
        val children = attempt { src.children(show) }
        if (BuildConfig.DEBUG) android.util.Log.d(
            "MenuStream",
            "show ${show.id} '${show.title}' children: ${children.getOrNull()?.map { "${it.type}:${it.seasonNumber}:${it.title}" } ?: children.exceptionOrNull()}",
        )
        val seasons = children.getOrDefault(emptyList())
        val season = seasons.firstOrNull { it.type == ItemType.SEASON && (it.seasonNumber ?: 0) > 0 } ?: seasons.firstOrNull()
        val episodes = season?.let { attempt { src.children(it) }.getOrDefault(emptyList()) } ?: emptyList()
        go(Screen.Show(ShowState(show, seasons, season, episodes, emptyList())))
        // Discs: manifests whose media are this show's episode files.
        val files = attempt { src.allEpisodes(show) }.getOrDefault(emptyList()).flatMap { e -> e.files.mapNotNull { it.path } }
        val discs = files.flatMap(menuIndex::manifestsFor).distinct()
            .sortedBy { it.path.lowercase() }
            .map { Tile.Disc(it, show) }
        (_screen.value as? Screen.Show)?.takeIf { it.state.show.id == show.id }?.let {
            _screen.value = Screen.Show(it.state.copy(discs = discs))
        }
    }

    /** Debug builds: `--es debugShow <title>` opens that show's page once the library has loaded. */
    fun debugOpenShow(title: String): Job = viewModelScope.launch {
        if (!BuildConfig.DEBUG) return@launch
        screen.first { it is Screen.Browse }
        val src = source ?: return@launch
        val show = libraries.filter { it.kind == LibraryKind.SHOWS }.firstNotNullOfOrNull { lib ->
            attempt { src.showsTitled(lib, title) }.getOrDefault(emptyList()).firstOrNull()
        } ?: return@launch
        openShow(show)
    }

    fun selectSeason(season: Item): Job = viewModelScope.launch {
        val src = source ?: return@launch
        val current = (_screen.value as? Screen.Show)?.state ?: return@launch
        val episodes = attempt { src.children(season) }.getOrDefault(emptyList())
        _screen.value = Screen.Show(current.copy(season = season, episodes = episodes))
    }

    private suspend fun openDisc(disc: Tile.Disc) {
        val src = source ?: return
        val show = disc.show
        val episodes = show?.let { attempt { src.allEpisodes(it) }.getOrDefault(emptyList()) } ?: emptyList()
        val extras = show?.let { attempt { src.extras(it) }.getOrDefault(emptyList()) } ?: emptyList()
        val files = filesOf(episodes + extras)
        val media = MenuMatcher.resolveMedia(disc.entry, files)
        go(Screen.Menu(OpenMenu(disc.entry.title?.name ?: show?.title ?: "Disc", disc.entry, media, null)))
    }

    fun play(item: Item, startSeconds: Double): Job = viewModelScope.launch {
        val src = source ?: return@launch
        // List entries can come without files (e.g. some "continue watching" lists): fetch the item.
        val full = if (item.files.isEmpty()) attempt { src.item(item.id) }.getOrNull() ?: item else item
        val file = full.files.firstOrNull() ?: return@launch
        val request = attempt { src.playRequest(full, file, startSeconds) }.getOrElse {
            _screen.value = Screen.Failed("Couldn't play ${item.title}: ${it.message}")
            return@launch
        }
        go(Screen.Play(request))
    }

    /** Resolve a menu button's media to something the native player can play. */
    suspend fun requestFor(open: OpenMenu, mediaId: String, start: Double, audio: Int? = null, subtitle: Int? = null): PlayRequest? {
        val src = source ?: return null
        val (item, file) = open.media[mediaId] ?: return null
        return attempt { src.playRequest(item, file, start, audio, subtitle) }.getOrNull()
    }

    fun imageUrl(image: String?, width: Int, height: Int): String? = image?.let { source?.imageUrl(it, width, height) }

    fun imageHeaders(): Map<String, String> = source?.imageHeaders() ?: emptyMap()

    /** "Plex" or "Jellyfin", for messages like "isn't in your Plex library". */
    val sourceKind: String get() = source?.kind ?: "media"

    /**
     * Debug builds only: open a manifest straight from an asset server, without a media server (menu
     * buttons then report that the media isn't in the library). Used to test menus on a device:
     *   adb shell am start -n org.menustream.tv/.MainActivity --es debugAssetServer http://10.0.2.2:8790 --es debugManifest sintel
     */
    fun debugOpen(assetServer: String, manifestMatch: String): Job = viewModelScope.launch {
        if (!BuildConfig.DEBUG) return@launch
        signInJob?.cancel()
        _screen.value = Screen.Busy("Debug: loading $manifestMatch from $assetServer…")
        val client = AssetServerClient(assetServer, mapOf("X-Plex-Token" to "debug"), "debug", Http.assets(getApplication()))
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
