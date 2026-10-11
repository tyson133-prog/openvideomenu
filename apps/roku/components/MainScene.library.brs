' The library beyond menus: a row per movie or TV library on the home screen, a screen per
' show (its menus, then a row per season), and titles without a menu, which simply play (from
' where the viewer left off). Plex and Jellyfin items are kept as the server sent them; the small
' accessors below read either.
'
' Loading never blocks the screen (as in the Android app): the rows appear at once and fill page by
' page. Movie pages are also where movie menus find their film. TV menus look for their show by
' title once the movie libraries are done, and a show's episodes load when it's first needed.

function pageSize() as integer
    return 300
end function

sub resetLibraries()
    m.found = {}          ' manifest id -> the movie it belongs to
    m.shows = {}          ' manifest id -> an episode of the show it belongs to (TV menus)
    m.manifestShow = {}       ' manifest id -> that series
    m.menuOf = {}         ' movie key -> manifest id, for movies whose files have a menu
    m.episodesByShow = {} ' show key -> its episodes, once loaded
    m.libraries = []      ' [{ name, kind: "movie" | "show", key, row, done }] in the server's order
    m.tileItems = []      ' what each tile stands for (tile.itemRef indexes this)
    m.manifestTiles = {}      ' manifest id -> its tile in the Menus row
    m.showTiles = {}      ' show key -> its tile in a library row
    m.tvLookups = -1      ' TV menu lookups still running; -1 = not started
end sub

sub addLibrary(name as dynamic, kind as string, key as string)
    m.libraries.push({ name: msOr(name, ""), kind: kind, key: key, row: invalid, done: false })
end sub

' The rows, at once: Menus (posters arrive as their titles are found), then a row per
' library with a "Loading" tile until its first page arrives. Then every library's first page.
sub startLibraries()
    root = CreateObject("roSGNode", "ContentNode")
    if m.catalog.Count() > 0
        row = root.createChild("ContentNode")
        row.title = "Menus"
        for each entry in m.catalog
            tile = row.createChild("ContentNode")
            tile.addFields({ kind: "manifest", entryId: entry["id"], badge: true, itemRef: -1, wide: false, place: placeOf(entry) })
            tile.title = msOr(entry["title"]["name"], "")
            year = ""
            if entry["title"]["year"] <> invalid then year = entry["title"]["year"].ToStr()
            tile.shortDescriptionLine2 = joinDots([year, tile.place])
            m.manifestTiles[entry["id"]] = tile
        end for
    end if
    for i = 0 to m.libraries.Count() - 1
        lib = m.libraries[i]
        lib.row = root.createChild("ContentNode")
        lib.row.title = lib.name
        loading = lib.row.createChild("ContentNode")
        loading.addFields({ kind: "loading", entryId: "", badge: false, itemRef: -1, wide: false })
        loading.title = "Loading…"
        requestPage(i, 0)
    end for
    m.rows.content = root
    m.rows.setFocus(true)
    resolveIndexItems()
    showBrowseStatus()
    if m.libraries.Count() = 0 then setStatus("No movie or TV libraries on " + m.login.name + ".")
    librariesProgress()
    launchComplete()
    if m.deepLink <> invalid then openDeepLink()
end sub

' "Media Center (Plex) · 3 menus from http://…" — which server, which kind, where menus come from.
sub showBrowseStatus()
    menus = m.catalog.Count().ToStr() + " menus"
    if m.catalog.Count() = 1 then menus = "1 menu"
    if m.menuBase = invalid and isPlex()
        menus = "no menus (no menu server)"
    else if m.menuBase = invalid
        menus = "no menus (the Open Video Menu plugin isn't installed)"
    else if isPlex()
        menus = menus + " from " + m.menuBase
    else
        menus = menus + " (built in)"
    end if
    m.top.findNode("browseStatus").text = serverLabel() + " · " + menus
end sub

function serverLabel() as string
    if isPlex() then return m.login.name + " (Plex)"
    return m.login.name + " (Jellyfin)"
end function

