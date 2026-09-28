package com.ovmplayer.tv.menu

import com.ovmplayer.tv.assets.CatalogEntry

/**
 * [MenuMatcher.manifestFor] over a whole catalog without comparing every file against every
 * manifest: media paths are grouped by their last two segments (which every match must share), so a
 * lookup only scores the few manifests in that group. Built once per catalog; libraries with
 * hundreds of menus badge each poster in constant time.
 */
class MenuIndex(entries: List<CatalogEntry>) {
    private val byTail: Map<String, List<Pair<CatalogEntry, String>>> =
        entries.filter { it.valid && it.hasMenus }
            .flatMap { e -> e.media.mapNotNull { m -> m.path?.let { e to it } } }
            .groupBy { (_, path) -> MenuMatcher.tailKey(path) ?: "" }
            .filterKeys { it.isNotEmpty() }

    val isEmpty get() = byTail.isEmpty()

    /** The manifest whose media includes [file] (a media-server path), preferring the longest match. */
    fun manifestFor(file: String): CatalogEntry? =
        candidates(file).maxByOrNull { it.second }?.first

    /** Every manifest with a media path matching [file]. */
    fun manifestsFor(file: String): List<CatalogEntry> = candidates(file).map { it.first }.distinct()

    private fun candidates(file: String): List<Pair<CatalogEntry, Int>> {
        val key = MenuMatcher.tailKey(file) ?: return emptyList()
        return byTail[key].orEmpty().map { (e, path) -> e to MenuMatcher.suffixScore(path, file) }
    }
}
