' The library beyond disc menus: a row per movie or TV library on the home screen, a screen per
' show (its Discs, then a row per season), and titles without a menu, which simply play (from
' where the viewer left off). Plex and Jellyfin items are kept as the server sent them; the small
' accessors below read either.

sub resetLibraries()
    m.found = {}      ' manifest id -> the movie it belongs to
    m.shows = {}      ' manifest id -> an episode of the show it belongs to
    m.menuOf = {}     ' movie key -> manifest id, for movies whose files have a menu
    m.episodeFiles = []
    m.episodesByShow = {}
    m.libraries = []  ' [{ name, kind: "movie" | "show", items }] in the server's order
    m.tileItems = []  ' what each tile stands for (tile.itemRef indexes this)
end sub

function addLibrary(name as dynamic, kind as string) as integer
    m.libraries.push({ name: msOr(name, ""), kind: kind, items: [] })
    return m.libraries.Count() - 1
end function

sub addEpisode(ep as object)
    key = showKeyOf(ep)
    if key = invalid then return
    if m.episodesByShow[key] = invalid then m.episodesByShow[key] = []
    m.episodesByShow[key].push(ep)
end sub

sub onShowList(res as object, ctx as dynamic)
    m.moviesPending = m.moviesPending - 1
    if res.json <> invalid and ctx <> invalid then m.libraries[ctx].items = itemsOf(res.json)
    if m.moviesPending <= 0 then showRows()
end sub

' --- reading Plex or Jellyfin items -------------------------------------------------------------

function itemKey(item as object) as string
    if isPlex() then return msOr(item["ratingKey"], "")
    return msOr(item["Id"], "")
end function

function showKeyOf(ep as object) as dynamic
    if isPlex() then return ep["grandparentRatingKey"]
    return ep["SeriesId"]
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

function progressOf(item as object) as string
    unwatched = unwatchedOf(item)
    if unwatched > 0 then return unwatched.ToStr() + " unwatched"
    if unwatched = 0 then return "Watched"
    if resumeOf(item) > 0 then return "In progress"
    if watchedOf(item) then return "Watched"
    return ""
end function

'  Unwatched episodes of a show; -1 for anything that isn't a show.
function unwatchedOf(item as object) as integer
    if isPlex()
        if item["leafCount"] = invalid then return -1
        return item["leafCount"] - msOr(item["viewedLeafCount"], 0)
    end if
    if item["Type"] <> "Series" or item["UserData"] = invalid then return -1
    return msOr(item["UserData"]["UnplayedItemCount"], 0)
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

' --- home rows --------------------------------------------------------------------------------

sub addLibraryRows(root as object)
    withDiscs = {}
    for each id in m.shows
        key = showKeyOf(m.shows[id])
        if key <> invalid then withDiscs[key] = true
    end for
    for each lib in m.libraries
        if lib.items.Count() > 0
            row = root.createChild("ContentNode")
            row.title = lib.name
            for each item in lib.items
                if lib.kind = "movie"
                    addTile(row, item, "movie", m.menuOf[itemKey(item)] <> invalid)
                else
                    addTile(row, item, "show", withDiscs[itemKey(item)] = true)
                end if
            end for
        end if
    end for
end sub

' --- a show: its discs, then its seasons ------------------------------------------------------

sub openShow(tile as object)
    showItem = tileItem(tile)
    key = itemKey(showItem)
    root = CreateObject("roSGNode", "ContentNode")
    sizes = []
    heights = []

    discs = invalid
    for each entry in m.catalog
        ep = m.shows[entry["id"]]
        if ep <> invalid and showKeyOf(ep) = key
            if discs = invalid
                discs = root.createChild("ContentNode")
                discs.title = "Discs"
                sizes.push([264, 470])
                heights.push(540)
            end if
            t = discs.createChild("ContentNode")
            t.addFields({ kind: "disc", entryId: entry["id"], badge: true, itemRef: -1, wide: false })
            t.title = msOr(entry["title"]["name"], "Disc")
            t.HDPosterUrl = posterOf(showItem)
            t.shortDescriptionLine2 = msOr(entry["title"]["disc"], "")
        end if
    end for

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

    m.top.findNode("showTitle").text = titleOf(showItem)
    m.top.findNode("showInfo").text = joinDots([yearOf(showItem), numbers.Count().ToStr() + " seasons"])
    if numbers.Count() = 1 then m.top.findNode("showInfo").text = joinDots([yearOf(showItem), "1 season"])
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