sub requestPage(i as integer, start as integer)
    lib = m.libraries[i]
    if isPlex()
        kind = "2"
        if lib.kind = "movie" then kind = "1"
        url = m.login.url + "/library/sections/" + lib.key + "/all?sort=titleSort&type=" + kind + "&X-Plex-Container-Start=" + start.ToStr() + "&X-Plex-Container-Size=" + pageSize().ToStr()
    else
        kind = "Series"
        if lib.kind = "movie" then kind = "Movie&fields=Path,MediaSources"
        url = m.login.url + "/Items?userId=" + m.login.userId + "&parentId=" + lib.key + "&recursive=true&sortBy=SortName&includeItemTypes=" + kind + "&startIndex=" + start.ToStr() + "&limit=" + pageSize().ToStr() + "&enableTotalRecordCount=true"
    end if
    http(url, "GET", invalid, "onLibraryPage", { lib: i, start: start })
end sub

sub onLibraryPage(res as object, ctx as dynamic)
    if res.code = 401
        signOut()
        return
    end if
    lib = m.libraries[ctx.lib]
    if lib = invalid or lib.row = invalid then return ' signed out or reloaded meanwhile
    items = itemsOf(res.json)
    if ctx.start = 0 then lib.row.removeChildIndex(0) ' the "Loading" tile
    for each item in items
        if lib.kind = "movie"
            matchMovie(item)
            addTile(lib.row, item, "movie", m.menuOf[itemKey(item)] <> invalid)
        else
            key = itemKey(item)
            m.showTiles[key] = addTile(lib.row, item, "show", manifestsOfShow(key).Count() > 0)
        end if
    end for
    total = 0
    if res.json <> invalid and isPlex() and res.json["MediaContainer"] <> invalid then total = msOr(res.json["MediaContainer"]["totalSize"], 0)
    if res.json <> invalid and not isPlex() then total = msOr(res.json["TotalRecordCount"], 0)
    nextStart = ctx.start + items.Count()
    if items.Count() > 0 and nextStart < total
        requestPage(ctx.lib, nextStart)
    else
        lib.done = true
        if res.json = invalid then lib.row.title = lib.name + " (couldn't load)"
        if lib.row.getChildCount() = 0 then removeRow(lib)
        librariesProgress()
    end if
end sub

' An empty library leaves no empty row behind.
sub removeRow(lib as object)
    root = m.rows.content
    for i = 0 to root.getChildCount() - 1
        if root.getChild(i).isSameNode(lib.row)
            root.removeChildIndex(i)
            exit for
        end if
    end for
end sub

' A movie that has a menu: its tile in the Menus row gets its poster, and the movie opens the menu.
sub matchMovie(movie as object)
    for each f in filesOf(movie)
        for each entry in msManifestsFor(f.path, m.catalog)
            if not m.found.DoesExist(entry["id"])
                m.found[entry["id"]] = movie
                updateManifestTile(entry["id"])
            end if
            m.menuOf[itemKey(movie)] = entry["id"]
        end for
    end for
end sub

sub updateManifestTile(id as string)
    tile = m.manifestTiles[id]
    if tile = invalid then return
    movie = m.found[id]
    series = m.manifestShow[id]
    if movie <> invalid
        tile.HDPosterUrl = posterOf(movie)
        tile.shortDescriptionLine2 = joinDots([yearOf(movie), tile.place])
    else if series <> invalid
        tile.HDPosterUrl = posterOf(series)
        ' The tile's title is already the show's; say which season and disc instead when the menu does.
        if tile.place <> "" then tile.shortDescriptionLine2 = tile.place else tile.shortDescriptionLine2 = titleOf(series)
    end if
end sub

' Once every movie library is loaded, the menus not found yet are TV menus: look for their shows.
sub librariesProgress()
    moviesDone = true
    allDone = true
    for each lib in m.libraries
        if not lib.done
            allDone = false
            if lib.kind = "movie" then moviesDone = false
        end if
    end for
    if moviesDone and m.tvLookups = -1 then findTvManifests()
    if not allDone or m.tvLookups > 0
        m.top.findNode("browseStatus").text = serverLabel() + " · loading your library…"
    else
        showBrowseStatus()
    end if
end sub

