package org.menustream.tv.menu

import org.menustream.tv.assets.CatalogEntry

/**
 * Matches file paths across two machines that see the same folders differently, e.g. Plex sees
 * "/data/movies/Sintel (2010)/Trailers/Sintel Trailer.mp4" while the asset server sees
 * "sintel/Trailers/Sintel Trailer.mp4" under its media root. Paths are compared by their trailing
 * segments (case-insensitively, either slash style). A match needs at least the file name plus its
 * folder, so two different films' "Trailer.mp4" never match each other.
 */
object MenuMatcher {
    private const val MIN_SEGMENTS = 2

    fun segments(path: String): List<String> = path.replace('\\', '/').split('/').filter { it.isNotEmpty() }.map { it.lowercase() }

    /** Number of equal trailing path segments. */
    fun suffixScore(a: String, b: String): Int {
        val x = segments(a)
        val y = segments(b)
        var n = 0
        while (n < x.size && n < y.size && x[x.size - 1 - n] == y[y.size - 1 - n]) n++
        return n
    }

    fun matches(a: String, b: String) = suffixScore(a, b) >= MIN_SEGMENTS

    /**
     * The manifest whose media includes [file] (a Plex part path), preferring the longest suffix match.
     * Ties between manifests mean the folders are ambiguous (e.g. two libraries with the same folder
     * names); the first is used and a manual mapping would be needed to choose.
     */
    fun manifestFor(file: String, entries: List<CatalogEntry>): CatalogEntry? =
        entries.filter { it.valid && it.hasMenus }
            .mapNotNull { e -> e.media.mapNotNull { m -> m.path?.let { suffixScore(it, file) } }.maxOrNull()?.let { e to it } }
            .filter { it.second >= MIN_SEGMENTS }
            .maxByOrNull { it.second }
            ?.first

    /** For each manifest media id, the candidate file (from [files]) it refers to, if any. */
    fun <T> resolveMedia(entry: CatalogEntry, files: List<Pair<String, T>>): Map<String, T> =
        entry.media.mapNotNull { m ->
            val path = m.path ?: return@mapNotNull null
            files.map { (file, value) -> suffixScore(path, file) to value }
                .filter { it.first >= MIN_SEGMENTS }
                .maxByOrNull { it.first }
                ?.let { m.id to it.second }
        }.toMap()
}
