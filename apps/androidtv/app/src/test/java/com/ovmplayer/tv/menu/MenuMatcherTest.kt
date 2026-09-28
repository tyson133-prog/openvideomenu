package com.ovmplayer.tv.menu

import kotlinx.serialization.json.JsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import com.ovmplayer.tv.assets.CatalogEntry
import com.ovmplayer.tv.assets.CatalogMedia

class MenuMatcherTest {
    private fun entry(id: String, vararg paths: Pair<String, String>) = CatalogEntry(
        id = id, root = 0, path = "$id.openvideomenu.json", url = "/v1/files/0/$id", valid = true, hasMenus = true,
        media = paths.map { (mid, p) -> CatalogMedia(mid, ref = JsonPrimitive(p), path = p) },
    )

    private val sintel = entry(
        "sintel",
        "feature" to "sintel/Sintel (2010).mp4",
        "trailer" to "sintel/Trailers/Sintel Trailer.mp4",
    )
    private val other = entry("other", "feature" to "Other Film (2001)/Other Film (2001).mkv", "trailer" to "Other Film (2001)/Trailers/Trailer.mp4")

    @Test fun matchesAcrossDifferentRootsAndSlashes() {
        assertEquals(sintel, MenuMatcher.manifestFor("/data/movies/sintel/Sintel (2010).mp4", listOf(other, sintel)))
        assertEquals(sintel, MenuMatcher.manifestFor("""D:\Movies\Sintel\Sintel (2010).mp4""", listOf(other, sintel)))
    }

    @Test fun fileNameAloneIsNotEnough() {
        assertNull(MenuMatcher.manifestFor("/data/movies/Somewhere Else/Sintel (2010).mp4", listOf(sintel)))
        assertNull(MenuMatcher.manifestFor("/x/Unrelated/Trailers/Trailer.mp4", listOf(sintel)))
    }

    @Test fun resolvesEveryMediaIdToItsFile() {
        val files = listOf(
            "/data/movies/sintel/Sintel (2010).mp4" to "part-1",
            "/data/movies/sintel/Trailers/Sintel Trailer.mp4" to "part-2",
            "/data/movies/Other Film (2001)/Trailers/Trailer.mp4" to "part-9",
        )
        assertEquals(mapOf("feature" to "part-1", "trailer" to "part-2"), MenuMatcher.resolveMedia(sintel, files))
    }

    @Test fun indexAgreesWithTheMatcher() {
        val entries = listOf(other, sintel)
        val index = MenuIndex(entries)
        listOf(
            "/data/movies/sintel/Sintel (2010).mp4",
            """D:\Movies\Sintel\Sintel (2010).mp4""",
            "/data/movies/Somewhere Else/Sintel (2010).mp4",
            "/x/Unrelated/Trailers/Trailer.mp4",
            "/x/Other Film (2001)/Trailers/Trailer.mp4",
            "Trailer.mp4",
        ).forEach { f -> assertEquals(f, MenuMatcher.manifestFor(f, entries), index.manifestFor(f)) }
        assertEquals(listOf(sintel), index.manifestsFor("/m/sintel/Trailers/Sintel Trailer.mp4"))
    }
}