' --- menus the menu server has already found in the library -------------------------------------
' A menu server that knows the media server's library says which item plays each menu's media (the
' index's "items", for this server only). Then a menu's film or show is one request away, and its tile
' fills in at once instead of after the library is paged through. Without items, the matching below
' (by file paths) does it as before.

sub resolveIndexItems()
    server = LCase(msOr(m.login.machineId, msOr(m.login.serverId, "")))
    if server = "" then return
    for each entry in m.catalog
        film = invalid
        series = invalid
        for each it in msOr(entry["items"], [])
            if LCase(msOr(it["server"], "")) = server
                if it["type"] = "movie" and film = invalid then film = it["id"]
                if it["type"] = "episode" and series = invalid and it["show"] <> invalid then series = it["show"]
            end if
        end for
        if film <> invalid
            http(itemUrl(film), "GET", invalid, "onIndexFilm", entry)
        else if series <> invalid
            http(itemUrl(series), "GET", invalid, "onIndexShow", entry)
        end if
    end for
end sub

function itemUrl(id as string) as string
    if isPlex() then return m.login.url + "/library/metadata/" + id
    return m.login.url + "/Items/" + id + "?userId=" + m.login.userId + "&fields=Path,MediaSources"
end function

sub onIndexFilm(res as object, entry as dynamic)
    items = itemsOf(res.json)
    if items.Count() = 0 or m.found = invalid then return
    id = entry["id"]
    film = items[0]
    if not m.found.DoesExist(id)
        m.found[id] = film
        updateManifestTile(id)
    end if
    m.menuOf[itemKey(film)] = id
end sub

sub onIndexShow(res as object, entry as dynamic)
    shows = itemsOf(res.json)
    if shows.Count() = 0 or m.manifestShow = invalid then return
    id = entry["id"]
    if m.manifestShow.DoesExist(id) then return
    m.manifestShow[id] = shows[0]
    updateManifestTile(id)
    key = itemKey(shows[0])
    if m.showTiles[key] <> invalid then m.showTiles[key].badge = true
end sub

' --- TV menus: find the show, confirmed by its episode files ---------------------------------
' Titles are unreliable (metadata agents rename shows: the example "Example Show" is the show
' "Example" in Plex), so as in the Android app: shows found by title, then the TV libraries'
' recently added episodes; a show only counts if one of its episode files is one the menu plays.

sub findTvManifests()
    m.tvLookups = 0
    m.manifestPending = {}
    m.manifestTriedRecent = {}
    for each entry in m.catalog
        name = entry["title"]["name"]
        if not m.found.DoesExist(entry["id"]) and not m.manifestShow.DoesExist(entry["id"])
            m.manifestPending[entry["id"]] = 0
            for each lib in m.libraries
                if lib.kind = "show" and name <> invalid
                    if isPlex()
                        url = m.login.url + "/library/sections/" + lib.key + "/all?type=2&title=" + percentEncodeQuery(name)
                    else
                        url = m.login.url + "/Items?userId=" + m.login.userId + "&parentId=" + lib.key + "&recursive=true&includeItemTypes=Series&limit=5&searchTerm=" + percentEncodeQuery(name)
                    end if
                    manifestLookup(entry, url, "onShowSearch", entry)
                end if
            end for
            if m.manifestPending[entry["id"]] = 0 then manifestSearchDone(entry)
        end if
    end for
end sub

' One request for a TV menu's lookup, counted so the app knows when the search is over.
sub manifestLookup(entry as object, url as string, handler as string, ctx as object)
    m.tvLookups = m.tvLookups + 1
    m.manifestPending[entry["id"]] = m.manifestPending[entry["id"]] + 1
    http(url, "GET", invalid, handler, ctx)
end sub

sub manifestLookupDone(entry as object)
    m.tvLookups = m.tvLookups - 1
    m.manifestPending[entry["id"]] = m.manifestPending[entry["id"]] - 1
    if m.manifestPending[entry["id"]] = 0 then manifestSearchDone(entry)
    librariesProgress()
end sub

