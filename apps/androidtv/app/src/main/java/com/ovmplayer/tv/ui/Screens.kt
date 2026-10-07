package com.ovmplayer.tv.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import kotlinx.coroutines.launch
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.tv.material3.Button
import androidx.tv.material3.Card
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import coil3.compose.AsyncImage
import coil3.network.NetworkHeaders
import coil3.network.httpHeaders
import coil3.request.ImageRequest
import com.ovmplayer.tv.AppViewModel
import com.ovmplayer.tv.OpenMenu
import com.ovmplayer.tv.menu.MenuWebView
import com.ovmplayer.tv.menu.PageMessage
import com.ovmplayer.tv.player.PlayRequest
import com.ovmplayer.tv.player.PlayerHandle
import com.ovmplayer.tv.player.VideoPlayer
import com.ovmplayer.tv.plex.PlexResource

private val Gold = Color(0xFFFFCF6A)

@Composable
fun Centered(content: @Composable () -> Unit) =
    Column(Modifier.fillMaxSize().padding(64.dp), verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
        content()
    }

@Composable
fun BusyScreen(message: String) = Centered { Text(message, style = MaterialTheme.typography.headlineSmall) }

@Composable
fun ResumeScreen(
    title: String,
    resumes: List<com.ovmplayer.tv.Resume>,
    justTheFilm: Boolean,
    onChoose: (com.ovmplayer.tv.Resume?) -> Unit,
) = Centered {
    Text(title, style = MaterialTheme.typography.displaySmall)
    Spacer(Modifier.height(12.dp))
    val prompt = if (justTheFilm) "You stopped at ${clock(resumes[0].position)}." else "Pick up where you left off:"
    Text(prompt, style = MaterialTheme.typography.titleLarge)
    Spacer(Modifier.height(32.dp))
    Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp)) {
        val focus = remember { FocusRequester() }
        resumes.forEachIndexed { i, r ->
            val label = when {
                justTheFilm -> "Resume from ${clock(r.position)}"
                r.position > 0 -> "Resume ${r.label} from ${clock(r.position)}"
                else -> "Play ${r.label}"
            }
            Button(onClick = { onChoose(r) }, modifier = if (i == 0) Modifier.focusRequester(focus) else Modifier) { Text(label) }
        }
        Button(onClick = { onChoose(null) }) { Text("Menu") }
        LaunchedEffect(Unit) { runCatching { focus.requestFocus() } }
    }
}

/** Seconds as h:mm:ss (m:ss under an hour). */
private fun clock(seconds: Double): String {
    val t = seconds.toLong()
    val (h, m, s) = Triple(t / 3600, t % 3600 / 60, t % 60)
    return if (h > 0) "%d:%02d:%02d".format(h, m, s) else "%d:%02d".format(m, s)
}

@Composable
fun FailedScreen(message: String, onRetry: () -> Unit, onSignOut: () -> Unit) = Centered {
    Text(message, style = MaterialTheme.typography.headlineSmall)
    Spacer(Modifier.height(24.dp))
    Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        val focus = remember { FocusRequester() }
        Button(onClick = onRetry, modifier = Modifier.focusRequester(focus)) { Text("Try again") }
        Button(onClick = onSignOut) { Text("Sign out") }
        LaunchedEffect(Unit) { focus.requestFocus() }
    }
}

@Composable
fun SignInScreen(code: String?, error: String?, onRetry: () -> Unit) = Centered {
    Text("Sign in with Plex", style = MaterialTheme.typography.displaySmall)
    Spacer(Modifier.height(32.dp))
    if (code != null) {
        Text("On your phone or computer, go to", style = MaterialTheme.typography.titleLarge)
        Text("plex.tv/link", style = MaterialTheme.typography.displayMedium.copy(color = Gold))
        Spacer(Modifier.height(16.dp))
        Text("and enter", style = MaterialTheme.typography.titleLarge)
        Text(code, style = TextStyle(fontSize = 72.sp, fontWeight = FontWeight.Bold, letterSpacing = 12.sp, color = Color.White))
    } else if (error != null) {
        Text(error, style = MaterialTheme.typography.titleMedium)
        Spacer(Modifier.height(16.dp))
        Button(onClick = onRetry) { Text("Try again") }
    } else {
        Text("Getting a code…", style = MaterialTheme.typography.titleMedium)
    }
}

