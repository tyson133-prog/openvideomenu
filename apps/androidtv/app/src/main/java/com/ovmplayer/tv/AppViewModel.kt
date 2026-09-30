package com.ovmplayer.tv

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
import com.ovmplayer.tv.assets.AssetServerClient
import com.ovmplayer.tv.assets.CatalogEntry
import com.ovmplayer.tv.assets.discoverAssetServers
import com.ovmplayer.tv.jellyfin.FoundJellyfin
import com.ovmplayer.tv.jellyfin.JellyfinAccount
import com.ovmplayer.tv.jellyfin.JfAuthResult
import com.ovmplayer.tv.jellyfin.JfPublicInfo
import com.ovmplayer.tv.jellyfin.JellyfinSource
import com.ovmplayer.tv.jellyfin.discoverJellyfin
import com.ovmplayer.tv.media.Item
import com.ovmplayer.tv.media.ItemType
import com.ovmplayer.tv.media.Letter
import com.ovmplayer.tv.media.Library
import com.ovmplayer.tv.media.LibraryKind
import com.ovmplayer.tv.media.MediaSource
import com.ovmplayer.tv.media.Playable
import com.ovmplayer.tv.media.filesOf
import com.ovmplayer.tv.menu.LastStop
import com.ovmplayer.tv.menu.MenuIndex
import com.ovmplayer.tv.menu.MenuMatcher
import com.ovmplayer.tv.net.Http
import com.ovmplayer.tv.player.PlayRequest
import com.ovmplayer.tv.plex.HttpStatusException
import com.ovmplayer.tv.plex.PlexAccount
import com.ovmplayer.tv.plex.PlexIdentity
import com.ovmplayer.tv.plex.PlexResource
import com.ovmplayer.tv.plex.PlexServer
import com.ovmplayer.tv.plex.PlexSource
import com.ovmplayer.tv.store.JellyfinLogin
import com.ovmplayer.tv.store.Settings
import com.ovmplayer.tv.store.Stop

/** Something on screen you can pick: a movie, show or episode, or a manifest (a TV show's menu). */
sealed interface Tile {
    val key: String

    /** [rowKey] keeps a tile's identity (and focus) when it replaces a placeholder in the Menus row. */
    data class Media(val item: Item, val menu: CatalogEntry?, val rowKey: String? = null) : Tile {
        override val key get() = rowKey ?: item.id
    }

    /** [pending]: a Menus placeholder whose title is still being looked up. */
    data class Manifest(val entry: CatalogEntry, val show: Item?, val pending: Boolean = false) : Tile {
        override val key get() = "manifest:" + entry.id
    }
}

data class TileRow(val title: String, val tiles: List<Tile>)

/** A title opened into its menu: manifest media ids resolved to the server's files. */
/**
 * A title offered when a menu opens: [label] is the menu's own name for it. [position] 0 means "Play"
 * (the film when it wasn't started). [audio] and [subtitle] are the tracks to use.
 */
data class Resume(
    val mediaId: String,
    val item: Item,
    val position: Double,
    val label: String,
    val audio: Int?,
    val subtitle: Int?,
    val playedAtMs: Long,
)

data class OpenMenu(
    val title: String,
    val entry: CatalogEntry,
    val media: Map<String, Playable>,
    val lastStop: LastStop?,
    /** Level 2: media id → its angle 2, 3, … files (null where the library has no match). */
    val angles: Map<String, List<Playable?>> = emptyMap(),
)

data class ShowState(val show: Item, val seasons: List<Item>, val season: Item?, val episodes: List<Item>, val manifests: List<Tile.Manifest>)

sealed interface Screen {
    data class Busy(val message: String) : Screen
    /** First run (or after signing out): Plex or Jellyfin? */
    data object ChooseSource : Screen
    data class SignIn(val code: String?, val error: String? = null) : Screen
    data class PickServer(val servers: List<PlexResource>) : Screen
    /** Jellyfin servers found on the network, plus typing an address. */
    data class FindJellyfin(val servers: List<FoundJellyfin>, val searching: Boolean, val error: String? = null) : Screen
    /** Quick Connect: the code to approve in another Jellyfin app. */
    /**
     * Signing in to a Jellyfin server: a Quick Connect [code] (when the server allows it; otherwise
     * [quickConnectNote] says why not) and a username/password form, whichever the viewer uses.
     */
    data class JellyfinSignIn(
        val server: String,
        val code: String? = null,
        val quickConnectNote: String? = null,
        val passwordError: String? = null,
        val busy: Boolean = false,
        val connecting: Boolean = false,
    ) : Screen
    data object Browse : Screen
    data class Show(val state: ShowState) : Screen
    data class Menu(val open: OpenMenu) : Screen
    /** A menu with titles stopped partway: resume one of them, or open the menu? */
    data class ResumeAsk(val open: OpenMenu, val resumes: List<Resume>, val justTheFilm: Boolean) : Screen
    data class Play(val request: PlayRequest) : Screen
    data class Failed(val message: String) : Screen
}