' Nothing confirmed by title: try the episodes most recently added to each TV library.
sub manifestSearchDone(entry as object)
    id = entry["id"]
    if m.manifestShow.DoesExist(id) or m.manifestTriedRecent[id] = true then return
    m.manifestTriedRecent[id] = true
    for each lib in m.libraries
        if lib.kind = "show"
            if isPlex()
                url = m.login.url + "/library/sections/" + lib.key + "/all?type=4&sort=addedAt:desc&X-Plex-Container-Start=0&X-Plex-Container-Size=100"
            else
                url = m.login.url + "/Items?userId=" + m.login.userId + "&parentId=" + lib.key + "&recursive=true&includeItemTypes=Episode&sortBy=DateCreated&sortOrder=Descending&limit=100&fields=Path,MediaSources"
            end if
            manifestLookup(entry, url, "onRecentEpisodes", entry)
        end if
    end for
end sub

sub onShowSearch(res as object, entry as dynamic)
    for each series in itemsOf(res.json)
        manifestLookup(entry, episodesUrl(series), "onManifestEpisodes", { entry: entry, show: series })
    end for
    manifestLookupDone(entry)
end sub

sub onRecentEpisodes(res as object, entry as dynamic)
    if not m.manifestShow.DoesExist(entry["id"])
        for each ep in itemsOf(res.json)
            if episodeOfManifest(ep, entry)
                ' Its show, then all its episodes (which confirms it and fills the cache).
                if isPlex()
                    url = m.login.url + "/library/metadata/" + msOr(ep["grandparentRatingKey"], "")
                else
                    url = m.login.url + "/Items/" + msOr(ep["SeriesId"], "") + "?userId=" + m.login.userId
                end if
                manifestLookup(entry, url, "onManifestShowFound", entry)
                exit for
            end if
        end for
    end if
    manifestLookupDone(entry)
end sub

sub onManifestShowFound(res as object, entry as dynamic)
    shows = itemsOf(res.json)
    if shows.Count() > 0 and not m.manifestShow.DoesExist(entry["id"])
        manifestLookup(entry, episodesUrl(shows[0]), "onManifestEpisodes", { entry: entry, show: shows[0] })
    end if
    manifestLookupDone(entry)
end sub

function episodesUrl(series as object) as string
    if isPlex() then return m.login.url + "/library/metadata/" + series["ratingKey"] + "/allLeaves"
    return m.login.url + "/Shows/" + series["Id"] + "/Episodes?userId=" + m.login.userId + "&fields=Path,MediaSources"
end function

function episodeOfManifest(ep as object, entry as object) as boolean
    for each f in filesOf(ep)
        if msManifestsFor(f.path, [entry]).Count() > 0 then return true
    end for
    return false
end function

' The show is the menu's when one of its episode files is a file the manifest references.
sub onManifestEpisodes(res as object, ctx as dynamic)
    eps = itemsOf(res.json)
    key = itemKey(ctx.show)
    m.episodesByShow[key] = eps
    id = ctx.entry["id"]
    if not m.manifestShow.DoesExist(id)
        for each ep in eps
            if episodeOfManifest(ep, ctx.entry)
                m.shows[id] = ep
                m.manifestShow[id] = ctx.show
                updateManifestTile(id)
                if m.showTiles[key] <> invalid then m.showTiles[key].badge = true
                exit for
            end if
        end for
    end if
    manifestLookupDone(ctx.entry)
end sub

' The files a TV show's menu can play: its episodes.
function manifestEpisodeFiles(id as string) as object
    files = []
    series = m.manifestShow[id]
    if series = invalid then return files
    for each ep in msOr(m.episodesByShow[itemKey(series)], [])
        files.Append(filesOf(ep))
    end for
    return files
end function

' Whether a menu tile can open yet: its title is known, or the search for it is over.
function manifestReady(id as string) as boolean
    if m.found.DoesExist(id) or m.manifestShow.DoesExist(id) then return true
    return m.tvLookups = 0
end function

function manifestsOfShow(key as string) as object
    out = []
    for each entry in m.catalog
        series = m.manifestShow[entry["id"]]
        if series <> invalid and itemKey(series) = key then out.push(entry)
    end for
    return out
end function