@Composable
fun PickServerScreen(servers: List<PlexResource>, onPick: (PlexResource) -> Unit) = Centered {
    Text("Choose your Plex server", style = MaterialTheme.typography.headlineMedium)
    Spacer(Modifier.height(24.dp))
    servers.forEachIndexed { i, s ->
        val focus = remember { FocusRequester() }
        Button(onClick = { onPick(s) }, modifier = Modifier.padding(6.dp).width(420.dp).focusRequester(focus)) { Text(s.name) }
        if (i == 0) LaunchedEffect(Unit) { focus.requestFocus() }
    }
}

/** First run: which kind of media server to use. */
@Composable
fun ChooseSourceScreen(onChoose: (String) -> Unit) = Centered {
    Text("Where are your movies?", style = MaterialTheme.typography.displaySmall)
    Spacer(Modifier.height(12.dp))
    Text("ovmplayer plays titles from your media server and adds their menus.", style = MaterialTheme.typography.titleMedium, color = Color.Gray)
    Spacer(Modifier.height(32.dp))
    Row(horizontalArrangement = Arrangement.spacedBy(24.dp)) {
        val focus = remember { FocusRequester() }
        Button(onClick = { onChoose("plex") }, modifier = Modifier.width(240.dp).focusRequester(focus)) { Text("Plex") }
        Button(onClick = { onChoose("jellyfin") }, modifier = Modifier.width(240.dp)) { Text("Jellyfin") }
        LaunchedEffect(Unit) { focus.requestFocus() }
    }
}

/** Jellyfin servers found on the network, or type an address. */
@Composable
fun FindJellyfinScreen(
    servers: List<com.ovmplayer.tv.jellyfin.FoundJellyfin>,
    searching: Boolean,
    error: String?,
    onPick: (String) -> Unit,
    onSearch: () -> Unit,
    onBack: () -> Unit,
) = Centered {
    Text("Choose your Jellyfin server", style = MaterialTheme.typography.headlineMedium)
    Spacer(Modifier.height(16.dp))
    if (error != null) {
        Text(error, style = MaterialTheme.typography.titleMedium, color = Gold)
        Spacer(Modifier.height(12.dp))
    }
    val first = remember { FocusRequester() }
    when {
        searching -> Text("Looking on your network…", style = MaterialTheme.typography.titleMedium)
        servers.isEmpty() -> Text("No Jellyfin server answered on your network. Type its address below.", style = MaterialTheme.typography.titleMedium)
        else -> servers.forEachIndexed { i, s ->
            Button(onClick = { onPick(s.url) }, modifier = Modifier.padding(6.dp).width(520.dp).let { if (i == 0) it.focusRequester(first) else it }) {
                Text("${s.name}  ·  ${s.url.removePrefix("http://")}")
            }
        }
    }
    Spacer(Modifier.height(24.dp))
    var address by remember { mutableStateOf(androidx.compose.ui.text.input.TextFieldValue("")) }
    val focusManager = androidx.compose.ui.platform.LocalFocusManager.current
    val keyboard = androidx.compose.ui.platform.LocalSoftwareKeyboardController.current
    val connect = { if (address.text.isNotBlank()) { keyboard?.hide(); onPick(address.text) } }
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        BasicTextField(
            value = address,
            onValueChange = { address = it },
            singleLine = true,
            textStyle = TextStyle(color = Color.White, fontSize = 20.sp),
            // The on-screen keyboard's ✓ connects straight away.
            keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(imeAction = androidx.compose.ui.text.input.ImeAction.Go),
            keyboardActions = androidx.compose.foundation.text.KeyboardActions(onGo = { connect() }),
            decorationBox = { inner ->
                Box(Modifier.width(360.dp).background(Color(0xFF333333), RoundedCornerShape(6.dp)).padding(10.dp)) {
                    if (address.text.isEmpty()) Text("e.g. 192.168.1.20:8096", color = Color.Gray)
                    inner()
                }
            },
            modifier = (if (servers.isEmpty() && !searching) Modifier.focusRequester(first) else Modifier)
                // On a remote, right/down at the end of the text moves on to the buttons instead of
                // being swallowed by the text cursor.
                .onPreviewKeyEvent { e ->
                    val atEnd = address.selection.collapsed && address.selection.end == address.text.length
                    val key = e.nativeKeyEvent.keyCode
                    if (e.nativeKeyEvent.action == android.view.KeyEvent.ACTION_DOWN && atEnd &&
                        (key == android.view.KeyEvent.KEYCODE_DPAD_RIGHT || key == android.view.KeyEvent.KEYCODE_DPAD_DOWN)
                    ) {
                        focusManager.moveFocus(androidx.compose.ui.focus.FocusDirection.Right)
                    } else {
                        false
                    }
                },
        )
        Button(onClick = { connect() }) { Text("Connect") }
        Button(onClick = onSearch) { Text("Search again") }
        Button(onClick = onBack) { Text("Back") }
    }
    LaunchedEffect(searching, servers.size) { if (!searching) runCatching { first.requestFocus() } }
}

