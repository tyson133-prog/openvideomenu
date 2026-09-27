' MenuStream for Roku: the app flow. Choose Plex or Jellyfin, sign in (plex.tv PIN, or Jellyfin
' Quick Connect), list titles with disc menus, and open one in MenuView. Menus come from the
' Jellyfin MenuStream plugin, or for Plex from a MenuStream asset server. Plex specifics are in
' MainScene.plex.brs; m.login.kind says which server this is ("plex", else Jellyfin).

sub init()
    m.tasks = {}          ' running HTTP tasks by id, so they aren't garbage-collected
    m.taskSeq = 0
    m.deviceId = CreateObject("roDeviceInfo").GetChannelClientId()
    m.profile = deviceProfile()
    m.registry = CreateObject("roRegistrySection", "menustream")
    m.catalog = []
    m.servers = []

    m.chooseList = m.top.findNode("chooseList")
    m.chooseList.observeField("itemSelected", "onKindChosen")
    m.serverList = m.top.findNode("serverList")
    m.serverList.observeField("itemSelected", "onServerChosen")
    m.rows = m.top.findNode("rows")
    m.rows.observeField("rowItemSelected", "onTileChosen")
    m.showRows = m.top.findNode("showRows")
    m.showRows.observeField("rowItemSelected", "onShowTileChosen")

    ' While a title plays, tell the server where we are, as its own apps do (~every 10 s).
    m.progressTimer = CreateObject("roSGNode", "Timer")
    m.progressTimer.duration = 10
    m.progressTimer.repeat = true
    m.progressTimer.observeField("fire", "reportProgress")
    m.pollTimer = CreateObject("roSGNode", "Timer")
    m.pollTimer.duration = 3
    m.pollTimer.repeat = true
    m.pollTimer.observeField("fire", "onPollQuickConnect")

    saved = m.registry.Read("login")
    login = invalid
    if saved <> "" then login = ParseJson(saved)
    if login <> invalid and login.token <> invalid
        m.login = login
        loadBrowse()
    else
        chooseKind()
    end if
end sub

' First run (or after switching server): Plex or Jellyfin?
sub chooseKind()
    show("choose")
    content = CreateObject("roSGNode", "ContentNode")
    content.createChild("ContentNode").title = "Plex"
    content.createChild("ContentNode").title = "Jellyfin"
    m.chooseList.content = content
    m.chooseList.setFocus(true)
end sub

sub onKindChosen()
    m.plexServers = invalid
    if m.chooseList.itemSelected = 0 then plexSignIn() else findServers()
end sub

function isPlex() as boolean
    return m.login <> invalid and m.login.kind = "plex"
end function

' Headers proving to the menu server that this viewer may use it.
function assetHeaders() as object
    if isPlex() then return { "X-Plex-Token": m.login.token }
    return { "Authorization": authHeader(m.login.token) }
end function

' --- screens ------------------------------------------------------------------------------

sub show(name as string)
    for each id in ["choose", "servers", "code", "browse", "show"]
        m.top.findNode(id).visible = (id = name)
    end for
    setStatus("")
end sub

' A one-line message at the bottom, brought to the front so it shows over an open menu or video.
sub setStatus(text as string)
    status = m.top.findNode("status")
    status.text = text
    if text <> ""
        status.getParent().removeChild(status)
        m.top.appendChild(status)
        if m.statusTimer = invalid
            m.statusTimer = CreateObject("roSGNode", "Timer")
            m.statusTimer.duration = 6
            m.statusTimer.observeField("fire", "onStatusTimer")
        end if
        m.statusTimer.control = "start"
    end if
end sub

sub onStatusTimer()
    m.top.findNode("status").text = ""
end sub

' --- HTTP ---------------------------------------------------------------------------------

' Jellyfin requires its MediaBrowser Authorization header on every call (Jellyfin 12 turned the
' older X-Emby-Token header and api_key parameter off by default).
function authHeader(token as dynamic) as string
    q = Chr(34)
    h = "MediaBrowser Client=" + q + "MenuStream" + q + ", Device=" + q + "Roku" + q + ", DeviceId=" + q + m.deviceId + q + ", Version=" + q + "0.1" + q
    if token <> invalid then h = h + ", Token=" + q + token + q
    return h
end function

function headers() as object
    token = invalid
    if m.login <> invalid then token = m.login.token
    if isPlex() then return plexHeaders(token)
    return { "Authorization": authHeader(token), "Accept": "application/json", "Content-Type": "application/json" }