' Which season and disc a menu is, e.g. "Season 5 · Disc 2" ("" when it doesn't say). Discs are
' "Disc" for menus from a DVD or Blu-ray, "Volume" otherwise.
function placeOf(entry as object) as string
    t = entry["title"]
    if t = invalid then return ""
    parts = []
    if t["season"] <> invalid
        if t["season"] = 0 then parts.push("Specials") else parts.push("Season " + t["season"].ToStr())
    end if
    if t["volume"] <> invalid
        word = "Volume "
        src = entry["source"]
        if src <> invalid
            kind = msOr(src["kind"], "")
            if kind = "dvd" or kind = "bluray" then word = "Disc "
        end if
        parts.push(word + t["volume"].ToStr())
    end if
    return joinDots(parts)
end function

' A menu's name with its season and disc, for headings: "All in the Family — Season 5 · Disc 2".
function menuName(entry as object) as string
    name = msOr(entry["title"]["name"], "Menu")
    place = placeOf(entry)
    if place = "" then return name
    return name + " — " + place
end function

' Menus by name, then season, then disc (spec §3.1); otherwise in the server's order.
sub sortCatalog()
    for i = 0 to m.catalog.Count() - 1
        e = m.catalog[i]
        t = e["title"]
        e["sortKey"] = LCase(msOr(t["name"], "")) + Chr(1) + sortNumber(t["season"]) + Chr(1) + sortNumber(t["volume"]) + Chr(1) + Right("00000" + i.ToStr(), 6)
    end for
    m.catalog.SortBy("sortKey")
end sub

function sortNumber(n as dynamic) as string
    if n = invalid then return ""
    return Right("0000" + n.ToStr(), 5)
end function

' --- reading Plex or Jellyfin items -------------------------------------------------------------

function itemKey(item as object) as string
    if isPlex() then return msOr(item["ratingKey"], "")
    return msOr(item["Id"], "")
end function

function titleOf(item as object) as string
    if isPlex() then return msOr(item["title"], "")
    return msOr(item["Name"], "")
end function

function yearOf(item as object) as string
    y = item["ProductionYear"]
    if isPlex() then y = item["year"]
    if y = invalid then return ""
    return y.ToStr()
end function

function posterOf(item as object) as dynamic
    if isPlex() then return plexImage(item["thumb"])
    return m.login.url + "/Items/" + item["Id"] + "/Images/Primary?fillWidth=264&fillHeight=396&quality=90"
end function

function thumbOf(ep as object) as dynamic
    if isPlex() then return plexImage(ep["thumb"], 400, 225)
    return m.login.url + "/Items/" + ep["Id"] + "/Images/Primary?fillWidth=400&fillHeight=225&quality=90"
end function

' Where playback would resume, in seconds (0 = from the start).
function resumeOf(item as object) as float
    if isPlex() then return msOr(item["viewOffset"], 0) / 1000
    ud = item["UserData"]
    if ud = invalid then return 0
    return msOr(ud["PlaybackPositionTicks"], 0) / 10000000
end function

' When the item was last played, in seconds since 1970 (0 = never or unknown).
function playedAtOf(item as object) as integer
    if isPlex() then return msOr(item["lastViewedAt"], 0)
    ud = item["UserData"]
    if ud = invalid or ud["LastPlayedDate"] = invalid then return 0
    t = CreateObject("roDateTime")
    t.FromISO8601String(ud["LastPlayedDate"])
    return t.AsSeconds()
end function

function watchedOf(item as object) as boolean
    if isPlex() then return msOr(item["viewCount"], 0) > 0
    ud = item["UserData"]
    return ud <> invalid and ud["Played"] = true
end function

' Unwatched episodes of a show; -1 for anything that isn't a show.
function unwatchedOf(item as object) as integer
    if isPlex()
        if item["leafCount"] = invalid then return -1
        return item["leafCount"] - msOr(item["viewedLeafCount"], 0)
    end if
    if item["Type"] <> "Series" or item["UserData"] = invalid then return -1
    return msOr(item["UserData"]["UnplayedItemCount"], 0)
end function

