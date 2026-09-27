' MenuStream for Roku: the Plex parts of the app flow (see MainScene.brs for the shared parts).
' Plex has no MenuStream plugin, so menus come from a MenuStream asset server (spec/server-api.md),
' which checks the viewer's Plex sign-in.

' --- requests -------------------------------------------------------------------------------

function plexHeaders(token as dynamic) as object
    h = {
        "Accept": "application/json",
        "X-Plex-Product": "MenuStream",
        "X-Plex-Version": "0.1",
        "X-Plex-Client-Identifier": m.deviceId,
        "X-Plex-Platform": "Roku",
        "X-Plex-Device": "Roku",
        "X-Plex-Device-Name": "MenuStream (Roku)"
    }
    if token <> invalid then h["X-Plex-Token"] = token
    return h
end function

' --- sign-in: plex.tv PIN, then the account's servers -----------------------------------------

sub plexSignIn()
    show("code")
    m.top.findNode("codeTitle").text = "Sign in with Plex"
    m.top.findNode("codeHint").text = "On your phone or computer, go to plex.tv/link and enter"
    m.top.findNode("codeValue").text = ""
    m.plexAccount = invalid
    http("https://plex.tv/api/v2/pins", "POST", "", "onPlexPin", invalid, { headers: plexHeaders(invalid) })
end sub

sub onPlexPin(res as object, ctx as dynamic)
    if res.json = invalid or res.json["code"] = invalid
        setStatus("Couldn't reach plex.tv (" + msOr(res.error, "HTTP " + res.code.ToStr()) + ").")
        return
    end if
    m.pin = { id: res.json["id"], code: res.json["code"] }
    m.top.findNode("codeValue").text = res.json["code"]
    m.pollTimer.control = "start"
end sub

sub pollPlexPin()
    if m.pin = invalid then return
    http("https://plex.tv/api/v2/pins/" + m.pin.id.ToStr(), "GET", invalid, "onPlexPinCheck", invalid, { headers: plexHeaders(invalid) })
end sub

sub onPlexPinCheck(res as object, ctx as dynamic)
    if res.json = invalid then return
    token = res.json["authToken"]
    if token = invalid or token = "" then return
    m.pollTimer.control = "stop"
    m.pin = invalid
    m.plexAccount = token
    setStatus("Signed in. Finding your Plex servers…")
    http("https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=1", "GET", invalid, "onPlexResources", invalid, { headers: plexHeaders(token) })
end sub

sub onPlexResources(res as object, ctx as dynamic)
    if res.code = 401
        plexSignIn()
        return
    end if
    if res.json = invalid
        setStatus("Couldn't list your Plex servers (HTTP " + res.code.ToStr() + ").")
        return
    end if
    m.plexServers = []
    for each r in res.json
        if r["provides"] <> invalid and Instr(1, r["provides"], "server") > 0 and r["accessToken"] <> invalid then m.plexServers.push(r)
    end for
    if m.plexServers.Count() = 0
        setStatus("No Plex servers on this account.")
    else if m.plexServers.Count() = 1
        plexConnect(m.plexServers[0])
    else
        show("servers")
        m.top.findNode("serversHint").text = "Choose your Plex server"
        content = CreateObject("roSGNode", "ContentNode")
        for each s in m.plexServers
            content.createChild("ContentNode").title = s["name"]
        end for
        m.serverList.content = content
        m.serverList.setFocus(true)
    end if
end sub