end function

' Run one request on an HttpTask and call `handler(response, context)` when it answers.
' extra (optional): { headers (instead of the signed-in server's), timeoutMs }.
sub http(url as string, method as string, body as dynamic, handler as string, context = invalid as dynamic, extra = invalid as dynamic)
    m.taskSeq = m.taskSeq + 1
    id = "t" + m.taskSeq.ToStr()
    task = CreateObject("roSGNode", "HttpTask")
    task.addFields({ taskId: id })
    req = { url: url, method: method, headers: headers(), body: body }
    if extra <> invalid
        if extra.headers <> invalid then req.headers = extra.headers
        if extra.timeoutMs <> invalid then req.timeoutMs = extra.timeoutMs
    end if
    task.request = req
    task.observeField("response", "onHttp")
    m.tasks[id] = { task: task, handler: handler, context: context }
    task.control = "RUN"
end sub

sub onHttp(event as object)
    task = event.getRoSGNode()
    pending = m.tasks[task.taskId]
    m.tasks.Delete(task.taskId)
    if pending = invalid then return
    res = event.getData()
    data = invalid
    if res.body <> invalid and res.body <> "" then data = ParseJson(res.body)
    res.json = data
    ' Handlers are called by name so each step of a flow reads top to bottom.
    if pending.handler = "onServerInfo" then onServerInfo(res, pending.context)
    if pending.handler = "onInitiate" then onInitiate(res, pending.context)
    if pending.handler = "onCheck" then onCheck(res, pending.context)
    if pending.handler = "onAuthenticated" then onAuthenticated(res, pending.context)
    if pending.handler = "onHealth" then onHealth(res, pending.context)
    if pending.handler = "onIndex" then onIndex(res, pending.context)
    if pending.handler = "onViews" then onViews(res, pending.context)
    if pending.handler = "onMovies" then onMovies(res, pending.context)
    if pending.handler = "onEpisodes" then onEpisodes(res, pending.context)
    if pending.handler = "onShowList" then onShowList(res, pending.context)
    if pending.handler = "onPlaybackInfo" then onPlaybackInfo(res, pending.context)
    if pending.handler = "onManifest" then onManifest(res, pending.context)
    if pending.handler = "onTitleFiles" then onTitleFiles(res, pending.context)
    if pending.handler = "onReported" then onReported(res, pending.context)
    if pending.handler = "onPlexPin" then onPlexPin(res, pending.context)
    if pending.handler = "onPlexPinCheck" then onPlexPinCheck(res, pending.context)
    if pending.handler = "onPlexResources" then onPlexResources(res, pending.context)
    if pending.handler = "onPlexIdentity" then onPlexIdentity(res, pending.context)
    if pending.handler = "onPlexSections" then onPlexSections(res, pending.context)
    if pending.handler = "onPlexAudioSet" then onPlexAudioSet(res, pending.context)
    if pending.handler = "onPlexDecision" then onPlexDecision(res, pending.context)
    if pending.handler = "onPlexTranscodeDecision" then onPlexTranscodeDecision(res, pending.context)
end sub

' --- finding a server -----------------------------------------------------------------------

sub findServers()
    show("servers")
    m.top.findNode("serversHint").text = "Looking on your network…"
    m.discover = CreateObject("roSGNode", "DiscoverTask")
    m.discover.observeField("servers", "onServersFound")
    m.discover.control = "RUN"
end sub

sub onServersFound()
    m.servers = m.discover.servers
    content = CreateObject("roSGNode", "ContentNode")
    for each s in m.servers
        item = content.createChild("ContentNode")
        item.title = s.name + "  ·  " + s.url.Replace("http://", "")
    end for
    content.createChild("ContentNode").title = "Type an address…"
    m.serverList.content = content
    if m.servers.Count() = 0
        m.top.findNode("serversHint").text = "No Jellyfin server answered on your network."
    else
        m.top.findNode("serversHint").text = "Found on your network:"
    end if
    m.serverList.setFocus(true)
end sub

sub onServerChosen()
    i = m.serverList.itemSelected
    if m.plexServers <> invalid and m.connecting = invalid and m.login = invalid
        plexConnect(m.plexServers[i])
        return
    end if
    if i < m.servers.Count()
        useServer(m.servers[i].url)
        return
    end if
    dialog = CreateObject("roSGNode", "StandardKeyboardDialog")
    dialog.title = "Jellyfin server address"
    dialog.message = ["For example 192.168.1.20:8096"]
    dialog.buttons = ["Connect", "Cancel"]
    dialog.observeField("buttonSelected", "onAddressEntered")
    m.top.dialog = dialog