function progressOf(item as object) as string
    unwatched = unwatchedOf(item)
    if unwatched > 0 then return unwatched.ToStr() + " unwatched"
    if unwatched = 0 then return "Watched"
    if resumeOf(item) > 0 then return "In progress"
    if watchedOf(item) then return "Watched"
    return ""
end function

function joinDots(parts as object) as string
    out = []
    for each p in parts
        if p <> "" then out.push(p)
    end for
    return out.Join(" · ")
end function

function tileItem(tile as object) as object
    return m.tileItems[tile.itemRef]
end function

' A tile for an item: { kind, entryId, badge, itemRef, wide } plus title, poster and a second line.
function addTile(row as object, item as object, kind as string, badge as boolean) as object
    m.tileItems.push(item)
    tile = row.createChild("ContentNode")
    tile.addFields({ kind: kind, entryId: "", badge: badge, itemRef: m.tileItems.Count() - 1, wide: kind = "episode" })
    tile.title = titleOf(item)
    if kind = "episode"
        tile.HDPosterUrl = thumbOf(item)
        n = item["IndexNumber"]
        if isPlex() then n = item["index"]
        if n <> invalid then tile.title = n.ToStr() + ". " + tile.title
        tile.shortDescriptionLine2 = progressOf(item)
    else
        tile.HDPosterUrl = posterOf(item)
        tile.shortDescriptionLine2 = joinDots([yearOf(item), progressOf(item)])
    end if
    return tile
end function

' --- where the viewer stopped, remembered by this app ---------------------------------------
' Servers keep their own resume points, but skip short viewings and often extras, and don't know
' which soundtrack a menu chose. So the app also keeps, per server and title, where the viewer
' stopped and with which tracks (registry "stops": { key: { position, audio, subtitle, at } }).

function stopKey(item as object) as string
    return msOr(m.login.machineId, msOr(m.login.serverId, m.login.url)) + "|" + itemKey(item)
end function

function stopsRead() as object
    stops = ParseJson(m.registry.Read("stops"))
    if stops = invalid then stops = {}
    return stops
end function

sub stopsWrite(stops as object)
    m.registry.Write("stops", FormatJson(stops))
    m.registry.Flush()
end sub

function stopOf(item as object) as dynamic
    return stopsRead()[stopKey(item)]
end function

' A new resume point replaces the previous one only once the viewer has watched 10 seconds of this
' playback; a brief or accidental play leaves the previous point as it was.
sub rememberStop(item as object, position as float, req as object)
    if position < 5 or m.playing = invalid or m.playing.startedAt = invalid then return
    ' A title made of segments isn't one of the server's items: the menu remembers where it stopped.
    if m.segPlay <> invalid then return
    if UpTime(0) - m.playing.startedAt < 10 then return
    stops = stopsRead()
    now = CreateObject("roDateTime").AsSeconds()
    stops[stopKey(item)] = { position: position, audio: req.audio, subtitle: req.subtitle, at: now, touchedAt: now }
    ' Keep the 200 most recent.
    if stops.Count() > 200
        oldest = invalid
        for each k in stops
            if oldest = invalid or stops[k].at < stops[oldest].at then oldest = k
        end for
        stops.Delete(oldest)
    end if
    stopsWrite(stops)
end sub

