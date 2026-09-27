' The library beyond disc menus: a row per movie or TV library on the home screen, a screen per
' show (its Discs, then a row per season), and titles without a menu, which simply play (from
' where the viewer left off). Plex and Jellyfin items are kept as the server sent them; the small
' accessors below read either.
'
' Loading never blocks the screen (as in the Android app): the rows appear at once and fill page by
' page. Movie pages are also where movie discs find their film. TV discs look for their show by
' title once the movie libraries are done, and a show's episodes load when it's first needed.

function pageSize() as integer
    return 300
end function

sub resetLibraries()
    m.found = {}          ' manifest id -> the movie it belongs to
    m.shows = {}          ' manifest id -> an episode of the show it belongs to (TV discs)
    m.discShow = {}       ' manifest id -> that series
    m.menuOf = {}         ' movie key -> manifest id, for movies whose files have a menu
    m.episodesByShow = {} ' show key -> its episodes, once loaded
    m.libraries = []      ' [{ name, kind: "movie" | "show", key, row, done }] in the server's order
    m.tileItems = []      ' what each tile stands for (tile.itemRef indexes this)
    m.discTiles = {}      ' manifest id -> its tile in the Disc Menus row
    m.showTiles = {}      ' show key -> its tile in a library row
    m.tvLookups = -1      ' TV disc lookups still running; -1 = not started
end sub

sub addLibrary(name as dynamic, kind as string, key as string)
    m.libraries.push({ name: msOr(name, ""), kind: kind, key: key, row: invalid, done: false })
end sub

' The rows, at once: Disc Menus (posters arrive as their titles are found), then a row per
' library with a "Loading" tile until its first page arrives. Then every library's first page.
sub startLibraries()
    root = CreateObject("roSGNode", "ContentNode")
    if m.catalog.Count() > 0
        row = root.createChild("ContentNode")
        row.title = "Disc Menus"
        for each entry in m.catalog
            tile = row.createChild("ContentNode")
            tile.addFields({ kind: "disc", entryId: entry["id"], badge: true, itemRef: -1, wide: false })
            tile.title = msOr(entry["title"]["name"], "")
            if entry["title"]["year"] <> invalid then tile.shortDescriptionLine2 = entry["title"]["year"].ToStr()
            m.discTiles[entry["id"]] = tile
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
    showBrowseStatus()
    if m.libraries.Count() = 0 then setStatus("No movie or TV libraries on " + m.login.name + ".")
    librariesProgress()
end sub

' "Media Center (Plex) · 3 disc menus from http://…" — which server, which kind, where menus come from.
sub showBrowseStatus()
    menus = m.catalog.Count().ToStr() + " disc menus"
    if m.catalog.Count() = 1 then menus = "1 disc menu"
    if m.menuBase = invalid and isPlex()
        menus = "no disc menus (no menu server)"
    else if m.menuBase = invalid
        menus = "no disc menus (the MenuStream plugin isn't installed)"
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
            m.showTiles[key] = addTile(lib.row, item, "show", discsOfShow(key).Count() > 0)
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

' A movie that has a menu: its disc tile gets its poster, and the movie opens the menu.
sub matchMovie(movie as object)
    for each f in filesOf(movie)
        for each entry in msManifestsFor(f.path, m.catalog)
            if not m.found.DoesExist(entry["id"])
                m.found[entry["id"]] = movie
                updateDiscTile(entry["id"])
            end if
            m.menuOf[itemKey(movie)] = entry["id"]
        end for
    end for
end sub

sub updateDiscTile(id as string)
    tile = m.discTiles[id]
    if tile = invalid then return
    movie = m.found[id]
    series = m.discShow[id]
    if movie <> invalid
        tile.HDPosterUrl = posterOf(movie)
        tile.shortDescriptionLine2 = yearOf(movie)
    else if series <> invalid
        tile.HDPosterUrl = posterOf(series)
        tile.shortDescriptionLine2 = titleOf(series)
    end if
end sub

' Once every movie library is loaded, the discs not found yet are TV discs: look for their shows.
sub librariesProgress()
    moviesDone = true
    allDone = true
    for each lib in m.libraries
        if not lib.done
            allDone = false
            if lib.kind = "movie" then moviesDone = false
        end if
    end for
    if moviesDone and m.tvLookups = -1 then findTvDiscs()
    if not allDone or m.tvLookups > 0
        m.top.findNode("browseStatus").text = serverLabel() + " · loading your library…"
    else
        showBrowseStatus()
    end if
end sub

' --- TV discs: find the show, confirmed by its episode files ---------------------------------
' Titles are unreliable (metadata agents rename shows: the example disc "Example Show" is the show
' "Example" in Plex), so as in the Android app: shows found by title, then the TV libraries'
' recently added episodes; a show only counts if one of its episode files is one the disc plays.

sub findTvDiscs()
    m.tvLookups = 0
    m.discPending = {}
    m.discTriedRecent = {}
    for each entry in m.catalog
        name = entry["title"]["name"]
        if not m.found.DoesExist(entry["id"])
            m.discPending[entry["id"]] = 0
            for each lib in m.libraries
                if lib.kind = "show" and name <> invalid
                    if isPlex()
                        url = m.login.url + "/library/sections/" + lib.key + "/all?type=2&title=" + percentEncodeQuery(name)
                    else
                        url = m.login.url + "/Items?userId=" + m.login.userId + "&parentId=" + lib.key + "&recursive=true&includeItemTypes=Series&limit=5&searchTerm=" + percentEncodeQuery(name)
                    end if
                    discLookup(entry, url, "onShowSearch", entry)
                end if
            end for
            if m.discPending[entry["id"]] = 0 then discSearchDone(entry)
        end if
    end for