end sub

sub onAddressEntered()
    dialog = m.top.dialog
    text = dialog.text
    ok = dialog.buttonSelected = 0
    dialog.close = true
    if not ok or text = "" then return
    url = text.Trim()
    if Left(url, 4) <> "http" then url = "http://" + url
    if CreateObject("roRegex", ":\d+/?$", "").IsMatch(url) = false then url = url + ":8096"
    useServer(url)
end sub

' --- Quick Connect ------------------------------------------------------------------------

sub useServer(url as string)
    m.login = invalid
    m.pending = { url: url }
    setStatus("Connecting to " + url + "…")
    http(url + "/System/Info/Public", "GET", invalid, "onServerInfo")
end sub

sub onServerInfo(res as object, ctx as dynamic)
    if res.code <> 200 or res.json = invalid
        setStatus("No Jellyfin server answered at " + m.pending.url + ".")
        return
    end if
    m.pending.id = res.json["Id"]
    m.pending.name = res.json["ServerName"]
    http(m.pending.url + "/QuickConnect/Initiate", "POST", "", "onInitiate")
end sub

sub onInitiate(res as object, ctx as dynamic)
    if res.code <> 200 or res.json = invalid
        setStatus("Quick Connect isn't available on " + m.pending.name + " (an admin can turn it on in Dashboard → General).")
        return
    end if
    m.pending.secret = res.json["Secret"]
    show("code")
    m.top.findNode("codeTitle").text = "Sign in to " + m.pending.name
    m.top.findNode("codeValue").text = res.json["Code"]
    m.pollTimer.control = "start"
end sub

sub onPollQuickConnect()
    if m.pin <> invalid
        pollPlexPin()
        return
    end if
    if m.pending = invalid or m.pending.secret = invalid then return
    http(m.pending.url + "/QuickConnect/Connect?secret=" + m.pending.secret, "GET", invalid, "onCheck")
end sub

sub onCheck(res as object, ctx as dynamic)
    if res.json = invalid or res.json["Authenticated"] <> true then return
    m.pollTimer.control = "stop"
    body = FormatJson({ "Secret": m.pending.secret })
    http(m.pending.url + "/Users/AuthenticateWithQuickConnect", "POST", body, "onAuthenticated")
end sub

sub onAuthenticated(res as object, ctx as dynamic)
    if res.code <> 200 or res.json = invalid
        setStatus("Sign-in failed (HTTP " + res.code.ToStr() + ").")
        return
    end if
    m.login = { url: m.pending.url, name: m.pending.name, serverId: m.pending.id, token: res.json["AccessToken"], userId: res.json["User"]["Id"] }
    m.registry.Write("login", FormatJson(m.login))
    m.registry.Flush()
    loadBrowse()
end sub

' --- browsing -----------------------------------------------------------------------------

' Menus come from the Jellyfin server itself (the MenuStream plugin, spec/server-api.md).
sub loadBrowse()
    show("browse")
    m.top.findNode("browseStatus").text = "Loading from " + m.login.name + "…"
    if isPlex()
        plexLoadMenus()
        return
    end if
    http(m.login.url + "/MenuStream/v1/health", "GET", invalid, "onHealth")
end sub

sub onHealth(res as object, ctx as dynamic)
    if res.code = 401
        signOut()
        return
    end if
    if res.code <> 200
        ' No plugin: no disc menus, but the library still browses and plays.
        m.menuBase = invalid
        onIndex({ code: 200, json: { manifests: [] } }, invalid)
        return
    end if
    m.menuBase = m.login.url + "/MenuStream"
    http(m.menuBase + "/v1/index", "GET", invalid, "onIndex")
end sub

sub onIndex(res as object, ctx as dynamic)
    if res.code = 401
        if isPlex() then setStatus("The menu server didn't accept this Plex sign-in.") else signOut()
        return
    end if
    if res.json = invalid
        if isPlex()
            ' The saved server may have moved (a new IP): look again once, then ask.
            if m.searchedAssetServers <> true
                findAssetServers()
            else
                setStatus("No menu server answered at " + m.menuBase + ".")
                askAssetServer()
            end if
        end if
        return
    end if
    m.catalog = []
    for each e in res.json["manifests"]
        if e["valid"] = true and e["hasMenus"] = true then m.catalog.push(e)
    end for
    if isPlex()
        plexLibraries()
        return
    end if
    resetLibraries()
    http(m.login.url + "/UserViews?userId=" + m.login.userId, "GET", invalid, "onViews")