/** The tabs across the top of the browse screen. */
sealed interface BrowseTab {
    val id: String
    val title: String

    data object Menus : BrowseTab {
        override val id = "menus"
        override val title = "Menus"
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

/** Movies per request when paging a library for the Menus row. */
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
        settings.lastServer = com.ovmplayer.tv.store.LastServer(server.name, server.machineId, server.baseUrl, server.token)
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

    /** The Jellyfin server being signed in to. */
    private class PendingJellyfin(val account: JellyfinAccount, val url: String, val info: JfPublicInfo)

    private var pendingJellyfin: PendingJellyfin? = null
    private var passwordJob: Job? = null

    /**
     * Sign in to the Jellyfin server at [address]. The screen offers both ways at once: a Quick Connect
     * code (approved in another Jellyfin app; shown while the server allows it, and renewed when it
     * expires) and a username/password form ([signInWithPassword]). Whichever finishes first wins.
     */
    fun useJellyfin(address: String) {
        signInJob?.cancel()
        passwordJob?.cancel()
        pendingJellyfin = null
        signInJob = viewModelScope.launch {
            val url = address.trim().trimEnd('/').let { if (it.startsWith("http")) it else "http://$it" }
                .let { if (Regex(":\\d+$").containsMatchIn(it.substringAfter("//"))) it else "$it:8096" }
            val jf = JellyfinAccount(url, settings.clientId)
            _screen.value = Screen.JellyfinSignIn(url, connecting = true)
            val info = attempt { jf.publicInfo() }.getOrElse {
                _screen.value = Screen.FindJellyfin(emptyList(), searching = false, error = "No Jellyfin server answered at $url (${it.message}).")
                return@launch
            }
            val pending = PendingJellyfin(jf, url, info).also { pendingJellyfin = it }
            _screen.value = Screen.JellyfinSignIn(info.serverName)
            if (!attempt { jf.quickConnectEnabled() }.getOrDefault(false)) {
                updateSignIn { it.copy(quickConnectNote = "Quick Connect is turned off on this server.") }
                return@launch
            }
            while (true) {
                val start = attempt { jf.initiate() }.getOrElse {
                    updateSignIn { s -> s.copy(quickConnectNote = "Couldn't start Quick Connect: ${it.message}") }
                    return@launch
                }
                updateSignIn { it.copy(code = start.code, quickConnectNote = null) }
                val approved = withTimeoutOrNull(10 * 60_000L) {
                    while (attempt { jf.check(start.secret).authenticated }.getOrDefault(false).not()) delay(3_000)
                    true
                }
                if (approved == null) continue // the code expired: show a new one
                val auth = attempt { jf.authenticate(start.secret) }.getOrElse {
                    updateSignIn { s -> s.copy(code = null, quickConnectNote = "Sign-in failed: ${it.message}") }
                    return@launch
                }
                passwordJob?.cancel()
                finishJellyfin(pending, auth)
                return@launch
            }
        }
    }

    /** The other way in: the viewer's Jellyfin username and password. */
    fun signInWithPassword(user: String, password: String) {
        val pending = pendingJellyfin ?: return
        if (user.isBlank()) {
            updateSignIn { it.copy(passwordError = "Enter your username.") }
            return
        }
        passwordJob?.cancel()
        passwordJob = viewModelScope.launch {
            updateSignIn { it.copy(busy = true, passwordError = null) }
            val auth = attempt { pending.account.authenticateByName(user.trim(), password) }.getOrElse { e ->
                val message = if (e is HttpStatusException && e.code == 401) "Wrong username or password." else "Sign-in failed: ${e.message}"
                updateSignIn { it.copy(busy = false, passwordError = message) }
                return@launch
            }
            signInJob?.cancel() // stop waiting for Quick Connect
            finishJellyfin(pending, auth)
        }
    }

