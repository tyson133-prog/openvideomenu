package org.menustream.tv

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.viewModels
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.darkColorScheme
import org.menustream.tv.ui.BusyScreen
import org.menustream.tv.ui.FailedScreen
import org.menustream.tv.ui.LibraryScreen
import org.menustream.tv.ui.MenuScreen
import org.menustream.tv.ui.PickServerScreen
import org.menustream.tv.ui.PlayScreen
import org.menustream.tv.ui.SignInScreen

class MainActivity : ComponentActivity() {
    private val vm: AppViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent { App(vm) }
        handleDebugIntent()
    }

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleDebugIntent()
    }

    /** Debug builds: `--es debugAssetServer <url> --es debugManifest <name>` opens a menu without Plex. */
    private fun handleDebugIntent() {
        if (!BuildConfig.DEBUG) return
        val server = intent?.getStringExtra("debugAssetServer") ?: return
        vm.debugOpen(server, intent.getStringExtra("debugManifest") ?: "")
    }
}

@Composable
fun App(vm: AppViewModel) {
    val screen by vm.screen.collectAsStateWithLifecycle()
    val source by vm.menuSource.collectAsStateWithLifecycle()
    MaterialTheme(colorScheme = darkColorScheme()) {
        Surface(modifier = Modifier.fillMaxSize()) {
            when (val s = screen) {
                is Screen.Busy -> BusyScreen(s.message)
                is Screen.SignIn -> SignInScreen(s.code, s.error, vm::signIn)
                is Screen.PickServer -> PickServerScreen(s.servers) { vm.connect(it) }
                is Screen.Library -> LibraryScreen(
                    rows = s.movies,
                    source = source,
                    posterUrl = { row -> row.item.thumb?.let { vm.server?.imageUrl(it, 300, 450) } },
                    imageHeaders = vm.server?.authHeaders() ?: emptyMap(),
                    onOpen = { vm.open(it) },
                    onSetServer = { vm.useAssetServer(it) },
                )
                is Screen.Menu -> MenuScreen(vm, s.open, onExit = vm::backToLibrary)
                is Screen.Play -> PlayScreen(s.request, onDone = vm::backToLibrary)
                is Screen.Failed -> FailedScreen(s.message, onRetry = { vm.start() }, onSignOut = vm::signOut)
            }
        }
    }
}