end sub

sub onViews(res as object, ctx as dynamic)
    if res.json = invalid then return
    m.moviesPending = 0
    for each view in res.json["Items"]
        items = m.login.url + "/Items?userId=" + m.login.userId + "&parentId=" + view["Id"] + "&recursive=true&sortBy=SortName"
        if view["CollectionType"] = "movies"
            lib = addLibrary(view["Name"], "movie")
            m.moviesPending = m.moviesPending + 1
            http(items + "&includeItemTypes=Movie&fields=Path,MediaSources&limit=2000", "GET", invalid, "onMovies", lib)
        else if view["CollectionType"] = "tvshows"
            lib = addLibrary(view["Name"], "show")
            m.moviesPending = m.moviesPending + 2
            http(items + "&includeItemTypes=Series&limit=2000", "GET", invalid, "onShowList", lib)
            ' TV discs reference episode files: fetch episodes (with paths) once, match locally.
            http(items + "&includeItemTypes=Episode&fields=Path,MediaSources&limit=5000", "GET", invalid, "onEpisodes", lib)
        end if
    end for
    if m.moviesPending = 0 then showRows()
end sub

' Match every movie's files against the manifests locally (one request per library page).
' Episodes of a TV library: their files let TV disc menus play episodes, and the first matching
' episode's series gives the disc its poster.
sub onEpisodes(res as object, ctx as dynamic)
    m.moviesPending = m.moviesPending - 1
    if res.json <> invalid
        for each ep in itemsOf(res.json)
            addEpisode(ep)
            for each f in filesOf(ep)
                m.episodeFiles.push(f)
                for each entry in msManifestsFor(f.path, m.catalog)
                    if not m.shows.DoesExist(entry["id"]) then m.shows[entry["id"]] = ep
                end for
            end for
        end for
    end if
    if m.moviesPending <= 0 then showRows()
end sub

sub onMovies(res as object, ctx as dynamic)
    m.moviesPending = m.moviesPending - 1
    if res.json <> invalid
        for each movie in itemsOf(res.json)
            if ctx <> invalid then m.libraries[ctx].items.push(movie)
            for each f in filesOf(movie)
                for each entry in msManifestsFor(f.path, m.catalog)
                    if not m.found.DoesExist(entry["id"]) then m.found[entry["id"]] = movie
                    m.menuOf[itemKey(movie)] = entry["id"]
                end for
            end for
        end for
    end if
    if m.moviesPending <= 0 then showRows()
end sub

' The items of a list response: Jellyfin's Items, or Plex's MediaContainer.Metadata.
function itemsOf(json as dynamic) as object
    if json = invalid then return []
    if isPlex()
        if json["MediaContainer"] = invalid then return []
        return msOr(json["MediaContainer"]["Metadata"], [])
    end if
    if GetInterface(json, "ifArray") <> invalid then return json
    if json["Items"] <> invalid then return json["Items"]
    return [json]
end function

' Every file of an item: [{ path, item, source }] (each version is a media source).
function filesOf(item as object) as object
    if isPlex() then return plexFilesOf(item)
    files = []
    if item["MediaSources"] <> invalid
        for each src in item["MediaSources"]
            if src["Path"] <> invalid then files.push({ path: src["Path"], item: item, source: src["Id"] })
        end for
    end if
    if files.Count() = 0 and item["Path"] <> invalid then files.push({ path: item["Path"], item: item, source: item["Id"] })
    return files
end function