end sub

' One request for a disc's lookup, counted so the app knows when the search is over.
sub discLookup(entry as object, url as string, handler as string, ctx as object)
    m.tvLookups = m.tvLookups + 1
    m.discPending[entry["id"]] = m.discPending[entry["id"]] + 1
    http(url, "GET", invalid, handler, ctx)
end sub

sub discLookupDone(entry as object)
    m.tvLookups = m.tvLookups - 1
    m.discPending[entry["id"]] = m.discPending[entry["id"]] - 1
    if m.discPending[entry["id"]] = 0 then discSearchDone(entry)
    librariesProgress()
end sub

' Nothing confirmed by title: try the episodes most recently added to each TV library.
sub discSearchDone(entry as object)
    id = entry["id"]
    if m.discShow.DoesExist(id) or m.discTriedRecent[id] = true then return
    m.discTriedRecent[id] = true
    for each lib in m.libraries
        if lib.kind = "show"
            if isPlex()
                url = m.login.url + "/library/sections/" + lib.key + "/all?type=4&sort=addedAt:desc&X-Plex-Container-Start=0&X-Plex-Container-Size=100"
            else
                url = m.login.url + "/Items?userId=" + m.login.userId + "&parentId=" + lib.key + "&recursive=true&includeItemTypes=Episode&sortBy=DateCreated&sortOrder=Descending&limit=100&fields=Path,MediaSources"
            end if
            discLookup(entry, url, "onRecentEpisodes", entry)
        end if
    end for
end sub

sub onShowSearch(res as object, entry as dynamic)
    for each series in itemsOf(res.json)
        discLookup(entry, episodesUrl(series), "onDiscEpisodes", { entry: entry, show: series })
    end for
    discLookupDone(entry)
end sub

sub onRecentEpisodes(res as object, entry as dynamic)
    if not m.discShow.DoesExist(entry["id"])
        for each ep in itemsOf(res.json)
            if episodeOfDisc(ep, entry)
                ' Its show, then all its episodes (which confirms it and fills the cache).
                if isPlex()
                    url = m.login.url + "/library/metadata/" + msOr(ep["grandparentRatingKey"], "")
                else
                    url = m.login.url + "/Items/" + msOr(ep["SeriesId"], "") + "?userId=" + m.login.userId
                end if
                discLookup(entry, url, "onDiscShowFound", entry)
                exit for
            end if
        end for
    end if
    discLookupDone(entry)
end sub

sub onDiscShowFound(res as object, entry as dynamic)
    shows = itemsOf(res.json)
    if shows.Count() > 0 and not m.discShow.DoesExist(entry["id"])
        discLookup(entry, episodesUrl(shows[0]), "onDiscEpisodes", { entry: entry, show: shows[0] })
    end if
    discLookupDone(entry)
end sub

function episodesUrl(series as object) as string
    if isPlex() then return m.login.url + "/library/metadata/" + series["ratingKey"] + "/allLeaves"
    return m.login.url + "/Shows/" + series["Id"] + "/Episodes?userId=" + m.login.userId + "&fields=Path,MediaSources"
end function

function episodeOfDisc(ep as object, entry as object) as boolean
    for each f in filesOf(ep)
        if msManifestsFor(f.path, [entry]).Count() > 0 then return true
    end for
    return false
end function

' The show is the disc's when one of its episode files is a file the disc references.
sub onDiscEpisodes(res as object, ctx as dynamic)
    eps = itemsOf(res.json)
    key = itemKey(ctx.show)
    m.episodesByShow[key] = eps
    id = ctx.entry["id"]
    if not m.discShow.DoesExist(id)
        for each ep in eps
            if episodeOfDisc(ep, ctx.entry)
                m.shows[id] = ep
                m.discShow[id] = ctx.show
                updateDiscTile(id)
                if m.showTiles[key] <> invalid then m.showTiles[key].badge = true
                exit for
            end if
        end for
    end if
    discLookupDone(ctx.entry)
end sub

' The files a TV disc's menu can play: its show's episodes.
function discEpisodeFiles(id as string) as object
    files = []
    series = m.discShow[id]
    if series = invalid then return files
    for each ep in msOr(m.episodesByShow[itemKey(series)], [])
        files.Append(filesOf(ep))
    end for
    return files
end function

' Whether a disc tile can open yet: its title is known, or the search for it is over.
function discReady(id as string) as boolean
    if m.found.DoesExist(id) or m.discShow.DoesExist(id) then return true
    return m.tvLookups = 0
end function

function discsOfShow(key as string) as object
    out = []
    for each entry in m.catalog
        series = m.discShow[entry["id"]]
        if series <> invalid and itemKey(series) = key then out.push(entry)
    end for
    return out
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

' --- a show: its discs, then its seasons ------------------------------------------------------

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

    discs = discsOfShow(key)
    if discs.Count() > 0
        row = root.createChild("ContentNode")
        row.title = "Discs"
        sizes.push([264, 470])
        heights.push(540)
        for each entry in discs
            t = row.createChild("ContentNode")
            t.addFields({ kind: "disc", entryId: entry["id"], badge: true, itemRef: -1, wide: false })
            t.title = msOr(entry["title"]["name"], "Disc")
            t.HDPosterUrl = posterOf(showItem)
            t.shortDescriptionLine2 = msOr(entry["title"]["disc"], "")
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
    startTitle({ start: resumeOf(item) }, files[0])
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
    if d.tile.kind = "episode"
        d.tile.shortDescriptionLine2 = progressOf(d.item)
    else
        d.tile.shortDescriptionLine2 = joinDots([yearOf(d.item), progressOf(d.item)])
    end if
    show(d.screen)
    d.list.setFocus(true)
end sub