    private fun updateSignIn(change: (Screen.JellyfinSignIn) -> Screen.JellyfinSignIn) {
        (_screen.value as? Screen.JellyfinSignIn)?.let { _screen.value = change(it) }
    }

    private fun finishJellyfin(pending: PendingJellyfin, auth: JfAuthResult) {
        pendingJellyfin = null
        val login = JellyfinLogin(pending.url, pending.info.id, pending.info.serverName, auth.user.id, auth.accessToken)
        settings.sourceKind = "jellyfin"
        settings.jellyfin = login
        startJellyfin(login)
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
        _tabs.value = listOf(BrowseTab.Menus) + libraries.map { BrowseTab.Libraries(it) } + BrowseTab.Search
        stack.clear()
        _screen.value = Screen.Browse
        loadMenuServer()
        loadMenusTab()
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
     * The Menus tab: every title with a menu, plus what's in progress and new. The row appears at
     * once with a placeholder per manifest (named from the manifest), and each fills in with its
     * poster as its title is found, so hundreds of menus never hold the screen up.
     */
    private suspend fun loadMenusTab() = coroutineScope {
        val src = source ?: return@coroutineScope
        val menus = catalog.filter { it.valid && it.hasMenus }.sortedBy { (it.title?.name ?: it.path).lowercase() }
        val found = HashMap<String, Tile>() // manifest id → its tile, once found
        var continueWatching = emptyList<Tile>()
        var recentlyAdded = emptyList<Tile>()
        // Everything below runs on the main thread (network calls switch threads inside), so the
        // state here needs no locking.
        fun publish() {
            val rows = listOf(
                TileRow("Menus", menus.map { found[it.id] ?: Tile.Manifest(it, null, pending = true) }),
                TileRow("Continue Watching", continueWatching),
                TileRow("Recently Added", recentlyAdded),
            ).filter { it.tiles.isNotEmpty() }
            _rows.update { it + (BrowseTab.Menus.id to rows) }
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
        if (BuildConfig.DEBUG) android.util.Log.d("ovmplayer", "menus: ${menus.size} placed in ${System.currentTimeMillis() - started} ms")
    }

    /**
     * Find the title for each manifest and report it with [onFound] as soon as it's known:
     *  1. Movies: page through each movie library once (a few requests, even for thousands of films)
     *     and match every file locally against the manifests.
     *  2. The rest are TV menus (or titles the server doesn't have): look for their show, a few at a time.
     */
    private suspend fun findCatalogTitles(menus: List<CatalogEntry>, onFound: (String, Tile) -> Unit) = coroutineScope {
        val src = source ?: return@coroutineScope
        val remaining = menus.associateBy { it.id }.toMutableMap()
        val index = MenuIndex(menus)
        for (lib in libraries.filter { it.kind == LibraryKind.MOVIES }) {
            var start = 0
            while (remaining.isNotEmpty()) {
                val page = attempt { src.moviesPage(lib, start, MOVIE_PAGE) }.getOrElse {
                    if (BuildConfig.DEBUG) android.util.Log.d("ovmplayer", "movies page ${lib.title} @$start failed: $it")
                    null
                } ?: break
                for (movie in page.items) {
                    // Every manifest naming one of its files (two manifests for one film is a setup
                    // mistake, but both should still find it).
                    for (entry in movie.files.mapNotNull { it.path }.flatMap(index::manifestsFor).distinct()) {
                        if (remaining.remove(entry.id) != null) onFound(entry.id, Tile.Media(movie, entry, rowKey = "manifest:" + entry.id))
                    }
                }
                start += page.items.size
                if (page.items.isEmpty() || start >= (page.total ?: Int.MAX_VALUE)) break
            }
        }
        if (BuildConfig.DEBUG) android.util.Log.d("ovmplayer", "menus: movie libraries done, ${menus.size - remaining.size} found, ${remaining.size} left for TV lookups")
        val showLibraries = libraries.filter { it.kind == LibraryKind.SHOWS }
        val recent = HashMap<String, List<Item>>() // per TV library, fetched once for all TV menus
        suspend fun recentEpisodes(lib: Library): List<Item> = recent[lib.id]
            ?: attempt { src.recentEpisodes(lib, 100) }.getOrDefault(emptyList()).also { recent[lib.id] = it }
        val gate = Semaphore(4)
        remaining.values.map { entry ->
            launch {
                val show = gate.withPermit {
                    entry.title?.name?.let { findShowForManifest(entry, it, showLibraries, ::recentEpisodes) }
                }
                onFound(entry.id, Tile.Manifest(entry, show))
            }
        }.joinAll()
    }

    /**
     * The show a TV manifest belongs to. Titles are unreliable (metadata agents rename shows), so
     * candidates come from the exact title, a search, and recently added episodes, and a candidate only
     * counts if one of its episode files matches a file the manifest references.
     */
    private suspend fun findShowForManifest(
        entry: CatalogEntry,
        name: String,
        showLibraries: List<Library>,
        recentEpisodes: suspend (Library) -> List<Item>,
    ): Item? {
        val src = source ?: return null
        val manifestFiles = entry.media.mapNotNull { it.path }
        if (manifestFiles.isEmpty()) return null
        fun episodeMatches(ep: Item) = ep.files.any { f -> f.path?.let { p -> manifestFiles.any { MenuMatcher.matches(it, p) } } == true }
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
        if (BuildConfig.DEBUG) android.util.Log.d("ovmplayer", "no show found for manifest '$name' (${entry.path})")
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
            if (BuildConfig.DEBUG) android.util.Log.d("ovmplayer", "native menus at $native failed: ${result.exceptionOrNull()}")
        }
        // 2. A Open Video Menu asset server: the saved one, else one found on the network.
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
        if (tile is Tile.Manifest && tile.pending) {
            // Picked before its title was found: wait for it (it's being looked up now).
            val ready = withTimeoutOrNull(30_000) {
                rows.map { r -> r[BrowseTab.Menus.id]?.flatMap { it.tiles }?.firstOrNull { it.key == tile.key } }
                    .first { it != null && !(it is Tile.Manifest && it.pending) }
            } ?: return@launch
            open(ready).join()
            return@launch
        }
        when (tile) {
            is Tile.Manifest -> openManifest(tile)
            is Tile.Media -> when (tile.item.type) {
                ItemType.SHOW -> openShow(tile.item)
                ItemType.MOVIE -> if (tile.menu != null) openMovieMenu(tile.item, tile.menu) else playFromLibrary(tile.item)
                else -> playFromLibrary(tile.item)
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
            android.util.Log.d("ovmplayer", "open ${movie.id} -> ${full.id}; files=${files.map { it.first }}")
            android.util.Log.d("ovmplayer", "manifest media=${entry.media.map { it.id + "=" + it.path }}; resolved=${media.mapValues { it.value.item.id + ":" + it.value.file.path }}")
        }
        openAsking(full.title, entry, media, film = full, angles = MenuMatcher.resolveAngles(entry, files))
    }

    /**
     * Open a manifest's menu; if titles it plays were stopped partway (the film, an extra, an episode),
     * ask first: the most recently played first, at most four. The menu's
     * own Resume button gets the newest either way.
     */
    private fun openAsking(title: String, entry: CatalogEntry, media: Map<String, Playable>, film: Item?, angles: Map<String, List<Playable?>> = emptyMap()) {
        val all = media.entries.distinctBy { it.value.item.id }.map { (id, p) ->
            val point = resumePointOf(p.item)
            Resume(id, p.item, point.position, entry.media.firstOrNull { it.id == id }?.label ?: p.item.title, point.audio, point.subtitle, point.atMs)
        }
        // Titles stopped partway, newest first; on a movie's menu the film leads ("Play …" when it
        // wasn't started), so it's always one press away.
        val filmEntry = film?.let { f -> all.firstOrNull { it.item.id == f.id } }
        val others = all.filter { it !== filmEntry && it.position > 0 }.sortedByDescending { it.playedAtMs }
        val resumes = ((if (filmEntry != null && (filmEntry.position > 0 || others.isNotEmpty())) listOf(filmEntry) else emptyList()) + others).take(4)
        val newest = resumes.filter { it.position > 0 }.maxByOrNull { it.playedAtMs }
        val open = OpenMenu(title, entry, media, newest?.let { LastStop(it.mediaId, it.position) }, angles)
        val justTheFilm = resumes.size == 1 && filmEntry != null && resumes[0] === filmEntry
        go(if (newest != null) Screen.ResumeAsk(open, resumes, justTheFilm) else Screen.Menu(open))
    }

    /**
     * Where to pick [item] up and with which tracks: this app's record of where the viewer stopped
     * (with the soundtrack and subtitles they had), or the server's resume point when that's newer
     * (watched elsewhere; tracks then default to the first soundtrack, subtitles off).
     */
    private fun resumePointOf(item: Item): Stop {
        val mine = settings.stopOf(stopKey(item))
        return if (mine != null && !changedElsewhere(item, mine)) mine.copy(audio = mine.audio ?: 1, subtitle = mine.subtitle ?: 0)
        else Stop(item.resumeSeconds, 1, 0, item.lastPlayedMs ?: 0)
    }

    /**
     * Whether the server's state for [item] comes from viewing somewhere else (another device or app)
     * after this app last played it. Otherwise this app's record stands, even when the server reset
     * its resume point because of a short play here.
     */
    private fun changedElsewhere(item: Item, mine: Stop) = (item.lastPlayedMs ?: 0) > maxOf(mine.atMs, mine.touchedAtMs) + 60_000

    /** When each title's current playback started here, for the 10-second rule in [recordStop]. */
    private val playStartedAt = HashMap<String, Long>()

    /**
     * Playback of [item] starts here: note it. With no record yet, keep the server's current resume
     * point as this app's own first, so a short play here (which may reset the server's) can't lose it.
     */
    private fun touchStop(item: Item) {
        val key = stopKey(item)
        val now = System.currentTimeMillis()
        playStartedAt[item.id] = now
        val mine = settings.stopOf(key)
        when {
            mine != null && !changedElsewhere(item, mine) -> settings.rememberStop(key, mine.copy(touchedAtMs = now))
            item.resumeSeconds > 0 -> settings.rememberStop(key, Stop(item.resumeSeconds, 1, 0, item.lastPlayedMs ?: now, touchedAtMs = now))
            mine != null -> settings.forgetStop(key)
        }
    }

    private fun stopKey(item: Item): String {
        val server = if (settings.sourceKind == "jellyfin") settings.jellyfin?.serverId else settings.serverId
        return "${server ?: "server"}|${item.id}"
    }

    /** The answer to [Screen.ResumeAsk]: a title from where it stopped, or (null) the menu. */
    fun resumeChosen(resume: Resume?) {
        val ask = _screen.value as? Screen.ResumeAsk ?: return
        if (resume != null) play(resume.item, resume.position, resume.audio, resume.subtitle) else _screen.value = Screen.Menu(ask.open)
    }

    private suspend fun openShow(show: Item) {
        val src = source ?: return
        val children = attempt { src.children(show) }
        if (BuildConfig.DEBUG) android.util.Log.d(
            "ovmplayer",
            "show ${show.id} '${show.title}' children: ${children.getOrNull()?.map { "${it.type}:${it.seasonNumber}:${it.title}" } ?: children.exceptionOrNull()}",
        )
        val seasons = children.getOrDefault(emptyList())
        val season = seasons.firstOrNull { it.type == ItemType.SEASON && (it.seasonNumber ?: 0) > 0 } ?: seasons.firstOrNull()
        val episodes = season?.let { attempt { src.children(it) }.getOrDefault(emptyList()) } ?: emptyList()
        go(Screen.Show(ShowState(show, seasons, season, episodes, emptyList())))
        // Menus: manifests whose media are this show's episode files.
        val files = attempt { src.allEpisodes(show) }.getOrDefault(emptyList()).flatMap { e -> e.files.mapNotNull { it.path } }
        val manifests = files.flatMap(menuIndex::manifestsFor).distinct()
            .sortedBy { it.path.lowercase() }
            .map { Tile.Manifest(it, show) }
        (_screen.value as? Screen.Show)?.takeIf { it.state.show.id == show.id }?.let {
            _screen.value = Screen.Show(it.state.copy(manifests = manifests))
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

    private suspend fun openManifest(manifest: Tile.Manifest) {
        val src = source ?: return
        val show = manifest.show
        val episodes = show?.let { attempt { src.allEpisodes(it) }.getOrDefault(emptyList()) } ?: emptyList()
        val extras = show?.let { attempt { src.extras(it) }.getOrDefault(emptyList()) } ?: emptyList()
        val files = filesOf(episodes + extras)
        val media = MenuMatcher.resolveMedia(manifest.entry, files)
        openAsking(manifest.entry.title?.name ?: show?.title ?: "Menu", manifest.entry, media, film = null, angles = MenuMatcher.resolveAngles(manifest.entry, files))
    }

    /** What plays now outside a menu, so where it stops can be remembered. */
    private var playingDirect: Triple<Item, Int?, Int?>? = null

    fun play(item: Item, startSeconds: Double, audio: Int? = null, subtitle: Int? = null): Job = viewModelScope.launch {
        val src = source ?: return@launch
        // List entries can come without files (e.g. some "continue watching" lists): fetch the item.
        val full = if (item.files.isEmpty()) attempt { src.item(item.id) }.getOrNull() ?: item else item
        val file = full.files.firstOrNull() ?: return@launch
        playingDirect = Triple(full, audio, subtitle)
        touchStop(full)
        val request = attempt { src.playRequest(full, file, startSeconds, audio, subtitle) }.getOrElse {
            _screen.value = Screen.Failed("Couldn't play ${item.title}: ${it.message}")
            return@launch
        }
        go(Screen.Play(request))
    }

    /** A library tile without a menu: from this app's last stop (with its tracks) when newest, else the server's. */
    private fun playFromLibrary(item: Item) {
        val mine = settings.stopOf(stopKey(item))
        if (mine != null && !changedElsewhere(item, mine)) play(item, mine.position, mine.audio, mine.subtitle)
        else play(item, item.resumeSeconds)
    }

    /** A title played outside a menu is done: stopped at [position], or [ended]. */
    fun playFinished(position: Double?, ended: Boolean) {
        playingDirect?.let { (item, audio, subtitle) -> recordStop(item, position, ended, audio, subtitle) }
        playingDirect = null
        back()
    }

    /** Where a title played outside a menu is now, saved as it plays (Home leaves without a stop). */
    fun directProgress(position: Double) {
        playingDirect?.let { (item, audio, subtitle) -> recordStop(item, position, ended = false, audio, subtitle) }
    }

    /** A title a menu played is done: remember where it stopped (or forget it once finished). */
    fun menuTitleFinished(open: OpenMenu, mediaId: String, position: Double?, ended: Boolean, audio: Int?, subtitle: Int?) {
        open.media[mediaId]?.let { recordStop(it.item, position, ended, audio, subtitle) }
    }

    /**
     * A new resume point replaces the previous one only once the viewer has watched 10 seconds of this
     * playback; a brief or accidental play leaves the previous point as it was. Finishing clears it.
     */
    private fun recordStop(item: Item, position: Double?, ended: Boolean, audio: Int?, subtitle: Int?) {
        val key = stopKey(item)
        val now = System.currentTimeMillis()
        if (ended) settings.forgetStop(key)
        else if (position != null && position >= 5 && now - (playStartedAt[item.id] ?: now) >= 10_000) {
            settings.rememberStop(key, Stop(position, audio, subtitle, now, touchedAtMs = now))
        }
    }

    /** Resolve a menu button's media to something the native player can play. */
    suspend fun requestFor(
        open: OpenMenu,
        mediaId: String,
        start: Double,
        audio: Int? = null,
        subtitle: Int? = null,
        angle: Int? = null,
        videoTrack: Int? = null,
    ): PlayRequest? {
        val src = source ?: return null
        val main = open.media[mediaId] ?: return null
        // Angle 2 and up of angle files is another file; without a match it plays angle 1 (spec 4.1).
        val (item, file) = angle?.takeIf { it > 1 }?.let { open.angles[mediaId]?.getOrNull(it - 2) } ?: main
        // Stops are remembered on the media itself: the angles line up in time.
        return attempt { src.playRequest(item, file, start, audio, subtitle) }.getOrNull()
            ?.copy(videoTrack = videoTrack)
            ?.also { touchStop(main.item) }
    }

    fun imageUrl(image: String?, width: Int, height: Int): String? = image?.let { source?.imageUrl(it, width, height) }

    fun imageHeaders(): Map<String, String> = source?.imageHeaders() ?: emptyMap()

    /** "Plex" or "Jellyfin", for messages like "isn't in your Plex library". */
    val sourceKind: String get() = source?.kind ?: "media"

    /**
     * Debug builds only: open a manifest straight from an asset server, without a media server (menu
     * buttons then report that the media isn't in the library). Used to test menus on a device:
     *   adb shell am start -n com.ovmplayer.tv/.MainActivity --es debugAssetServer http://10.0.2.2:8790 --es debugManifest sintel
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