sub showRows()
    root = CreateObject("roSGNode", "ContentNode")
    if m.catalog.Count() > 0
        row = root.createChild("ContentNode")
        row.title = "Disc Menus"
    end if
    for each entry in m.catalog
        tile = row.createChild("ContentNode")
        tile.addFields({ kind: "disc", entryId: entry["id"], badge: true })
        title = entry["title"]
        tile.title = title["name"]
        movie = m.found[entry["id"]]
        if movie <> invalid and isPlex()
            tile.HDPosterUrl = plexImage(movie["thumb"])
            if movie["year"] <> invalid then tile.shortDescriptionLine2 = movie["year"].ToStr()
        else if m.shows[entry["id"]] <> invalid and isPlex()
            ep = m.shows[entry["id"]]
            tile.HDPosterUrl = plexImage(ep["grandparentThumb"])
            tile.shortDescriptionLine2 = msOr(ep["grandparentTitle"], "")
        else if movie <> invalid
            tile.HDPosterUrl = m.login.url + "/Items/" + movie["Id"] + "/Images/Primary?fillWidth=264&fillHeight=396&quality=90"
            if movie["ProductionYear"] <> invalid then tile.shortDescriptionLine2 = movie["ProductionYear"].ToStr()
        else if m.shows[entry["id"]] <> invalid and m.shows[entry["id"]]["SeriesId"] <> invalid
            ep = m.shows[entry["id"]]
            tile.HDPosterUrl = m.login.url + "/Items/" + ep["SeriesId"] + "/Images/Primary?fillWidth=264&fillHeight=396&quality=90"
            tile.shortDescriptionLine2 = msOr(ep["SeriesName"], "")
        else if title["year"] <> invalid
            tile.shortDescriptionLine2 = title["year"].ToStr()
        end if
    end for
    addLibraryRows(root)
    m.rows.content = root
    if m.menuBase = invalid and isPlex()
        m.top.findNode("browseStatus").text = m.login.name + " · no disc menus (no menu server)"
    else if m.menuBase = invalid
        m.top.findNode("browseStatus").text = m.login.name + " · no disc menus (the MenuStream plugin isn't installed)"
    else if isPlex()
        m.top.findNode("browseStatus").text = "Menus from " + m.menuBase + " · " + m.catalog.Count().ToStr() + " available · " + m.login.name
    else
        m.top.findNode("browseStatus").text = "Menus from " + m.login.name + " (built in) · " + m.catalog.Count().ToStr() + " available"
    end if
    m.rows.setFocus(true)
end sub

sub onTileChosen()
    sel = m.rows.rowItemSelected
    chooseTile(m.rows.content.getChild(sel[0]).getChild(sel[1]), "browse", m.rows)
end sub

' Open a disc menu, play a title that has none, or open a show.
sub chooseTile(tile as object, screen as string, list as object)
    if tile.kind = "show"
        openShow(tile)
        return
    end if
    if tile.kind = "episode" ' a TV disc's menu opens from the show's Discs row
        playDirect(tileItem(tile), tile, screen, list)
        return
    end if
    if tile.kind = "movie"
        item = tileItem(tile)
        if m.menuOf[itemKey(item)] = invalid
            playDirect(item, tile, screen, list)
            return
        end if
        tile.entryId = m.menuOf[itemKey(item)]
    end if
    m.menuFrom = { screen: screen, list: list }
    entry = invalid
    for each e in m.catalog
        if e["id"] = tile.entryId then entry = e
    end for
    if entry = invalid then return
    setStatus("Opening " + tile.title + "…")
    ' Fetch the manifest, then the title's files (film + extras) so menu buttons can play them.
    m.opening = { entry: entry, movie: m.found[entry["id"]], files: [], waiting: 1 }
    http(m.menuBase + entry["url"], "GET", invalid, "onManifest")
    movie = m.opening.movie
    if movie <> invalid and isPlex()
        m.opening.waiting = 3
        base = m.login.url + "/library/metadata/" + movie["ratingKey"]
        http(base, "GET", invalid, "onTitleFiles")
        http(base + "/extras", "GET", invalid, "onTitleFiles")
    else if movie <> invalid
        m.opening.waiting = 4
        base = m.login.url + "/Items/" + movie["Id"]
        http(base + "?userId=" + m.login.userId, "GET", invalid, "onTitleFiles")
        http(base + "/SpecialFeatures?userId=" + m.login.userId, "GET", invalid, "onTitleFiles")
        http(base + "/LocalTrailers?userId=" + m.login.userId, "GET", invalid, "onTitleFiles")
    end if
end sub

sub onManifest(res as object, ctx as dynamic)
    if res.json = invalid
        setStatus("Couldn't load the menu (HTTP " + res.code.ToStr() + ").")
        m.opening = invalid
        return
    end if
    m.opening.manifest = res.json
    openWhenReady()
end sub

sub onTitleFiles(res as object, ctx as dynamic)
    if m.opening = invalid then return
    for each item in itemsOf(res.json)
        m.opening.files.Append(filesOf(item))
    end for
    openWhenReady()
end sub

