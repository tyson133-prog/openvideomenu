package org.menustream.tv.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
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
import org.menustream.tv.AppViewModel
import org.menustream.tv.OpenMenu
import org.menustream.tv.menu.MenuWebView
import org.menustream.tv.menu.PageMessage
import org.menustream.tv.player.PlayRequest
import org.menustream.tv.player.PlayerHandle
import org.menustream.tv.player.VideoPlayer
import org.menustream.tv.plex.PlexResource

private val Gold = Color(0xFFFFCF6A)

@Composable
fun Centered(content: @Composable () -> Unit) =
    Column(Modifier.fillMaxSize().padding(64.dp), verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
        content()
    }

@Composable
fun BusyScreen(message: String) = Centered { Text(message, style = MaterialTheme.typography.headlineSmall) }

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

/** A title's disc menu, with native playback layered on top when a button plays something. */
@Composable
fun MenuScreen(vm: AppViewModel, open: OpenMenu, onExit: () -> Unit) {
    val context = LocalContext.current
    val assets = vm.assets ?: return onExit()
    var playing by remember { mutableStateOf<PlayRequest?>(null) }
    var handle by remember { mutableStateOf<PlayerHandle?>(null) }
    var failure by remember { mutableStateOf<String?>(null) }
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    lateinit var web: MenuWebView
    web = remember {
        MenuWebView(context, assets) { msg ->
            when (msg) {
                PageMessage.Ready -> web.init(open.entry.url, open.lastStop)
                is PageMessage.Play -> scope.launch {
                    val request = vm.requestFor(open, msg.mediaId, msg.start)
                    if (request == null) web.playError("\"${msg.label}\" isn't in your Plex library.") else playing = request
                }
                PageMessage.Exit -> onExit()
                is PageMessage.Error -> failure = msg.message
            }
        }
    }
    val lifecycle = androidx.lifecycle.compose.LocalLifecycleOwner.current.lifecycle
    DisposableEffect(web, lifecycle) {
        web.open()
        // Home/other apps: pause the page (its menu music and motion stop), resume on return.
        val observer = androidx.lifecycle.LifecycleEventObserver { _, event ->
            when (event) {
                androidx.lifecycle.Lifecycle.Event.ON_PAUSE -> web.onPause()
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
            playing = null
            web.stopped(position)
            web.requestFocus()
        } else if (failure != null) {
            onExit() // the page is gone or never loaded: Back leaves the menu
        } else {
            web.back()
        }
    }
    Box(Modifier.fillMaxSize().background(Color.Black)) {
        AndroidView(factory = { web.also { it.requestFocus() } }, modifier = Modifier.fillMaxSize())
        playing?.let { request ->
            VideoPlayer(
                request = request,
                onReady = { handle = it },
                onEnded = {
                    playing = null
                    web.ended()
                    web.requestFocus()
                },
                onError = { message ->
                    playing = null
                    web.playError(message)
                    web.requestFocus()
                },
                modifier = Modifier.fillMaxSize(),
            )
        }
        failure?.let { message ->
            val main = open.entry.media.firstOrNull { it.kind == "feature" && open.media.containsKey(it.id) }
                ?: open.entry.media.firstOrNull { open.media.containsKey(it.id) }
            Box(Modifier.fillMaxSize().background(Color.Black)) {
                Centered {
                    Text("This disc menu couldn't be shown", style = MaterialTheme.typography.headlineSmall)
                    Spacer(Modifier.height(8.dp))
                    // The page reports raw URLs; viewers get plain words (debug builds keep the detail).
                    val loadFailed = message.startsWith("Couldn't load")
                    val shown = if (loadFailed) "The menu file couldn't be loaded. The menu server may be off or unreachable." else message
                    Text(shown, style = MaterialTheme.typography.bodyLarge, color = Color.Gray)
                    if (org.menustream.tv.BuildConfig.DEBUG && shown != message) Text(message, style = MaterialTheme.typography.bodySmall, color = Color.DarkGray)
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
fun PlayScreen(request: PlayRequest, onDone: () -> Unit) {
    var error by remember { mutableStateOf<String?>(null) }
    BackHandler { onDone() }
    Box(Modifier.fillMaxSize().background(Color.Black)) {
        if (error == null) VideoPlayer(request, onReady = {}, onEnded = onDone, onError = { error = it }, modifier = Modifier.fillMaxSize())
        else Centered { Text(error!!, style = MaterialTheme.typography.headlineSmall) }
    }
}

/** Shown on Android 17+ until the viewer allows access to the home network. */
@Composable
fun NetworkPermissionScreen(onAllow: () -> Unit) = Centered {
    Text("Allow access to your home network", style = MaterialTheme.typography.headlineMedium)
    Spacer(Modifier.height(16.dp))
    Text(
        "MenuStream needs it to reach your Plex server and the menu server on your network. It connects to nothing else there.",
        style = MaterialTheme.typography.titleMedium,
    )
    Spacer(Modifier.height(24.dp))
    val focus = remember { FocusRequester() }
    Button(onClick = onAllow, modifier = Modifier.focusRequester(focus)) { Text("Allow") }
    LaunchedEffect(Unit) { focus.requestFocus() }
}