' Try the server's addresses in the order other Plex clients use (local before remote before relay,
' https before http) and keep the first that answers /identity.
sub plexConnect(server as object)
    list = []
    for each c in msOr(server["connections"], [])
        rank = 0
        if c["local"] <> true then rank = rank + 4
        if c["relay"] = true then rank = rank + 8
        if c["protocol"] <> "https" then rank = rank + 1
        if c["IPv6"] = true then rank = rank + 2
        list.push({ uri: c["uri"], rank: rank })
    end for
    list.SortBy("rank")
    m.connecting = { server: server, candidates: list, i: -1 }
    setStatus("Connecting to " + server["name"] + "…")
    plexTryNext()
end sub

sub plexTryNext()
    c = m.connecting
    c.i = c.i + 1
    if c.i >= c.candidates.Count()
        setStatus("Couldn't reach " + c.server["name"] + " at any of its " + c.candidates.Count().ToStr() + " addresses.")
        return
    end if
    http(c.candidates[c.i].uri + "/identity", "GET", invalid, "onPlexIdentity", invalid, { headers: plexHeaders(c.server["accessToken"]), timeoutMs: 4000 })
end sub

sub onPlexIdentity(res as object, ctx as dynamic)
    c = m.connecting
    if res.code <> 200
        plexTryNext()
        return
    end if
    m.login = { kind: "plex", name: c.server["name"], url: c.candidates[c.i].uri, token: c.server["accessToken"], machineId: c.server["clientIdentifier"] }
    m.registry.Write("login", FormatJson(m.login))
    m.registry.Flush()
    m.connecting = invalid
    loadBrowse()
end sub

' --- menus: the asset server ----------------------------------------------------------------

' The saved asset server; else (or when it stops answering) look for one with mDNS; else ask.
sub plexLoadMenus()
    saved = m.registry.Read("assetServer")
    if saved <> ""
        m.menuBase = saved
        http(m.menuBase + "/v1/index", "GET", invalid, "onIndex", invalid, { headers: plexHeaders(m.login.token), timeoutMs: 6000 })
    else
        findAssetServers()
    end if
end sub

sub findAssetServers()
    m.searchedAssetServers = true
    setStatus("Looking for a MenuStream menu server...")
    m.mdns = CreateObject("roSGNode", "MdnsTask")
    m.mdns.observeField("servers", "onAssetServersFound")
    m.mdns.control = "RUN"
end sub

sub onAssetServersFound()
    servers = m.mdns.servers
    m.mdns = invalid
    setStatus("")
    if servers.Count() = 1
        useAssetServer(servers[0].url)
    else if servers.Count() > 1
        m.assetServers = servers
        dialog = CreateObject("roSGNode", "StandardMessageDialog")
        dialog.title = "Choose a MenuStream menu server"
        buttons = []
        for each s in servers
            buttons.Push(s.name + "  (" + Mid(s.url, 8) + ")")
        end for
        buttons.Push("Enter an address")
        dialog.buttons = buttons
        dialog.observeField("buttonSelected", "onAssetServerChosen")
        m.top.dialog = dialog
    else
        askAssetServer()
    end if
end sub

sub onAssetServerChosen()
    i = m.top.dialog.buttonSelected
    m.top.dialog.close = true
    if i < m.assetServers.Count() then useAssetServer(m.assetServers[i].url) else askAssetServer()
end sub

sub useAssetServer(url as string)
    m.registry.Write("assetServer", url)
    m.registry.Flush()
    plexLoadMenus()
end sub

sub askAssetServer()
    dialog = CreateObject("roSGNode", "StandardKeyboardDialog")
    dialog.title = "MenuStream menu server address"
    dialog.message = ["The computer running the MenuStream asset server, e.g. 192.168.1.20:8790"]
    dialog.buttons = ["Connect", "Titles without menus"]
    dialog.observeField("buttonSelected", "onAssetServerEntered")
    m.top.dialog = dialog
end sub

sub onAssetServerEntered()
    dialog = m.top.dialog
    text = dialog.text.Trim()
    ok = dialog.buttonSelected = 0
    dialog.close = true
    if not ok or text = ""
        m.catalog = []
        plexLibraries()
        return
    end if
    if Left(text, 4) <> "http" then text = "http://" + text
    if CreateObject("roRegex", ":\d+/?$", "").IsMatch(text) = false then text = text + ":8790"
    useAssetServer(text)
end sub

' --- browsing ---------------------------------------------------------------------------------

sub plexLibraries()
    m.found = {}
    m.shows = {}
    m.episodeFiles = []
    http(m.login.url + "/library/sections", "GET", invalid, "onPlexSections")
end sub

sub onPlexSections(res as object, ctx as dynamic)
    if res.code = 401
        signOut()
        return
    end if
    m.moviesPending = 0
    dirs = invalid
    if res.json <> invalid then dirs = res.json["MediaContainer"]["Directory"]
    for each d in msOr(dirs, [])
        page = "&X-Plex-Container-Start=0&X-Plex-Container-Size=2000"
        if d["type"] = "movie"
            m.moviesPending = m.moviesPending + 1
            http(m.login.url + "/library/sections/" + d["key"] + "/all?type=1" + page, "GET", invalid, "onMovies")
        else if d["type"] = "show"
            m.moviesPending = m.moviesPending + 1
            http(m.login.url + "/library/sections/" + d["key"] + "/all?type=4" + page, "GET", invalid, "onEpisodes")
        end if
    end for
    if m.moviesPending = 0 then showRows()
end sub

' Every file of a Plex item: [{ path, item, source ("mediaIndex:partIndex:partKey"), partId }].
function plexFilesOf(item as object) as object
    files = []
    mi = 0
    for each media in msOr(item["Media"], [])
        pi = 0
        for each part in msOr(media["Part"], [])
            if part["file"] <> invalid then files.push({ path: part["file"], item: item, source: mi.ToStr() + ":" + pi.ToStr() + ":" + part["key"], partId: part["id"], media: media, part: part })
            pi = pi + 1
        end for
        mi = mi + 1
    end for
    return files
end function

' Poster URL for a Plex image path, via Plex's image transcoder. Posters can't send headers, so the
' token travels in this URL (Plex's own clients do the same for images).
function plexImage(path as dynamic) as dynamic
    if path = invalid then return invalid
    return m.login.url + "/photo/:/transcode?width=264&height=396&minSize=1&upscale=1&url=" + percentEncodeQuery(path) + "&X-Plex-Token=" + m.login.token