sub openWhenReady()
    m.opening.waiting = m.opening.waiting - 1
    if m.opening.waiting > 0 or m.opening.manifest = invalid then return
    files = m.opening.files
    if m.opening.movie = invalid then files = m.episodeFiles ' a TV disc: its media are episodes
    m.menuMedia = msResolveMedia(m.opening.entry, files)
    m.menu = CreateObject("roSGNode", "MenuView")
    m.menu.manifest = m.opening.manifest
    m.menu.manifestUrl = m.menuBase + m.opening.entry["url"]
    m.menu.authHeaders = assetHeaders()
    m.menu.observeField("playRequest", "onPlayRequest")
    m.menu.observeField("exit", "onMenuExit")
    m.top.findNode(m.menuFrom.screen).visible = false
    setStatus("")
    m.direct = invalid
    m.top.appendChild(m.menu)
    m.menu.control = "start"
    m.opening = invalid
end sub

sub onMenuExit()
    m.top.removeChild(m.menu)
    m.menu = invalid
    show(m.menuFrom.screen)
    m.menuFrom.list.setFocus(true)
end sub

' --- playing titles ------------------------------------------------------------------------

' What this Roku can play, as a Jellyfin device profile, built from the TV's own answers
' (CanDecodeVideo/CanDecodeAudio): files that fit play directly; anything else Jellyfin transcodes
' to H.264 + AAC over HLS. A Roku that decodes HEVC direct-plays HEVC; one that can't gets a transcode.
function deviceProfile() as object
    di = CreateObject("roDeviceInfo")
    video = []
    for each c in [["mpeg4 avc", "h264"], ["hevc", "hevc"], ["mpeg2", "mpeg2video"], ["vp9", "vp9"], ["av1", "av1"]]
        if di.CanDecodeVideo({ Codec: c[0] }).Result = true then video.push(c[1])
    end for
    if video.Count() = 0 then video.push("h264")
    audio = []
    for each c in [["aac", "aac"], ["ac3", "ac3"], ["eac3", "eac3"], ["mp3", "mp3"], ["dts", "dts"], ["flac", "flac"]]
        if di.CanDecodeAudio({ Codec: c[0] }).Result = true then audio.push(c[1])
    end for
    if audio.Count() = 0 then audio.push("aac")
    ' The same lists decide direct play for Plex files (MainScene.plex.brs).
    m.decodable = { video: {}, audio: {} }
    for each c in video
        m.decodable.video[c] = true
    end for
    for each c in audio
        m.decodable.audio[c] = true
    end for
    v = video.Join(",")
    a = audio.Join(",")
    print "MenuStream: this Roku plays video "; v; "; audio "; a
    return {
        "MaxStreamingBitrate": 40000000,
        "DirectPlayProfiles": [
            { "Container": "mp4,m4v,mov", "Type": "Video", "VideoCodec": v, "AudioCodec": a },
            { "Container": "mkv", "Type": "Video", "VideoCodec": v, "AudioCodec": a },
            { "Container": "ts,mpegts", "Type": "Video", "VideoCodec": v, "AudioCodec": a }
        ],
        "TranscodingProfiles": [
            { "Container": "ts", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac", "Protocol": "hls", "Context": "Streaming", "MaxAudioChannels": "2", "MinSegments": 1, "BreakOnNonKeyFrames": false }
        ],
        "ContainerProfiles": [],
        "CodecProfiles": [],
        "SubtitleProfiles": [{ "Format": "srt", "Method": "External" }, { "Format": "vtt", "Method": "External" }]
    }
end function

' Jellyfin's index of the Nth (1-based) audio stream of a media source, for AudioStreamIndex.
function audioStreamIndex(item as object, sourceId as string, n as dynamic) as dynamic
    if n = invalid or item["MediaSources"] = invalid then return invalid
    for each src in item["MediaSources"]
        if src["Id"] = sourceId and src["MediaStreams"] <> invalid
            count = 0
            for each stream in src["MediaStreams"]
                if stream["Type"] = "Audio"
                    count = count + 1
                    if count = n then return stream["Index"]
                end if
            end for
        end if
    end for
    return invalid
end function

' The media source's default audio track index, else its first audio track's.
function defaultAudioIndex(item as object, sourceId as string) as dynamic
    if item["MediaSources"] = invalid then return invalid
    for each src in item["MediaSources"]
        if src["Id"] = sourceId
            if src["DefaultAudioStreamIndex"] <> invalid then return src["DefaultAudioStreamIndex"]
            if src["MediaStreams"] <> invalid
                for each stream in src["MediaStreams"]
                    if stream["Type"] = "Audio" then return stream["Index"]
                end for
            end if
        end if
    end for
    return invalid
end function

' Ask Jellyfin how to play this file here (PlaybackInfo with the device profile): direct play, or a
' transcode it prepares. The menu's soundtrack choice travels as AudioStreamIndex, so it survives
' transcoding; subtitles off (0) is SubtitleStreamIndex -1.
sub onPlayRequest()
    req = m.menu.playRequest
    target = m.menuMedia[req.media]
    if target = invalid
        setStatus("That title isn't in your library.")
        titleStopped(req.start)
        return
    end if
    startTitle(req, target)
end sub

' Play one file: req { start, audio?, subtitle? } from a menu button or a library tile.
sub startTitle(req as object, target as object)
    if isPlex()
        plexPlay(req, target)
        return
    end if
    m.playing = { req: req, item: target.item, source: target.source, path: target.path, session: invalid, method: "DirectPlay", started: false }
    body = { "UserId": m.login.userId, "MediaSourceId": target.source, "DeviceProfile": m.profile, "EnableDirectPlay": true, "EnableDirectStream": true, "EnableTranscoding": true, "AutoOpenLiveStream": true, "IsPlayback": true }
    ' Always name the audio track that will play (the menu's choice, else the file's default, else
    ' its first). Jellyfin only checks an explicitly requested track against the device profile;
    ' left to choose, it narrows candidates to "default"-flagged tracks, and with none (the example
    ' Finale's DTS track has no default flag) it skips the audio check and approves direct play of
    ' audio this TV can't decode (StreamBuilder.cs, Jellyfin 12.1, "Collect candidate audio streams").
    audio = audioStreamIndex(target.item, target.source, req.audio)
    if audio = invalid then audio = defaultAudioIndex(target.item, target.source)
    if audio <> invalid then body["AudioStreamIndex"] = audio
    if req.subtitle = 0 then body["SubtitleStreamIndex"] = -1
    http(m.login.url + "/Items/" + target.item["Id"] + "/PlaybackInfo?userId=" + m.login.userId, "POST", FormatJson(body), "onPlaybackInfo")
