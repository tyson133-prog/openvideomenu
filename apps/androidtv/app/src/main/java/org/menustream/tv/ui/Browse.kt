package org.menustream.tv.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.tv.material3.Button
import androidx.tv.material3.Card
import androidx.tv.material3.ListItem
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Tab
import androidx.tv.material3.TabRow
import androidx.tv.material3.Text
import coil3.compose.AsyncImage
import coil3.network.NetworkHeaders
import coil3.network.httpHeaders
import coil3.request.ImageRequest
import org.menustream.tv.AppViewModel
import org.menustream.tv.BrowseTab
import org.menustream.tv.ShowState
import org.menustream.tv.Tile
import org.menustream.tv.TileRow

private val Gold = Color(0xFFFFCF6A)
private val PosterWidth = 132.dp

@Composable
fun BrowseScreen(vm: AppViewModel) {
    val tabs by vm.tabs.collectAsStateWithLifecycle()
    val rows by vm.rows.collectAsStateWithLifecycle()
    val letters by vm.letters.collectAsStateWithLifecycle()
    val source by vm.menuSource.collectAsStateWithLifecycle()
    var selected by rememberSaveable { mutableIntStateOf(0) }
    var editingServer by remember { mutableStateOf(false) }
    val tab = tabs.getOrNull(selected)
    LaunchedEffect(tab) { tab?.let { vm.loadTab(it) } }
    // Start with the first poster focused, so the first remote press already moves around; coming
    // back from a title or its menu, focus the tile it was opened from instead.
    val firstTile = remember { androidx.compose.ui.focus.FocusRequester() }
    val returnTile = remember { androidx.compose.ui.focus.FocusRequester() }
    var focusedOnce by rememberSaveable { mutableStateOf(false) }
    val firstRowReady = rows[BrowseTab.Discs.id]?.isNotEmpty() == true
    LaunchedEffect(firstRowReady) {
        if (firstRowReady && !focusedOnce && tab == BrowseTab.Discs) {
            kotlinx.coroutines.delay(100)
            val back = vm.returnFocusKey?.let { key -> rows[BrowseTab.Discs.id]?.any { r -> r.tiles.any { it.key == key } } } == true
            focusedOnce = (back && runCatching { returnTile.requestFocus() }.isSuccess) ||
                runCatching { firstTile.requestFocus() }.isSuccess
        }
    }

    Column(Modifier.fillMaxSize().padding(start = 40.dp, end = 40.dp, top = 20.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            TabRow(selectedTabIndex = selected, modifier = Modifier.weight(1f)) {
                tabs.forEachIndexed { i, t ->
                    Tab(selected = i == selected, onFocus = { selected = i }) {
                        Text(t.title, modifier = Modifier.padding(horizontal = 14.dp, vertical = 6.dp))
                    }
                }
            }
            Button(onClick = { editingServer = !editingServer }) { Text(if (editingServer) "Close" else "Menu server") }
            Spacer(Modifier.width(8.dp))
            Button(onClick = { vm.signOut() }) { Text("Switch server") }
        }
        Text(source.status, style = MaterialTheme.typography.bodySmall, color = Color.Gray, modifier = Modifier.padding(top = 6.dp, bottom = 4.dp))
        if (editingServer) AssetServerEditor(source.url) { editingServer = false; vm.useAssetServer(it) }

        when (tab) {
            is BrowseTab.Search -> SearchTab(vm)
            null -> {}
            else -> {
                val tabRows = rows[tab.id]
                val letterState = letters[tab.id]
                LazyColumn(contentPadding = PaddingValues(bottom = 48.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    if (tabRows == null) item { Text("Loading…", modifier = Modifier.padding(16.dp)) }
                    items(tabRows ?: emptyList(), key = { "row:" + it.title }) { row ->
                        TileRowView(vm, row, if (row == tabRows?.firstOrNull()) firstTile else null, returnTile)
                    }
                    if (tab is BrowseTab.Discs && tabRows != null && tabRows.none { it.title == "Disc Menus" }) {
                        item { Text("No disc menus yet. Put a .menustream.json manifest next to a title.", modifier = Modifier.padding(16.dp)) }
                    }
                    if (tab is BrowseTab.Libraries && letterState != null) {
                        item(key = "letters") {
                            Column {
                                Text("Browse A–Z", style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(vertical = 8.dp))
                                LazyRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                                    items(letterState.letters, key = { it.key }) { letter ->
                                        val isSelected = letter.key == letterState.selected?.key
                                        Button(onClick = { vm.selectLetter(tab, letter) }, contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp)) {
                                            Text(letter.title, fontWeight = if (isSelected) FontWeight.Bold else FontWeight.Normal, color = if (isSelected) Gold else Color.Unspecified)
                                        }
                                    }
                                }
                            }
                        }
                        items(letterState.tiles.chunked(7), key = { "grid:" + it.first().key }) { chunk ->
                            Row(horizontalArrangement = Arrangement.spacedBy(16.dp), modifier = Modifier.padding(vertical = 6.dp)) {
                                chunk.forEach { TileCard(vm, it) }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
fun TileRowView(
    vm: AppViewModel,
    row: TileRow,
    firstFocus: androidx.compose.ui.focus.FocusRequester? = null,
    returnFocus: androidx.compose.ui.focus.FocusRequester? = null,
) {
    Column {
        Text(row.title, style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(vertical = 8.dp))
        LazyRow(horizontalArrangement = Arrangement.spacedBy(16.dp), contentPadding = PaddingValues(end = 32.dp)) {
            items(row.tiles, key = { it.key }) { tile ->
                var m: Modifier = Modifier
                if (firstFocus != null && tile == row.tiles.first()) m = m.focusRequester(firstFocus)
                if (returnFocus != null && tile.key == vm.returnFocusKey) m = m.focusRequester(returnFocus)
                TileCard(vm, tile, m)
            }
        }
    }
}

@Composable
fun TileCard(vm: AppViewModel, tile: Tile, modifier: Modifier = Modifier) {
    val (title, subtitle, thumb, hasMenu) = when (tile) {
        is Tile.Media -> {
            val i = tile.item
            val isEpisode = i.type == org.menustream.tv.media.ItemType.EPISODE
            Quad(
                if (isEpisode) i.showTitle ?: i.title else i.title,
                if (isEpisode) "S${i.seasonNumber ?: 0} · E${i.episodeNumber ?: 0}  ${i.title}" else i.year?.toString() ?: "",
                if (isEpisode) i.showImage ?: i.image else i.image,
                tile.menu != null,
            )
        }
        is Tile.Disc -> Quad(
            tile.entry.title?.name ?: "Disc",
            if (tile.pending) tile.entry.title?.year?.toString() ?: "" else discLabel(tile),
            tile.show?.image,
            true,
        )
    }
    Column(Modifier.width(PosterWidth)) {
        Card(onClick = { vm.open(tile) }, modifier = modifier) {
            Box(Modifier.width(PosterWidth).height(PosterWidth * 1.5f).background(Color(0xFF222222))) {
                val url = vm.imageUrl(thumb, 264, 396)
                if (url != null) {
                    AsyncImage(
                        model = ImageRequest.Builder(LocalContext.current).data(url)
                            .httpHeaders(NetworkHeaders.Builder().apply { vm.imageHeaders().forEach { (k, v) -> set(k, v) } }.build())
                            .build(),
                        contentDescription = title,
                        contentScale = ContentScale.Crop,
                        modifier = Modifier.fillMaxSize(),
                    )
                } else {
                    Text(title, modifier = Modifier.align(Alignment.Center).padding(8.dp))
                }
                if (hasMenu) MenuBadge(Modifier.align(Alignment.TopEnd).padding(5.dp))
            }
        }
        Text(title, style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 4.dp))
        if (subtitle.isNotEmpty()) Text(subtitle, style = MaterialTheme.typography.labelSmall, color = Color.Gray, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

private data class Quad(val title: String, val subtitle: String, val thumb: String?, val hasMenu: Boolean)

/** "Season 01" etc. from where the disc's manifest sits, e.g. Show/Season 01/Disc 2.menustream.json */
private fun discLabel(disc: Tile.Disc): String {
    val parts = disc.entry.path.split('/')
    val file = parts.last().removeSuffix(".menustream.json")
    val folder = parts.getOrNull(parts.size - 2) ?: ""
    return if (file.equals(disc.entry.title?.name, ignoreCase = true)) folder else file
}

@Composable
fun MenuBadge(modifier: Modifier = Modifier) = Text(
    "DISC MENU",
    style = MaterialTheme.typography.labelSmall.copy(color = Color.Black, fontWeight = FontWeight.Bold, fontSize = 9.sp),
    modifier = modifier.background(Gold, RoundedCornerShape(4.dp)).padding(horizontal = 5.dp, vertical = 1.dp),
)

@Composable
private fun SearchTab(vm: AppViewModel) {
    val results by vm.searchResults.collectAsStateWithLifecycle()
    var query by rememberSaveable { mutableStateOf("") }
    Column {
        BasicTextField(
            value = query,
            onValueChange = { query = it },
            singleLine = true,
            textStyle = TextStyle(color = Color.White, fontSize = 22.sp),
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
            keyboardActions = KeyboardActions(onSearch = { vm.search(query) }),
            decorationBox = { inner ->
                Box(Modifier.width(520.dp).background(Color(0xFF333333), RoundedCornerShape(6.dp)).padding(12.dp)) {
                    if (query.isEmpty()) Text("Search titles…", color = Color.Gray)
                    inner()
                }
            },
            modifier = Modifier.padding(vertical = 12.dp),
        )
        LazyColumn {
            items(results.chunked(7), key = { "s:" + it.first().key }) { chunk ->
                Row(horizontalArrangement = Arrangement.spacedBy(16.dp), modifier = Modifier.padding(vertical = 6.dp)) {
                    chunk.forEach { TileCard(vm, it) }
                }
            }
        }
    }
}

@Composable
fun AssetServerEditor(current: String?, onDone: (String?) -> Unit) {
    var text by remember { mutableStateOf(current ?: "") }
    Row(Modifier.padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Menu server address:")
        BasicTextField(
            value = text,
            onValueChange = { text = it },
            singleLine = true,
            textStyle = TextStyle(color = Color.White, fontSize = 20.sp),
            modifier = Modifier.width(360.dp).background(Color(0xFF333333), RoundedCornerShape(6.dp)).padding(10.dp),
        )
        Button(onClick = { onDone(text.ifBlank { null }) }) { Text("Use") }
        Button(onClick = { onDone(null) }) { Text("Find automatically") }
    }
}

/** A show: its disc menus, then seasons and episodes. */
@Composable
fun ShowScreen(vm: AppViewModel, state: ShowState) {
    Column(Modifier.fillMaxSize().padding(horizontal = 48.dp, vertical = 24.dp)) {
        Text(state.show.title, style = MaterialTheme.typography.headlineMedium)
        state.show.year?.let { Text("$it", color = Color.Gray) }
        LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp), contentPadding = PaddingValues(bottom = 48.dp)) {
            if (state.discs.isNotEmpty()) item(key = "discs") { TileRowView(vm, TileRow("Discs", state.discs)) }
            item(key = "seasons") {
                Column {
                    Text("Seasons", style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(vertical = 8.dp))
                    LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        items(state.seasons, key = { it.id }) { season ->
                            val isSelected = season.id == state.season?.id
                            Button(onClick = { vm.selectSeason(season) }) {
                                Text(season.title, fontWeight = if (isSelected) FontWeight.Bold else FontWeight.Normal, color = if (isSelected) Gold else Color.Unspecified)
                            }
                        }
                    }
                }
            }
            items(state.episodes, key = { it.id }) { ep ->
                ListItem(
                    selected = false,
                    onClick = { vm.play(ep, ep.resumeSeconds) },
                    headlineContent = { Text("${ep.episodeNumber ?: ""}. ${ep.title}") },
                    supportingContent = {
                        val minutes = ((ep.durationSeconds ?: 0.0) / 60).toInt()
                        val watched = if (ep.watched) " · watched" else if (ep.resumeSeconds > 0) " · in progress" else ""
                        Text("$minutes min$watched", color = Color.Gray)
                    },
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        }
    }
}

@Composable
fun Spacer8() = Spacer(Modifier.size(8.dp))