' Playback of the title started here: note it, so the server's state changing because of this play
' isn't mistaken for viewing elsewhere. With no record yet, keep the server's current resume point
' as this app's own first, so a short play here (which may reset the server's) can't lose it.
sub touchStop(item as object)
    stops = stopsRead()
    key = stopKey(item)
    now = CreateObject("roDateTime").AsSeconds()
    mine = stops[key]
    if mine <> invalid and not changedElsewhere(item, mine)
        mine.touchedAt = now
    else if resumeOf(item) > 0
        stops[key] = { position: resumeOf(item), audio: 1, subtitle: 0, at: playedAtOf(item), touchedAt: now }
    else
        if mine <> invalid then stops.Delete(key)
        return
    end if
    stopsWrite(stops)
end sub

sub forgetStop(item as object)
    stops = stopsRead()
    if stops.Delete(stopKey(item)) then stopsWrite(stops)
end sub

' --- a show: its menus, then its seasons ------------------------------------------------------

sub openShow(tile as object)
    showItem = tileItem(tile)
    m.openShowItem = showItem
    m.top.findNode("showTitle").text = titleOf(showItem)
    key = itemKey(showItem)
    if m.episodesByShow[key] <> invalid
        buildShow(showItem)
        return
    end if
    m.top.findNode("showInfo").text = "Loading episodes…"
    m.showRows.content = CreateObject("roSGNode", "ContentNode")
    show("show")
    http(episodesUrl(showItem), "GET", invalid, "onShowEpisodes", showItem)
end sub

sub onShowEpisodes(res as object, showItem as dynamic)
    m.episodesByShow[itemKey(showItem)] = itemsOf(res.json)
    if m.openShowItem <> invalid and itemKey(m.openShowItem) = itemKey(showItem) and m.top.findNode("show").visible
        buildShow(showItem)
    end if
end sub

sub buildShow(showItem as object)
    key = itemKey(showItem)
    root = CreateObject("roSGNode", "ContentNode")
    sizes = []
    heights = []

    manifests = manifestsOfShow(key)
    if manifests.Count() > 0
        row = root.createChild("ContentNode")
        row.title = "Menus"
        sizes.push([264, 470])
        heights.push(540)
        for each entry in manifests
            t = row.createChild("ContentNode")
            t.addFields({ kind: "manifest", entryId: entry["id"], badge: true, itemRef: -1, wide: false })
            t.title = msOr(entry["title"]["name"], "Menu")
            t.shortDescriptionLine2 = placeOf(entry)
            t.HDPosterUrl = posterOf(showItem)
        end for
    end if

    ' Seasons in order (0 = Specials, last), episodes in order within each.
    bySeason = {}
    numbers = []
    for each ep in msOr(m.episodesByShow[key], [])
        n = ep["ParentIndexNumber"]
        if isPlex() then n = ep["parentIndex"]
        n = msOr(n, 0)
        if bySeason[n.ToStr()] = invalid
            bySeason[n.ToStr()] = []
            numbers.push(n)
        end if
        bySeason[n.ToStr()].push(ep)
    end for
    numbers.Sort()
    if numbers.Count() > 1 and numbers[0] = 0 then numbers.push(numbers.Shift())
    field = "IndexNumber"
    if isPlex() then field = "index"
    for each n in numbers
        row = root.createChild("ContentNode")
        row.title = "Season " + n.ToStr()
        if n = 0 then row.title = "Specials"
        eps = bySeason[n.ToStr()]
        eps.SortBy(field)
        for each ep in eps
            addTile(row, ep, "episode", false)
        end for
        sizes.push([400, 330])
        heights.push(400)
    end for

    seasons = numbers.Count().ToStr() + " seasons"
    if numbers.Count() = 1 then seasons = "1 season"
    m.top.findNode("showInfo").text = joinDots([yearOf(showItem), seasons])
    if sizes.Count() = 0
        sizes.push([400, 330])
        heights.push(400)
    end if
    m.showRows.rowItemSize = sizes
    m.showRows.rowHeights = heights
    m.showRows.content = root
    show("show")
    m.showRows.jumpToRowItem = [0, 0]
    m.showRows.setFocus(true)
    if root.getChildCount() = 0 then setStatus("No episodes of " + titleOf(showItem) + " were found.")
end sub

sub onShowTileChosen()
    sel = m.showRows.rowItemSelected
    chooseTile(m.showRows.content.getChild(sel[0]).getChild(sel[1]), "show", m.showRows)
end sub

' --- titles without a menu ----------------------------------------------------------------------

' Play the title's (first) file from where the viewer left off, as the server's own apps do.
sub playDirect(item as object, tile as object, screen as string, list as object)
    files = filesOf(item)
    if files.Count() = 0
        setStatus("No playable file for " + titleOf(item) + ".")
        return
    end if
    m.direct = { item: item, tile: tile, screen: screen, list: list }
    ' Where this app last stopped it (with those tracks) when that's the newest; else the server's.
    req = { start: resumeOf(item) }
    mine = stopOf(item)
    if mine <> invalid and not changedElsewhere(item, mine) then req = { start: mine.position, audio: mine.audio, subtitle: mine.subtitle }
    startTitle(req, files[0])
end sub

sub backFromTitle(position as float, watched as boolean)
    d = m.direct
    m.direct = invalid
    if d = invalid then return
    if watched then position = 0
    if isPlex()
        d.item["viewOffset"] = Int(position * 1000)
        if watched then d.item["viewCount"] = msOr(d.item["viewCount"], 0) + 1
    else
        ud = msOr(d.item["UserData"], {})
        ud["PlaybackPositionTicks"] = position * 10000000
        if watched then ud["Played"] = true
        d.item["UserData"] = ud
    end if
    if d.tile = invalid
        ' Resumed from a menu's question: no tile of its own.
    else if d.tile.kind = "episode"
        d.tile.shortDescriptionLine2 = progressOf(d.item)
    else
        d.tile.shortDescriptionLine2 = joinDots([yearOf(d.item), progressOf(d.item)])
    end if
    show(d.screen)
    d.list.setFocus(true)
end sub

' --- deep links -----------------------------------------------------------------------------------
' A deep link names a title by its ID on the signed-in server (Jellyfin Id, Plex ratingKey). It opens
' once the library is on screen: a movie's menu if it has one, a show's screen, else the title plays.
' Signed out, there's nothing to open it from: the link is dropped and sign-in carries on.

sub onDeepLink()
    link = m.top.deepLink
    if link = invalid or link.contentId = invalid then return
    if m.login = invalid
        m.deepLink = invalid
        return
    end if
    m.deepLink = link
    ' Already running: leave whatever is open and open it now. Still loading: startLibraries opens it.
    if m.launched = true and m.libraries <> invalid
        leaveForDeepLink()
        openDeepLink()
    end if
end sub

' Back to the library from anywhere: a title playing stops (its resume point kept, as Back does),
' and an open menu or question closes.
sub leaveForDeepLink()
    m.holdTimer.control = "stop"
    m.navTimer.control = "stop"
    position = 0
    if m.player <> invalid
        position = playerPosition()
        if m.playing <> invalid then rememberStop(msOr(m.playing.stopItem, m.playing.item), position, m.playing.req)
        report("/Sessions/Playing/Stopped")
        closePlayer()
        m.playing = invalid
    end if
    m.segPlay = invalid
    if m.menu <> invalid
        m.top.removeChild(m.menu)
        m.menu = invalid
    else if m.direct <> invalid
        backFromTitle(position, false)
    end if
    if m.top.dialog <> invalid then m.top.dialog.close = true
    m.opening = invalid
    show("browse")
    m.rows.setFocus(true)
end sub

sub openDeepLink()
    link = m.deepLink
    m.deepLink = invalid
    id = link.contentId.ToStr()
    if not CreateObject("roRegex", "^[A-Za-z0-9_-]+$", "").IsMatch(id)
        setStatus("ovmplayer can't open that link.")
        return
    end if
    if isPlex()
        http(m.login.url + "/library/metadata/" + id, "GET", invalid, "onDeepLinkItem")
    else
        http(m.login.url + "/Items/" + id + "?userId=" + m.login.userId, "GET", invalid, "onDeepLinkItem")
    end if
end sub

sub onDeepLinkItem(res as object, ctx as dynamic)
    items = itemsOf(res.json)
    if res.code <> 200 or items.Count() = 0
        setStatus("Couldn't find that title on " + m.login.name + ".")
        return
    end if
    item = items[0]
    kind = LCase(msOr(item["Type"], ""))
    if isPlex() then kind = LCase(msOr(item["type"], ""))
    if kind = "show" or kind = "series"
        openShow(addTile(CreateObject("roSGNode", "ContentNode"), item, "show", false))
    else if kind = "movie" or kind = "episode"
        if kind = "movie" then matchMovie(item)
        entryId = m.menuOf[itemKey(item)]
        if entryId <> invalid and m.manifestTiles[entryId] <> invalid
            chooseTile(m.manifestTiles[entryId], "browse", m.rows)
        else
            playDirect(item, invalid, "browse", m.rows)
        end if
    else
        setStatus("ovmplayer can't open that kind of item.")
    end if
end sub