end sub

sub onPlaybackInfo(res as object, ctx as dynamic)
    if m.playing = invalid then return
    req = m.playing.req
    source = invalid
    if res.json <> invalid and res.json["MediaSources"] <> invalid then source = res.json["MediaSources"][0]
    if source = invalid
        setStatus("Jellyfin couldn't prepare this title (HTTP " + res.code.ToStr() + ").")
        titleStopped(req.start)
        return
    end if
    print "MenuStream: PlaybackInfo directPlay="; source["SupportsDirectPlay"]; " directStream="; source["SupportsDirectStream"]; " transcode="; source["SupportsTranscoding"]; " url="; source["TranscodingUrl"] <> invalid; " reasons="; FormatJson(msOr(source["TranscodeReasons"], [])); " container="; source["Container"]
    m.playing.session = msOr(res.json["PlaySessionId"], CreateObject("roDeviceInfo").GetRandomUUID().Replace("-", ""))
    content = CreateObject("roSGNode", "ContentNode")
    if source["TranscodingUrl"] <> invalid and source["SupportsDirectPlay"] <> true
        m.playing.method = "Transcode"
        content.url = m.login.url + source["TranscodingUrl"]
        content.StreamFormat = "hls"
        print "MenuStream: transcoding ("; msOr(source["TranscodeReasons"], []).Join(", "); ")"
    else
        content.url = m.login.url + "/Videos/" + m.playing.item["Id"] + "/stream?static=true&mediaSourceId=" + m.playing.source + "&playSessionId=" + m.playing.session
        content.StreamFormat = streamFormat(m.playing.path)
    end if
    content.HttpHeaders = ["Authorization:" + authHeader(m.login.token)]
    content.PlayStart = req.start
    content.title = m.playing.item["Name"]
    openPlayer(content)
end sub

' Full-screen playback of a title, over the menu.
sub openPlayer(content as object)
    m.player = CreateObject("roSGNode", "Video")
    m.player.width = 1920
    m.player.height = 1080
    m.player.observeField("state", "onPlayerState")
    m.player.observeField("availableAudioTracks", "onAudioTracks")
    m.player.content = content
    m.top.appendChild(m.player)
    m.player.setFocus(true)
    m.player.control = "play"
end sub

