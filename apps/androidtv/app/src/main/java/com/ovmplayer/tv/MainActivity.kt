package com.ovmplayer.tv

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.viewModels
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.darkColorScheme
import com.ovmplayer.tv.ui.BusyScreen
import com.ovmplayer.tv.ui.FailedScreen
import com.ovmplayer.tv.ui.BrowseScreen
import com.ovmplayer.tv.ui.ShowScreen
import com.ovmplayer.tv.ui.MenuScreen
import com.ovmplayer.tv.ui.PickServerScreen
import com.ovmplayer.tv.ui.PlayScreen
import com.ovmplayer.tv.ui.SignInScreen

class MainActivity : ComponentActivity() {
    private val vm: AppViewModel by viewModels()

    /** null = no need to ask; false = asking or refused (see [LocalNetwork]). */
    private var networkAllowed by androidx.compose.runtime.mutableStateOf<Boolean?>(null)
    private val askNetwork = registerForActivityResult(androidx.activity.result.contract.ActivityResultContracts.RequestPermission()) { granted ->
        networkAllowed = granted
        if (granted) vm.begin()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            when (networkAllowed) {
                false -> com.ovmplayer.tv.ui.NetworkPermissionScreen(onAllow = ::requestNetwork)
                else -> App(vm)
            }
        }
        if (LocalNetwork.mustAsk(this)) {
            networkAllowed = false
            requestNetwork()
        } else {
            vm.begin()
        }
        handleDebugIntent()
    }

    private var askedNetwork = false

    /**
     * Ask for the permission; after the viewer has refused twice Android stops showing the prompt, so
     * send them to the app's settings page instead of a button that does nothing.
     */
    private fun requestNetwork() {
        if (askedNetwork && !shouldShowRequestPermissionRationale(LocalNetwork.PERMISSION)) {
            startActivity(
                android.content.Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS)
                    .setData(android.net.Uri.fromParts("package", packageName, null)),
            )
            return
        }
        askedNetwork = true
        askNetwork.launch(LocalNetwork.PERMISSION)
    }

    override fun onResume() {
        super.onResume()
        // Back from Settings with the permission now granted.
        if (networkAllowed == false && !LocalNetwork.mustAsk(this)) {
            networkAllowed = true
            vm.begin()
        }
    }

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleDebugIntent()
    }

    /** Debug builds: `--es debugAssetServer <url> --es debugManifest <name>` opens a menu without Plex. */
    private fun handleDebugIntent() {
        if (!BuildConfig.DEBUG) return
        intent?.getStringExtra("debugShow")?.let { vm.debugOpenShow(it); return }
        val server = intent?.getStringExtra("debugAssetServer") ?: return
        vm.debugOpen(server, intent.getStringExtra("debugManifest") ?: "")
    }
}

@Composable
fun App(vm: AppViewModel) {
    val screen by vm.screen.collectAsStateWithLifecycle()
    MaterialTheme(colorScheme = darkColorScheme()) {
        Surface(modifier = Modifier.fillMaxSize()) {
            when (val s = screen) {
                is Screen.Busy -> BusyScreen(s.message)
                Screen.ChooseSource -> com.ovmplayer.tv.ui.ChooseSourceScreen(vm::choose)
                is Screen.SignIn -> {
                    androidx.activity.compose.BackHandler { vm.signOut() }
                    SignInScreen(s.code, s.error, vm::signIn)
                }
                is Screen.FindJellyfin -> com.ovmplayer.tv.ui.FindJellyfinScreen(
                    s.servers, s.searching, s.error,
                    onPick = { vm.useJellyfin(it) },
                    onSearch = { vm.findJellyfin() },
                    onBack = vm::signOut,
                )
                is Screen.QuickConnect -> {
                    androidx.activity.compose.BackHandler { vm.findJellyfin() }
                    com.ovmplayer.tv.ui.QuickConnectScreen(s.server, s.code, s.error, onBack = { vm.findJellyfin() })
                }
                is Screen.PickServer -> PickServerScreen(s.servers) { vm.connect(it) }
                Screen.Browse -> BrowseScreen(vm)
                is Screen.Show -> {
                    androidx.activity.compose.BackHandler { vm.back() }
                    ShowScreen(vm, s.state)
                }
                is Screen.Menu -> MenuScreen(vm, s.open, onExit = vm::back)
                is Screen.ResumeAsk -> {
                    androidx.activity.compose.BackHandler { vm.back() }
                    com.ovmplayer.tv.ui.ResumeScreen(s.open.title, s.resumes, s.justTheFilm, onChoose = vm::resumeChosen)
                }
                is Screen.Play -> PlayScreen(s.request, onProgress = vm::directProgress, onDone = vm::playFinished)
                is Screen.Failed -> FailedScreen(s.message, onRetry = { vm.start() }, onSignOut = vm::signOut)
            }
        }
    }
}