/**
 * Signing in to a Jellyfin server, both ways side by side: a Quick Connect code to approve in another
 * Jellyfin app, or a username and password.
 */
@Composable
fun JellyfinSignInScreen(
    state: com.ovmplayer.tv.Screen.JellyfinSignIn,
    onPassword: (String, String) -> Unit,
    onBack: () -> Unit,
) = Centered {
    Text("Sign in to ${state.server}", style = MaterialTheme.typography.displaySmall)
    Spacer(Modifier.height(32.dp))
    if (state.connecting) {
        Text("Connecting…", style = MaterialTheme.typography.titleMedium)
        return@Centered
    }
    var user by remember { mutableStateOf(androidx.compose.ui.text.input.TextFieldValue("")) }
    var password by remember { mutableStateOf(androidx.compose.ui.text.input.TextFieldValue("")) }
    val userFocus = remember { FocusRequester() }
    val passwordFocus = remember { FocusRequester() }
    val keyboard = androidx.compose.ui.platform.LocalSoftwareKeyboardController.current
    // Submitting closes the keyboard so the result (or an error) shows.
    val submit = { if (!state.busy) { keyboard?.hide(); onPassword(user.text, password.text) } }
    Row(horizontalArrangement = Arrangement.spacedBy(64.dp), verticalAlignment = Alignment.Top) {
        // Username and password.
        Column(Modifier.width(420.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("With your username and password", style = MaterialTheme.typography.titleLarge)
            SignInField(user, { user = it }, "Username", userFocus, password = false, onDone = { runCatching { passwordFocus.requestFocus() }; keyboard?.show() })
            SignInField(password, { password = it }, "Password", passwordFocus, password = true, onDone = submit)
            state.passwordError?.let { Text(it, style = MaterialTheme.typography.titleMedium, color = Gold) }
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Button(onClick = submit) { Text(if (state.busy) "Signing in…" else "Sign in") }
                Button(onClick = onBack) { Text("Back") }
            }
        }
        // Quick Connect.
        Column(Modifier.width(420.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("Or with Quick Connect", style = MaterialTheme.typography.titleLarge)
            when {
                state.code != null -> {
                    Text("In Jellyfin on your phone or computer, open your profile, choose Quick Connect, and enter", style = MaterialTheme.typography.titleMedium, color = Color.Gray)
                    Text(state.code, style = TextStyle(fontSize = 64.sp, fontWeight = FontWeight.Bold, letterSpacing = 10.sp, color = Color.White))
                }
                state.quickConnectNote != null -> Text(state.quickConnectNote, style = MaterialTheme.typography.titleMedium, color = Color.Gray)
                else -> Text("Getting a code…", style = MaterialTheme.typography.titleMedium, color = Color.Gray)
            }
        }
    }
    LaunchedEffect(Unit) {
        runCatching { userFocus.requestFocus() }
        // The keyboard may still be open from typing the server's address; close it once focus has
        // moved here (hiding it before the move leaves it attached to the new field).
        kotlinx.coroutines.delay(150)
        keyboard?.hide()
    }
}

/** A one-line text field for the TV: the on-screen keyboard's action key moves on (or submits). */
@Composable
private fun SignInField(
    value: androidx.compose.ui.text.input.TextFieldValue,
    onChange: (androidx.compose.ui.text.input.TextFieldValue) -> Unit,
    placeholder: String,
    focus: FocusRequester,
    password: Boolean,
    onDone: () -> Unit,
) {
    val focusManager = androidx.compose.ui.platform.LocalFocusManager.current
    val keyboard = androidx.compose.ui.platform.LocalSoftwareKeyboardController.current
    var focused by remember { mutableStateOf(false) }
    BasicTextField(
        value = value,
        onValueChange = onChange,
        singleLine = true,
        textStyle = TextStyle(color = Color.White, fontSize = 20.sp),
        cursorBrush = androidx.compose.ui.graphics.SolidColor(Color.White),
        visualTransformation = if (password) androidx.compose.ui.text.input.PasswordVisualTransformation() else androidx.compose.ui.text.input.VisualTransformation.None,
        keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(
            keyboardType = if (password) androidx.compose.ui.text.input.KeyboardType.Password else androidx.compose.ui.text.input.KeyboardType.Text,
            imeAction = if (password) androidx.compose.ui.text.input.ImeAction.Done else androidx.compose.ui.text.input.ImeAction.Next,
            autoCorrectEnabled = false,
            // The keyboard opens on OK, not on focus, so it doesn't cover the Quick Connect code.
            showKeyboardOnFocus = false,
        ),
        keyboardActions = androidx.compose.foundation.text.KeyboardActions(onNext = { onDone() }, onDone = { onDone() }),
        decorationBox = { inner ->
            Box(
                Modifier.fillMaxWidth()
                    .background(Color(0xFF333333), RoundedCornerShape(6.dp))
                    .border(2.dp, if (focused) Gold else Color.Transparent, RoundedCornerShape(6.dp))
                    .padding(12.dp),
            ) {
                if (value.text.isEmpty()) Text(placeholder, color = Color.Gray)
                inner()
            }
        },
        modifier = Modifier.fillMaxWidth().focusRequester(focus)
            .onFocusChanged { focused = it.isFocused }
            // On a remote: OK opens the keyboard; up and down move between fields instead of being
            // swallowed by the text cursor.
            .onPreviewKeyEvent { e ->
                val key = e.nativeKeyEvent.keyCode
                if (key == android.view.KeyEvent.KEYCODE_DPAD_CENTER || key == android.view.KeyEvent.KEYCODE_ENTER) {
                    if (e.nativeKeyEvent.action == android.view.KeyEvent.ACTION_UP) keyboard?.show()
                    true
                } else if (e.nativeKeyEvent.action == android.view.KeyEvent.ACTION_DOWN &&
                    (key == android.view.KeyEvent.KEYCODE_DPAD_DOWN || key == android.view.KeyEvent.KEYCODE_DPAD_UP)
                ) {
                    focusManager.moveFocus(if (key == android.view.KeyEvent.KEYCODE_DPAD_DOWN) androidx.compose.ui.focus.FocusDirection.Down else androidx.compose.ui.focus.FocusDirection.Up)
                } else {
                    false
                }
            },
    )
}

/** A title's menu, with native playback layered on top when a button plays something. */
@Composable
fun MenuScreen(vm: AppViewModel, open: OpenMenu, onExit: () -> Unit) {
    val context = LocalContext.current
    val assets = vm.assets ?: return onExit()
    var playing by remember { mutableStateOf<PlayRequest?>(null) }
    var playingMsg by remember { mutableStateOf<PageMessage.Play?>(null) }
    var handle by remember { mutableStateOf<PlayerHandle?>(null) }
    var failure by remember { mutableStateOf<String?>(null) }
    /** Level 2: an overlay's buttons show over the playing title (the page is over the video). */
    var overlay by remember { mutableStateOf(false) }
    /** Level 2: the playing title's cue clock (cues, holds, its end), when it has any. */
    var clock by remember { mutableStateOf<com.ovmplayer.tv.player.CueClock?>(null) }
    var holdJob by remember { mutableStateOf<kotlinx.coroutines.Job?>(null) }
    /** Level 2: go on from a held frame (its time ran out, OK, or an overlay button); false when none. */
    fun endHold(): Boolean {
        val c = clock ?: return false
        if (c.holding == null) return false
        holdJob?.cancel()
        if (!c.release()) handle?.player?.play()
        return true
    }
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    lateinit var web: MenuWebView
    web = remember {
        MenuWebView(context, assets) { msg ->
            when (msg) {
                PageMessage.Ready -> web.init(open.entry.url, open.lastStop, open.session, open.kept?.version, open.resume)
                is PageMessage.Session -> vm.rememberSession(open, msg.version, msg.session)
                is PageMessage.Play -> scope.launch {
                    val request = vm.requestFor(open, msg.mediaId, msg.start, msg.audio, msg.subtitle, msg.angle, msg.videoTrack, msg.segments)
                    if (request == null) web.playError("\"${msg.label}\" isn't in your ${vm.sourceKind} library.") else {
                        // A cue may start this while something plays (it returns there afterwards).
                        handle = null
                        overlay = false
                        playingMsg = msg
                        playing = request
                    }
                }
                // A cue opened a menu: playback ends where it is, and the page shows that menu.
                PageMessage.Stop -> {
                    val position = handle?.positionSeconds()
                    if (com.ovmplayer.tv.BuildConfig.DEBUG) android.util.Log.d("ovmplayer", "page: stop at ${position}s")
                    playingMsg?.let { vm.menuTitleFinished(open, it.mediaId, position, ended = false, it.audio, it.subtitle) }
                    playing = null
                    overlay = false
                }
                // Level 2: a seek action moves the title; the cue clock sees the jump as a seek. From a held
                // frame it moves on without the hold's cues.
                is PageMessage.Seek -> {
                    if (clock?.holding != null) {
                        clock?.cancelHold()
                        handle?.player?.play()
                    }
                    handle?.seekTo(msg.position)
                }
                PageMessage.EndHold -> endHold()
                is PageMessage.Overlay -> {
                    if (com.ovmplayer.tv.BuildConfig.DEBUG) android.util.Log.d("ovmplayer", "page: overlay ${msg.showing}")
                    overlay = msg.showing
                }
                PageMessage.Exit -> onExit()
                is PageMessage.Error -> failure = msg.message
            }
        }
    }
    // Remote keys belong to the player while a title plays, never to the menu page behind it, except
    // while an overlay's buttons show: then the page is over the video (see-through) and takes them.
    LaunchedEffect(playing, overlay) {
        val hidden = playing != null && !overlay
        web.setOverVideo(playing != null && overlay)
        web.visibility = if (hidden) android.view.View.INVISIBLE else android.view.View.VISIBLE
        web.setBehindPlayer(playing != null)
    }
    // The overlay's keys are claimed ahead of the player, which keeps focus (and Back) meanwhile. So
    // are the remote's Menu and Guide keys during playback (Level 2): the menus decide what they do.
    DisposableEffect(playing, overlay) {
        if (playing != null) {
            com.ovmplayer.tv.KeyClaim.handler = handler@{ e ->
                val menuKey = when (e.keyCode) {
                    android.view.KeyEvent.KEYCODE_MENU -> "menu"
                    android.view.KeyEvent.KEYCODE_GUIDE -> "title"
                    else -> null
                }
                if (menuKey != null) {
                    if (e.action == android.view.KeyEvent.ACTION_DOWN && e.repeatCount == 0) web.key(menuKey, handle?.positionSeconds())
                    return@handler true
                }
                // Level 2: OK goes on from a frame held until the viewer is ready (the overlay's buttons
                // take it while they show).
                val ok = e.keyCode == android.view.KeyEvent.KEYCODE_DPAD_CENTER || e.keyCode == android.view.KeyEvent.KEYCODE_ENTER
                if (ok && !overlay && clock?.holding != null) {
                    if (e.action == android.view.KeyEvent.ACTION_DOWN && e.repeatCount == 0) endHold()
                    return@handler true
                }
                if (!overlay) return@handler false
                val key = when (e.keyCode) {
                    android.view.KeyEvent.KEYCODE_DPAD_UP -> "up"
                    android.view.KeyEvent.KEYCODE_DPAD_DOWN -> "down"
                    android.view.KeyEvent.KEYCODE_DPAD_LEFT -> "left"
                    android.view.KeyEvent.KEYCODE_DPAD_RIGHT -> "right"
                    android.view.KeyEvent.KEYCODE_DPAD_CENTER, android.view.KeyEvent.KEYCODE_ENTER -> "enter"
                    else -> null
                }
                if (key != null && e.action == android.view.KeyEvent.ACTION_DOWN && e.repeatCount == 0) {
                    if (com.ovmplayer.tv.BuildConfig.DEBUG) android.util.Log.d("ovmplayer", "overlay key $key")
                    web.key(key)
                }
                key != null
            }
        }
        onDispose { com.ovmplayer.tv.KeyClaim.handler = null }
    }
    fun titleEnded() {
        playingMsg?.let { vm.menuTitleFinished(open, it.mediaId, null, ended = true, it.audio, it.subtitle) }
        playing = null
        overlay = false
        web.ended()
        web.requestFocus()
    }
    // Level 2: the page's cues, holds (and an item's end), timed against the player's position.
    LaunchedEffect(playing, handle) {
        clock = null
        val request = playing ?: return@LaunchedEffect
        val msg = playingMsg ?: return@LaunchedEffect
        val h = handle ?: return@LaunchedEffect
        if (msg.cues.isEmpty() && msg.end == null && msg.holds.isEmpty()) return@LaunchedEffect
        fun log(what: String) {
            if (com.ovmplayer.tv.BuildConfig.DEBUG) android.util.Log.d("ovmplayer", "$what at ${h.positionSeconds()}s")
        }
        val c = com.ovmplayer.tv.player.CueClock(
            msg.cues,
            msg.end,
            onCue = { log("cue $it"); web.cue(it) },
            onCueEnd = { log("cueEnd $it"); web.cueEnd(it) },
            onEnd = { log("end"); titleEnded() },
            msg.holds,
            onHold = { hold ->
                // Stop on the held frame, for its time or until the viewer goes on.
                log("hold ${hold.at}")
                h.player.pause()
                h.seekTo(hold.at)
                hold.seconds?.let { seconds ->
                    holdJob = scope.launch {
                        kotlinx.coroutines.delay((seconds * 1000).toLong())
                        endHold()
                    }
                }
            },
        )
        c.start(msg.start)
        clock = c
        while (playing === request) {
            kotlinx.coroutines.delay(200)
            c.tick(h.positionSeconds())
        }
    }
    // Where the viewer is gets saved as they watch: Home leaves the app without a stop.
    fun saveMenuPosition() {
        val msg = playingMsg ?: return
        val position = handle?.positionSeconds() ?: return
        if (playing != null) vm.menuTitleFinished(open, msg.mediaId, position, ended = false, msg.audio, msg.subtitle)
    }
    LaunchedEffect(playing) {
        while (playing != null) {
            kotlinx.coroutines.delay(10_000)
            saveMenuPosition()
        }
    }
    val lifecycle = androidx.lifecycle.compose.LocalLifecycleOwner.current.lifecycle
    DisposableEffect(web, lifecycle) {
        web.open()
        // Home/other apps: pause the page (its menu music and motion stop), resume on return.
        val observer = androidx.lifecycle.LifecycleEventObserver { _, event ->
            when (event) {
                androidx.lifecycle.Lifecycle.Event.ON_PAUSE -> {
                    saveMenuPosition()
                    web.onPause()
                }
                androidx.lifecycle.Lifecycle.Event.ON_RESUME -> web.onResume()
                else -> {}
            }
        }
        lifecycle.addObserver(observer)
        onDispose {
            lifecycle.removeObserver(observer)
            web.destroy()
        }
    }
    BackHandler {
        val p = playing
        if (p != null) {
            val position = handle?.positionSeconds() ?: 0.0
            playingMsg?.let { vm.menuTitleFinished(open, it.mediaId, position, ended = false, it.audio, it.subtitle) }
            playing = null
            overlay = false
            web.stopped(position)
            web.requestFocus()
        } else if (failure != null) {
            onExit() // the page is gone or never loaded: Back leaves the menu
        } else {
            web.back()
        }
    }
    Box(Modifier.fillMaxSize().background(Color.Black)) {
        // The title plays under the page: the page is hidden while it plays, except for overlays.
        playing?.let { request ->
            VideoPlayer(
                request = request,
                onReady = { handle = it },
                // A still at the very end (or one not reached yet) holds before the title ends.
                onEnded = { if (clock?.finish() != false) titleEnded() },
                onError = { message ->
                    playing = null
                    overlay = false
                    web.playError(message)
                    web.requestFocus()
                },
                modifier = Modifier.fillMaxSize(),
            )
        }
        AndroidView(factory = { web.also { it.requestFocus() } }, modifier = Modifier.fillMaxSize())
        failure?.let { message ->
            val main = open.entry.media.firstOrNull { it.kind == "feature" && open.media.containsKey(it.id) }
                ?: open.entry.media.firstOrNull { open.media.containsKey(it.id) }
            Box(Modifier.fillMaxSize().background(Color.Black)) {
                Centered {
                    Text("This menu couldn't be shown", style = MaterialTheme.typography.headlineSmall)
                    Spacer(Modifier.height(8.dp))
                    // The page reports raw URLs; viewers get plain words (debug builds keep the detail).
                    val loadFailed = message.startsWith("Couldn't load")
                    val shown = if (loadFailed) "The menu file couldn't be loaded. The menu server may be off or unreachable." else message
                    Text(shown, style = MaterialTheme.typography.bodyLarge, color = Color.Gray)
                    if (com.ovmplayer.tv.BuildConfig.DEBUG && shown != message) Text(message, style = MaterialTheme.typography.bodySmall, color = Color.DarkGray)
                    Spacer(Modifier.height(24.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                        val focus = remember { FocusRequester() }
                        if (main != null) {
                            Button(onClick = {
                                scope.launch {
                                    failure = null
                                    vm.requestFor(open, main.id, 0.0)?.let { playing = it }
                                }
                            }, modifier = Modifier.focusRequester(focus)) { Text("Play without menu") }
                        }
                        Button(onClick = onExit, modifier = if (main == null) Modifier.focusRequester(focus) else Modifier) { Text("Back") }
                        LaunchedEffect(Unit) { runCatching { focus.requestFocus() } }
                    }
                }
            }
        }
    }
}

/** Direct playback of a title without a menu. */
@Composable
fun PlayScreen(request: PlayRequest, onProgress: (Double) -> Unit, onDone: (position: Double?, ended: Boolean) -> Unit) {
    var error by remember { mutableStateOf<String?>(null) }
    var handle by remember { mutableStateOf<com.ovmplayer.tv.player.PlayerHandle?>(null) }
    BackHandler { onDone(handle?.positionSeconds(), false) }
    // Where the viewer is gets saved as they watch: Home leaves the app without a stop.
    LaunchedEffect(Unit) {
        while (true) {
            kotlinx.coroutines.delay(10_000)
            handle?.positionSeconds()?.let(onProgress)
        }
    }
    val lifecycle = androidx.lifecycle.compose.LocalLifecycleOwner.current.lifecycle
    DisposableEffect(lifecycle) {
        val observer = androidx.lifecycle.LifecycleEventObserver { _, event ->
            if (event == androidx.lifecycle.Lifecycle.Event.ON_PAUSE) handle?.positionSeconds()?.let(onProgress)
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer) }
    }
    Box(Modifier.fillMaxSize().background(Color.Black)) {
        if (error == null) VideoPlayer(request, onReady = { handle = it }, onEnded = { onDone(null, true) }, onError = { error = it }, modifier = Modifier.fillMaxSize())
        else Centered { Text(error!!, style = MaterialTheme.typography.headlineSmall) }
    }
}

/** Shown on Android 17+ until the viewer allows access to the home network. */
@Composable
fun NetworkPermissionScreen(onAllow: () -> Unit) = Centered {
    Text("Allow access to your home network", style = MaterialTheme.typography.headlineMedium)
    Spacer(Modifier.height(16.dp))
    Text(
        "ovmplayer needs it to reach your Plex or Jellyfin server and the menu server on your network. It connects to nothing else there.",
        style = MaterialTheme.typography.titleMedium,
    )
    Spacer(Modifier.height(24.dp))
    val focus = remember { FocusRequester() }
    Button(onClick = onAllow, modifier = Modifier.focusRequester(focus)) { Text("Allow") }
    LaunchedEffect(Unit) { focus.requestFocus() }
}