function streamFormat(path as string) as string
    ext = LCase(path.Split(".").Peek())
    if ext = "mkv" then return "mkv"
    if ext = "m4v" or ext = "mov" then return "mp4"
    if ext = "ts" or ext = "m2ts" then return "ts"
    return "mp4"
end function

' Apply the menu's soundtrack choice (e.g. commentary) once the file's tracks are known.
sub onAudioTracks()
    if m.playing = invalid or m.playing.req.audio = invalid then return
    if m.playing.method <> "DirectPlay" then return ' a transcode already carries only the chosen track
    tracks = m.player.availableAudioTracks
    n = m.playing.req.audio
    if tracks <> invalid and n >= 1 and n <= tracks.Count() then m.player.audioTrack = tracks[n - 1].Track
end sub

sub onPlayerState()
    if m.player = invalid then return
    state = m.player.state
    if state = "playing" and not m.playing.started
        m.playing.started = true
        report("/Sessions/Playing")
        m.progressTimer.control = "start"
    else if (state = "paused" or state = "playing") and m.playing.started
        reportProgress()
    else if state = "finished"
        report("/Sessions/Playing/Stopped")
        m.playing = invalid
        closePlayer()
        titleEnded()
    else if state = "error"
        print "MenuStream: playback error "; m.player.errorCode; " msg "; m.player.errorMsg; " info "; FormatJson(m.player.errorInfo)
        setStatus("Couldn't play this title: " + m.player.errorMsg)
        closePlayer()
        titleStopped(m.playing.req.start)
    end if
end sub

sub stopTitle()
    position = playerPosition()
    report("/Sessions/Playing/Stopped")
    closePlayer()
    titleStopped(position)
end sub

' Playback is over: back to the menu that asked for it, or to the tile that was picked (whose
' resume point and watched mark are updated here, as the server now has them).
sub titleStopped(position as float)
    if m.menu <> invalid
        m.menu.playbackStopped = position
    else
        backFromTitle(position, false)
    end if
end sub

sub titleEnded()
    if m.menu <> invalid
        m.menu.playbackEnded = true
    else
        backFromTitle(0, true)
    end if
end sub

sub closePlayer()
    m.progressTimer.control = "stop"
    m.player.control = "stop"
    m.top.removeChild(m.player)
    m.player = invalid
end sub

' Tell Jellyfin, so resume points and "continue watching" match its other apps.
sub report(path as string)
    if m.playing = invalid then return
    paused = m.player <> invalid and m.player.state = "paused"
    if isPlex()
        if path = "/Sessions/Playing/Stopped"
            plexReport("stopped")
        else if paused
            plexReport("paused")
        else
            plexReport("playing")
        end if
        return
    end if
    position = playerPosition()
    body = FormatJson({ "ItemId": m.playing.item["Id"], "MediaSourceId": m.playing.source, "PlaySessionId": m.playing.session, "PositionTicks": Int(position * 10000000), "PlayMethod": m.playing.method, "CanSeek": true, "IsPaused": paused })
    http(m.login.url + path, "POST", body, "onReported")
end sub

'  Where playback is in the file (seconds). Right after a chapter start the player still says 0
' until its seek lands, so until then it's where playback was asked to start.
function playerPosition() as float
    if m.player = invalid or m.playing = invalid then return 0
    offset = 0
    if m.playing.offset <> invalid then offset = m.playing.offset
    if m.player.position = 0 and offset = 0 then return m.playing.req.start
    return offset + m.player.position
end function

sub reportProgress()
    if m.playing <> invalid and m.player <> invalid then report("/Sessions/Playing/Progress")
end sub

sub onReported(res as object, ctx as dynamic)
end sub

sub signOut()
    m.registry.Delete("login")
    m.registry.Flush()
    m.login = invalid
    chooseKind()
end sub

function onKeyEvent(key as string, press as boolean) as boolean
    if not press then return false
    if key = "back" and m.player <> invalid
        stopTitle()
        return true
    end if
    if key = "back" and m.top.findNode("code").visible
        m.pollTimer.control = "stop"
        m.pin = invalid
        chooseKind()
        return true
    end if
    if key = "back" and m.top.findNode("servers").visible
        chooseKind()
        return true
    end if
    if key = "back" and m.top.findNode("show").visible
        show("browse")
        m.rows.setFocus(true)
        return true
    end if
    if key = "options" and m.top.findNode("browse").visible
        signOut() ' the * button: switch server
        return true
    end if
    return false
end function