end function

function percentEncodeQuery(s as string) as string
    out = ""
    for i = 1 to Len(s)
        c = Mid(s, i, 1)
        a = Asc(c)
        if (a >= 48 and a <= 57) or (a >= 65 and a <= 90) or (a >= 97 and a <= 122) or c = "-" or c = "_" or c = "." or c = "~"
            out = out + c
        else
            out = out + "%" + Right("0" + UCase(StrI(a, 16)), 2)
        end if
    end for
    return out
end function

' --- playback -----------------------------------------------------------------------------------

' Play a Plex file. The app decides direct play from the file's codecs and this TV's own answers
' (the same lists as the Jellyfin device profile); Plex still gets its direct-play decision call,
' which PMS 1.43 requires before streaming a part. Anything the TV can't decode is transcoded by
' Plex to H.264 + AAC over HLS. The menu's soundtrack choice is set on the part first (as Plex's
' own apps do), so it applies to direct play and transcode alike.
sub plexPlay(req as object, target as object)
    parts = target.source.Split(":")
    m.playing = { kind: "plex", req: req, item: target.item, source: target.source, path: target.path, partKey: target.part["key"], mediaIndex: parts[0], partIndex: parts[1], session: CreateObject("roDeviceInfo").GetRandomUUID(), method: "DirectPlay", started: false, offset: 0 }
    audio = plexAudioStream(target.part, req.audio)
    m.playing.audioCodec = msOr(target.media["audioCodec"], "")
    if audio <> invalid
        m.playing.audioCodec = msOr(audio["codec"], m.playing.audioCodec)
        http(m.login.url + "/library/parts/" + target.partId.ToStr() + "?audioStreamID=" + audio["id"].ToStr() + "&allParts=1", "PUT", "", "onPlexAudioSet")
    else
        plexDecide()
    end if
end sub

' The Nth (1-based) audio stream of a part, when the item came with its streams.
function plexAudioStream(part as object, n as dynamic) as dynamic
    if n = invalid or part["Stream"] = invalid then return invalid
    count = 0
    for each s in part["Stream"]
        if s["streamType"] = 2
            count = count + 1
            if count = n then return s
        end if
    end for
    return invalid
end function

sub onPlexAudioSet(res as object, ctx as dynamic)
    plexDecide()
end sub

function plexPlayParams() as string
    p = m.playing
    return "path=" + percentEncodeQuery("/library/metadata/" + p.item["ratingKey"]) + "&mediaIndex=" + p.mediaIndex + "&partIndex=" + p.partIndex + "&protocol=hls&location=lan&session=" + p.session + "&X-Plex-Session-Identifier=" + p.session
end function

sub plexDecide()
    p = m.playing
    media = invalid
    for each f in plexFilesOf(p.item)
        if f.source = p.source then media = f.media
    end for
    container = LCase(msOr(media["container"], ""))
    video = LCase(msOr(media["videoCodec"], ""))
    audio = LCase(p.audioCodec)
    fits = m.decodable.video.DoesExist(video) and m.decodable.audio.DoesExist(audio) and (container = "mp4" or container = "mkv" or container = "mov" or container = "mpegts")
    print "MenuStream: Plex file "; container; " / "; video; " / "; audio; " -> "; fits
    if fits
        http(m.login.url + "/video/:/transcode/universal/decision?" + plexPlayParams() + "&directPlay=1&directStream=1&directStreamAudio=1&hasMDE=1", "GET", invalid, "onPlexDecision")
    else
        plexTranscode()
    end if
end sub

sub onPlexDecision(res as object, ctx as dynamic)
    c = invalid
    if res.json <> invalid then c = res.json["MediaContainer"]
    ok = c <> invalid and (c["mdeDecisionCode"] = 1000 or c["directPlayDecisionCode"] = 1000)
    if ok
        startPlexPlayer()
    else
        print "MenuStream: Plex declined direct play: "; FormatJson(c)
        plexTranscode()
    end if
end sub

' A transcode also needs its decision first, on the same session and parameters. Without one, PMS
' only allows an "ad hoc" session while this client has no other transcode running, so a restart
' right after playback (whose transcode Plex keeps for a few minutes) got HTTP 400.
function plexTranscodeParams() as string
    return plexPlayParams() + "&directPlay=0&directStream=1&fastSeek=1&hasMDE=1&offset=" + Int(m.playing.req.start).ToStr() + "&videoCodec=h264&audioCodec=aac&maxVideoBitrate=20000"
end function

sub plexTranscode()
    m.playing.method = "Transcode"
    http(m.login.url + "/video/:/transcode/universal/decision?" + plexTranscodeParams(), "GET", invalid, "onPlexTranscodeDecision")
end sub

sub onPlexTranscodeDecision(res as object, ctx as dynamic)
    if res.code <> 200 then print "MenuStream: Plex transcode decision HTTP "; res.code
    startPlexPlayer()
end sub

sub startPlexPlayer()
    p = m.playing
    content = CreateObject("roSGNode", "ContentNode")
    h = plexHeaders(m.login.token)
    h["X-Plex-Session-Identifier"] = p.session
    lines = []
    for each k in h
        lines.push(k + ":" + h[k])
    end for
    content.HttpHeaders = lines
    if p.method = "Transcode"
        ' Plex starts the transcode at the offset; the stream's own clock then starts at 0.
        p.offset = p.req.start
        content.url = m.login.url + "/video/:/transcode/universal/start.m3u8?" + plexTranscodeParams() + "&X-Plex-Token=" + m.login.token
        content.StreamFormat = "hls"
    else
        content.url = m.login.url + p.partKey
        content.StreamFormat = streamFormat(p.path)
        content.PlayStart = p.req.start
    end if
    content.title = msOr(p.item["title"], "")
    openPlayer(content)
end sub

' Plex timeline: resume points and watched status match other Plex apps.
sub plexReport(state as string)
    p = m.playing
    if p = invalid then return
    position = playerPosition()
    duration = msOr(p.item["duration"], 0)
    url = m.login.url + "/:/timeline?ratingKey=" + p.item["ratingKey"] + "&key=" + percentEncodeQuery("/library/metadata/" + p.item["ratingKey"]) + "&state=" + state + "&time=" + Int(position * 1000).ToStr() + "&duration=" + duration.ToStr()
    extra = { headers: plexHeaders(m.login.token) }
    extra.headers["X-Plex-Session-Identifier"] = p.session
    http(url, "GET", invalid, "onReported", invalid, extra)
    if state = "stopped" and p.method = "Transcode"
        http(m.login.url + "/video/:/transcode/universal/stop?session=" + p.session, "GET", invalid, "onReported", invalid, extra)
    end if
end sub
