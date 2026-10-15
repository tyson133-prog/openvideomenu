' ovmplayer for Roku: the app flow. Choose Plex or Jellyfin, sign in (plex.tv PIN, or Jellyfin
' Quick Connect or username and password), list titles with menus, and open one in MenuView. Menus come from the
' Jellyfin Open Video Menu plugin, or for Plex from an Open Video Menu asset server. Plex specifics are in
' MainScene.plex.brs; m.login.kind says which server this is ("plex", else Jellyfin).

sub init()
    m.tasks = {}          ' running HTTP tasks by id, so they aren't garbage-collected
    m.taskSeq = 0
    m.deviceId = CreateObject("roDeviceInfo").GetChannelClientId()
    m.profile = deviceProfile()
    m.registry = CreateObject("roRegistrySection", "ovmplayer")
    m.catalog = []
    m.servers = []

    m.chooseList = m.top.findNode("chooseList")
    m.chooseList.observeField("itemSelected", "onKindChosen")
    m.serverList = m.top.findNode("serverList")
    m.serverList.observeField("itemSelected", "onServerChosen")
    m.top.findNode("codeOptions").observeField("itemSelected", "onCodeOption")
    m.rows = m.top.findNode("rows")
    m.rows.observeField("rowItemSelected", "onTileChosen")
    m.showRows = m.top.findNode("showRows")
    m.showRows.observeField("rowItemSelected", "onShowTileChosen")

    ' While a title plays, tell the server where we are, as its own apps do (~every 10 s).
    ' Level 3: the navigation timer (spec 6.7).
    m.navTimer = CreateObject("roSGNode", "Timer")
    m.navTimer.repeat = false
    m.navTimer.observeField("fire", "onNavTimer")
    ' Level 2: just before a hold's time, where the cue clock stands while it holds: a hundredth of a
    ' second, which single-precision numbers keep (a millionth would round away).
    m.holdBefore = 0.01
    ' Level 2: how long a frame is held (spec 6.3).
    m.holdTimer = CreateObject("roSGNode", "Timer")
    m.holdTimer.repeat = false
    m.holdTimer.observeField("fire", "onHoldTimer")
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

' First run (or after switching server): Plex, Jellyfin, or the public demo? For a viewer without a
' server signed in, this is the app's home page, so the launch is complete once it's on screen.
sub chooseKind()
    show("choose")
    content = CreateObject("roSGNode", "ContentNode")
    content.createChild("ContentNode").title = "Plex"
    content.createChild("ContentNode").title = "Jellyfin"
    content.createChild("ContentNode").title = "Try the demo"
    m.chooseList.content = content
    m.chooseList.setFocus(true)
    launchComplete()
end sub

sub onKindChosen()
    m.plexServers = invalid
    i = m.chooseList.itemSelected
    if i = 0
        plexSignIn()
    else if i = 1
        findServers()
    else
        tryDemo()
    end if
end sub

' --- the demo -------------------------------------------------------------------------------------
' The public demo server: Jellyfin with the Open Video Menu plugin and the example titles, signed in
' as the public demo account the website gives (demo / demo). Nothing to type.

function demoServer() as string
    return "https://demo.openvideomenu.org"
end function

sub tryDemo()
    m.login = invalid
    m.pending = { url: demoServer() }
    setStatus("Connecting to the demo…")
    http(m.pending.url + "/System/Info/Public", "GET", invalid, "onDemoInfo")
end sub

sub onDemoInfo(res as object, ctx as dynamic)
    if m.pending = invalid then return
    if res.code <> 200 or res.json = invalid
        m.deepLink = invalid
        setStatus("The demo didn't answer. Try again in a while.")
        return
    end if
    m.pending.id = res.json["Id"]
    m.pending.name = res.json["ServerName"]
    body = FormatJson({ "Username": "demo", "Pw": "demo" })
    http(m.pending.url + "/Users/AuthenticateByName", "POST", body, "onPasswordAuthenticated", invalid, { headers: headers(), timeoutMs: 30000 })
end sub

function isPlex() as boolean
    return m.login <> invalid and m.login.kind = "plex"
end function

' Headers proving to the menu server that this viewer may use it.
function assetHeaders() as object
    if isPlex() then return { "X-Plex-Token": m.login.token }
    return { "Authorization": authHeader(m.login.token) }
end function

' --- launch beacons (Roku certification 3.2) ------------------------------------------------
' Launch time runs until the home page is on screen: the library when signed in, else the first
' screen (Plex, Jellyfin or the demo). Questions asked before the library on a saved sign-in (the
' menu server) are dialogs, whose time the beacons leave out.

sub launchDialog()
    if m.launched = true or m.inLaunchDialog = true then return
    m.inLaunchDialog = true
    m.top.signalBeacon("AppDialogInitiate")
end sub

sub launchComplete()
    if m.launched = true then return
    if m.inLaunchDialog = true then m.top.signalBeacon("AppDialogComplete")
    m.inLaunchDialog = false
    m.launched = true
    m.top.signalBeacon("AppLaunchComplete")
end sub

' --- screens ------------------------------------------------------------------------------

sub show(name as string)
    for each id in ["choose", "servers", "code", "browse", "show"]
        m.top.findNode(id).visible = (id = name)
    end for
    m.top.findNode("codeOptions").visible = false
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
    h = "MediaBrowser Client=" + q + "ovmplayer" + q + ", Device=" + q + "Roku" + q + ", DeviceId=" + q + m.deviceId + q + ", Version=" + q + "1.0.0" + q
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
    if res.code >= 400
        detail = ""
        if GetInterface(pending.context, "ifString") <> invalid then detail = " " + pending.context
        print "ovmplayer: HTTP "; res.code; " for "; pending.handler; detail
    end if
    ' Only JSON is parsed: some servers answer errors with an HTML page.
    if res.body <> invalid
        first = Left(res.body.Trim(), 1)
        if first = "{" or first = "[" then data = ParseJson(res.body)
    end if
    res.json = data
    ' Handlers are called by name so each step of a flow reads top to bottom.
    if pending.handler = "onServerInfo" then onServerInfo(res, pending.context)
    if pending.handler = "onDemoInfo" then onDemoInfo(res, pending.context)
    if pending.handler = "onInitiate" then onInitiate(res, pending.context)
    if pending.handler = "onCheck" then onCheck(res, pending.context)
    if pending.handler = "onAuthenticated" then onAuthenticated(res, pending.context)
    if pending.handler = "onPasswordAuthenticated" then onPasswordAuthenticated(res, pending.context)
    if pending.handler = "onHealth" then onHealth(res, pending.context)
    if pending.handler = "onServerName" then onServerName(res, pending.context)
    if pending.handler = "onIndex" then onIndex(res, pending.context)
    if pending.handler = "onViews" then onViews(res, pending.context)
    if pending.handler = "onLibraryPage" then onLibraryPage(res, pending.context)
    if pending.handler = "onShowSearch" then onShowSearch(res, pending.context)
    if pending.handler = "onManifestEpisodes" then onManifestEpisodes(res, pending.context)
    if pending.handler = "onRecentEpisodes" then onRecentEpisodes(res, pending.context)
    if pending.handler = "onManifestShowFound" then onManifestShowFound(res, pending.context)
    if pending.handler = "onIndexFilm" then onIndexFilm(res, pending.context)
    if pending.handler = "onIndexShow" then onIndexShow(res, pending.context)
    if pending.handler = "onShowEpisodes" then onShowEpisodes(res, pending.context)
    if pending.handler = "onPlaybackInfo" then onPlaybackInfo(res, pending.context)
    if pending.handler = "onManifest" then onManifest(res, pending.context)
    if pending.handler = "onTitleFiles" then onTitleFiles(res, pending.context)
    if pending.handler = "onManifestRefresh" then onManifestRefresh(res, pending.context)
    if pending.handler = "onReported" then onReported(res, pending.context)
    if pending.handler = "onPlexPin" then onPlexPin(res, pending.context)
    if pending.handler = "onPlexPinCheck" then onPlexPinCheck(res, pending.context)
    if pending.handler = "onPlexResources" then onPlexResources(res, pending.context)
    if pending.handler = "onPlexIdentity" then onPlexIdentity(res, pending.context)
    if pending.handler = "onPlexSections" then onPlexSections(res, pending.context)
    if pending.handler = "onPlexAudioSet" then onPlexAudioSet(res, pending.context)
    if pending.handler = "onPlexDecision" then onPlexDecision(res, pending.context)
    if pending.handler = "onPlexTranscodeDecision" then onPlexTranscodeDecision(res, pending.context)
    if pending.handler = "onDeepLinkItem" then onDeepLinkItem(res, pending.context)
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
    ' A home server's address gets Jellyfin's port; an https one (a server published on the
    ' internet, e.g. https://demo.openvideomenu.org) is used as typed.
    if Right(url, 1) = "/" then url = Left(url, Len(url) - 1)
    https = LCase(Left(url, 6)) = "https:"
    if not https and CreateObject("roRegex", ":\d+$", "").IsMatch(url) = false then url = url + ":8096"
    useServer(url)
end sub

' --- signing in: Quick Connect, or username and password -----------------------------------

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
    ' Both ways at once: a Quick Connect code (once the server gives one), and username and password.
    show("code")
    m.top.findNode("codeTitle").text = "Sign in to " + m.pending.name
    m.top.findNode("codeHint").text = "Getting a Quick Connect code…"
    m.top.findNode("codeValue").text = ""
    options = CreateObject("roSGNode", "ContentNode")
    options.createChild("ContentNode").title = "Use username and password"
    list = m.top.findNode("codeOptions")
    list.content = options
    list.visible = true
    list.setFocus(true)
    http(m.pending.url + "/QuickConnect/Initiate", "POST", "", "onInitiate")
end sub

sub onInitiate(res as object, ctx as dynamic)
    if m.pending = invalid then return
    if res.code <> 200 or res.json = invalid
        m.top.findNode("codeHint").text = "Quick Connect is turned off on this server. Sign in with your username and password."
        return
    end if
    m.pending.secret = res.json["Secret"]
    m.top.findNode("codeHint").text = "In Jellyfin on your phone or computer, open your profile, choose Quick Connect, and enter"
    m.top.findNode("codeValue").text = res.json["Code"]
    m.pollTimer.control = "start"
end sub

' Roku requires its Request for Information screen in an app's sign-in (certification RP 2.1): it asks
' whether to share the viewer's Roku email. Shown once, before the username. The answer isn't used,
' kept or sent: the account is on the viewer's own server, so sign-in carries on either way.
sub onCodeOption()
    if m.askedUserData = true
        askSignIn("Username", false, "onUsernameEntered")
        return
    end if
    m.askedUserData = true
    m.store = CreateObject("roSGNode", "ChannelStore")
    m.store.requestedUserData = "email"
    m.store.observeField("userData", "onUserData")
    m.store.command = "getUserData"
end sub

sub onUserData()
    m.store.unobserveField("userData")
    m.store = invalid
    if m.pending <> invalid then askSignIn("Username", false, "onUsernameEntered")
end sub

' Roku's own keyboard for one sign-in field; the password is masked.
sub askSignIn(title as string, secret as boolean, handler as string)
    dialog = CreateObject("roSGNode", "StandardKeyboardDialog")
    dialog.title = title
    if m.pending <> invalid then dialog.message = ["Your account on " + m.pending.name]
    if secret
        dialog.keyboardDomain = "password"
        ' Masking is the text box's secureMode; the dialog holds it as textEditBox.
        editBox = invalid
        if dialog.hasField("textEditBox") then editBox = dialog.textEditBox
        if editBox <> invalid then editBox.secureMode = true
    end if
    dialog.buttons = ["OK", "Cancel"]
    dialog.observeField("buttonSelected", handler)
    m.top.dialog = dialog
end sub

sub onUsernameEntered()
    dialog = m.top.dialog
    text = dialog.text.Trim()
    ok = dialog.buttonSelected = 0
    dialog.close = true
    if not ok or m.pending = invalid then return
    if text = ""
        setStatus("Enter your username.")
        return
    end if
    m.pending.user = text
    askSignIn("Password for " + text, true, "onPasswordEntered")
end sub

' The password may be empty: Jellyfin allows accounts without one.
sub onPasswordEntered()
    dialog = m.top.dialog
    password = dialog.text
    ok = dialog.buttonSelected = 0
    dialog.close = true
    if not ok or m.pending = invalid then return
    setStatus("Signing in…")
    body = FormatJson({ "Username": m.pending.user, "Pw": password })
    http(m.pending.url + "/Users/AuthenticateByName", "POST", body, "onPasswordAuthenticated", invalid, { headers: headers(), timeoutMs: 30000 })
end sub

sub onPasswordAuthenticated(res as object, ctx as dynamic)
    if m.pending = invalid then return ' the viewer left the sign-in screen
    if res.code = 401
        setStatus("Wrong username or password.")
        m.top.findNode("codeOptions").setFocus(true)
        return
    end if
    m.pollTimer.control = "stop"
    onAuthenticated(res, ctx)
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
    m.pollTimer.control = "stop"
    m.login = { url: m.pending.url, name: m.pending.name, serverId: m.pending.id, token: res.json["AccessToken"], userId: res.json["User"]["Id"] }
    m.registry.Write("login", FormatJson(m.login))
    m.registry.Flush()
    loadBrowse()
end sub

' --- browsing -----------------------------------------------------------------------------

' Menus come from the Jellyfin server itself (the Open Video Menu plugin, spec/server-api.md).
sub loadBrowse()
    show("browse")
    m.top.findNode("browseStatus").text = "Loading from " + m.login.name + "…"
    if isPlex()
        plexLoadMenus()
        return
    end if
    ' The server's name can change after sign-in (it's a server setting): show the current one.
    http(m.login.url + "/System/Info/Public", "GET", invalid, "onServerName")
    http(m.login.url + "/OpenVideoMenu/v1/health", "GET", invalid, "onHealth")
end sub

sub onServerName(res as object, ctx as dynamic)
    if res.json = invalid or res.json["ServerName"] = invalid or m.login = invalid then return
    if m.login.name = res.json["ServerName"] then return
    m.login.name = res.json["ServerName"]
    m.registry.Write("login", FormatJson(m.login))
    m.registry.Flush()
    if m.libraries <> invalid then librariesProgress()
end sub

sub onHealth(res as object, ctx as dynamic)
    if res.code = 401
        signOut()
        return
    end if
    if res.code <> 200
        ' No plugin: no menus, but the library still browses and plays.
        m.menuBase = invalid
        onIndex({ code: 200, json: { manifests: [] } }, invalid)
        return
    end if
    m.menuBase = m.login.url + "/OpenVideoMenu"
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
    sortCatalog()
    if isPlex()
        plexLibraries()
        return
    end if
    resetLibraries()
    http(m.login.url + "/UserViews?userId=" + m.login.userId, "GET", invalid, "onViews")
end sub

sub onViews(res as object, ctx as dynamic)
    if res.json = invalid then return
    for each view in res.json["Items"]
        if view["CollectionType"] = "movies" then addLibrary(view["Name"], "movie", view["Id"])
        if view["CollectionType"] = "tvshows" then addLibrary(view["Name"], "show", view["Id"])
    end for
    startLibraries()
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

sub onTileChosen()
    sel = m.rows.rowItemSelected
    chooseTile(m.rows.content.getChild(sel[0]).getChild(sel[1]), "browse", m.rows)
end sub

' Open a menu, play a title that has none, or open a show.
sub chooseTile(tile as object, screen as string, list as object)
    if tile.kind = "loading" then return
    if tile.kind = "show"
        openShow(tile)
        return
    end if
    if tile.kind = "manifest" and not manifestReady(tile.entryId)
        setStatus("Still finding " + tile.title + " in your library…")
        return
    end if
    if tile.kind = "episode" ' a TV show's menu opens from the show's Menus row
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
    m.menuFrom = { screen: screen, list: list, tile: tile }
    entry = invalid
    for each e in m.catalog
        if e["id"] = tile.entryId then entry = e
    end for
    if entry = invalid then return
    opening = tile.title
    if placeOf(entry) <> "" then opening = menuName(entry)
    setStatus("Opening " + opening + "…")
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
    else if m.manifestShow[entry["id"]] <> invalid
        ' A TV show's menu: its episodes, fresh (resume points may have moved since they loaded).
        m.opening.waiting = 2
        series = m.manifestShow[entry["id"]]
        http(episodesUrl(series), "GET", invalid, "onManifestRefresh", series)
    end if
end sub

sub onManifestRefresh(res as object, series as dynamic)
    if res.json <> invalid then m.episodesByShow[itemKey(series)] = itemsOf(res.json)
    if m.opening <> invalid then openWhenReady()
end sub

sub onManifest(res as object, ctx as dynamic)
    if res.json = invalid
        setStatus("Couldn't load the menu (HTTP " + res.code.ToStr() + ").")
        m.opening = invalid
        return
    end if
    m.opening.manifest = res.json
    ' The manifest's version, so a kept session of an older one is discarded (spec 6.5.1).
    m.opening.stamp = manifestStamp(msOr(res.body, ""))
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
    if m.opening.movie = invalid then files = manifestEpisodeFiles(m.opening.entry["id"]) ' a TV show's menu: its media are episodes
    m.menuMedia = msResolveMedia(m.opening.entry, files)
    m.menuAngles = msResolveAngles(m.opening.entry, files)
    ' Stopped partway last time: ask first. Every title the menu plays that
    ' was stopped partway (the film, an extra, an episode), most recently played first; on a movie
    ' menu the film always leads the list ("Play …" when it wasn't started), so it's one press away.
    ' The menu's own Resume button gets the newest.
    m.opening.resumes = menuResumes(m.opening.manifest, m.menuMedia, m.opening.movie)
    m.opening.session = sessionRead(m.opening.entry["id"], m.opening.stamp)
    m.opening.lastStop = invalid
    newest = invalid
    for each r in m.opening.resumes
        if r.position > 0 and (newest = invalid or r.playedAt > newest.playedAt) then newest = r
    end for
    if newest <> invalid
        m.opening.lastStop = { media: newest.media, position: newest.position }
        askResume()
    else
        startMenu(menuSession(m.opening.session, invalid))
    end if
end sub

' The prompt's titles: [{ media, item, position, audio, subtitle, label, playedAt }]. Titles stopped
' partway, newest first; on a movie's menu the film first, with position 0 when it wasn't started.
' At most four. Empty when nothing was stopped partway.
function menuResumes(manifest as object, media as object, film as dynamic) as object
    seen = {}
    list = []
    filmEntry = invalid
    for each id in media
        item = media[id].item
        key = itemKey(item)
        if not seen.DoesExist(key)
            seen[key] = true
            label = invalid
            if manifest["media"] <> invalid and manifest["media"][id] <> invalid then label = manifest["media"][id]["label"]
            r = resumePointOf(item)
            r.media = id
            r.item = item
            r.label = msOr(label, titleOf(item))
            if film <> invalid and key = itemKey(film)
                filmEntry = r
            else if r.position > 0
                list.push(r)
            end if
        end if
    end for
    list.SortBy("playedAt", "r")
    if filmEntry <> invalid and (filmEntry.position > 0 or list.Count() > 0) then list.Unshift(filmEntry)
    while list.Count() > 4
        list.Pop()
    end while
    return list
end function

' Where to pick a title up, and with which tracks: this app's own record of where the viewer
' stopped (with the soundtrack and subtitles they had), or the server's resume point when that's
' newer (watched elsewhere; tracks then default to the first soundtrack, subtitles off).
function resumePointOf(item as object) as object
    mine = stopOf(item)
    if mine <> invalid and not changedElsewhere(item, mine)
        return { position: mine.position, audio: msOr(mine.audio, 1), subtitle: msOr(mine.subtitle, 0), playedAt: mine.at }
    end if
    return { position: resumeOf(item), audio: 1, subtitle: 0, playedAt: playedAtOf(item) }
end function

' Whether the server's state for the title comes from viewing somewhere else (another device or
' app) after this app last played it. Otherwise this app's record stands, even when the server
' reset its resume point because of a short play here.
function changedElsewhere(item as object, mine as object) as boolean
    return playedAtOf(item) > msOr(mine.touchedAt, mine.at) + 60
end function

sub askResume()
    resumes = m.opening.resumes
    dialog = CreateObject("roSGNode", "StandardMessageDialog")
    dialog.title = menuName(m.opening.entry)
    buttons = []
    if resumes.Count() = 1 and m.opening.movie <> invalid and itemKey(resumes[0].item) = itemKey(m.opening.movie)
        ' Just the film: "Resume from 1:02:13".
        dialog.message = ["You stopped at " + clockOf(resumes[0].position) + "."]
        buttons.push("Resume from " + clockOf(resumes[0].position))
    else
        dialog.message = ["Pick up where you left off:"]
        for each r in resumes
            if r.position > 0
                buttons.push("Resume " + r.label + " from " + clockOf(r.position))
            else
                buttons.push("Play " + r.label)
            end if
        end for
    end if
    buttons.push("Menu")
    dialog.buttons = buttons
    dialog.observeField("buttonSelected", "onResumeChosen")
    dialog.observeField("wasClosed", "onResumeClosed")
    m.top.dialog = dialog
end sub

sub onResumeChosen()
    choice = m.top.dialog.buttonSelected
    m.top.dialog.close = true
    if m.opening = invalid then return
    if choice < m.opening.resumes.Count() and m.opening.session <> invalid
        ' With a kept session: resume through the menus, as a disc does, with the settings they had
        ' (variables, the viewing mode, the resume point's queue and after-action), so the title's
        ' cues and buttons work too.
        startMenu(resumeSession(m.opening.session, m.opening.resumes[choice]), true)
    else if choice < m.opening.resumes.Count()
        ' Straight to where they stopped; afterwards back to the tile, as for any title.
        r = m.opening.resumes[choice]
        tile = invalid
        if m.opening.movie <> invalid and itemKey(r.item) = itemKey(m.opening.movie) then tile = m.menuFrom.tile
        m.direct = { item: r.item, tile: tile, screen: m.menuFrom.screen, list: m.menuFrom.list }
        m.opening = invalid
        startTitle({ start: r.position, audio: r.audio, subtitle: r.subtitle }, m.menuMedia[r.media])
    else
        startMenu(menuSession(m.opening.session, m.opening.lastStop))
    end if
end sub

' The session for resuming the prompt's title r: the kept one, with its resume point when that's
' the stop the prompt offers, else r's title from r's position (another title, or the server's
' position when it was watched elsewhere). The tracks are the ones the viewer had, as the prompt's
' plain resume uses.
function resumeSession(kept as object, r as object) as object
    session = ParseJson(FormatJson(kept))
    point = session["lastStop"]
    if point = invalid or point["media"] <> r.media or Abs(msOr(point["position"], 0) - r.position) > 2
        session["lastStop"] = { media: r.media, position: r.position }
    end if
    if session["player"] = invalid then session["player"] = {}
    session["player"]["@audio"] = r.audio
    session["player"]["@subtitle"] = r.subtitle
    return session
end function

' The session for opening the menus: the kept one (invalid: none), with the newest stop this app
' recorded when the kept one has no resume point or another one (a title played from its tile since).
function menuSession(kept as dynamic, lastStop as dynamic) as dynamic
    if kept = invalid then return invalid
    session = ParseJson(FormatJson(kept))
    point = session["lastStop"]
    if lastStop <> invalid and (point = invalid or point["media"] <> lastStop.media or Abs(msOr(point["position"], 0) - lastStop.position) > 2)
        session["lastStop"] = { media: lastStop.media, position: lastStop.position }
    end if
    return session
end function

' --- kept sessions (spec 6.5.1) ------------------------------------------------------------------
' One per menu and server, in their own registry section: { stamp, at, session }. The registry holds
' 32 KB in all, shared with the stops and the sign-in, so only the most recent few are kept.

' A manifest's version: the MD5 of its text.
function manifestStamp(text as string) as string
    bytes = CreateObject("roByteArray")
    bytes.FromAsciiString(text)
    digest = CreateObject("roEVPDigest")
    digest.Setup("md5")
    return digest.Process(bytes)
end function

function sessionKey(entryId as string) as string
    return msOr(m.login.machineId, msOr(m.login.serverId, m.login.url)) + "|" + entryId
end function

' The kept session for this menu, or invalid. One kept for another version of the manifest is
' discarded: its variables and resume point mean nothing to this one.
function sessionRead(entryId as string, stamp as dynamic) as dynamic
    section = CreateObject("roRegistrySection", "ovmplayer-sessions")
    key = sessionKey(entryId)
    if not section.Exists(key) then return invalid
    kept = ParseJson(section.Read(key))
    if kept <> invalid and kept.stamp = stamp and kept.session <> invalid then return kept.session
    section.Delete(key)
    section.Flush()
    return invalid
end function

' Keep a session. A write that fails (no space left) never stops the menus or playback.
sub sessionWrite(key as dynamic, stamp as dynamic, session as dynamic)
    if key = invalid or stamp = invalid or session = invalid then return
    section = CreateObject("roRegistrySection", "ovmplayer-sessions")
    section.Write(key, FormatJson({ stamp: stamp, at: CreateObject("roDateTime").AsSeconds(), session: session }))
    ' The ten most recent, and at least 4 KB of the registry left free for everything else.
    registry = CreateObject("roRegistry")
    keys = section.GetKeyList()
    while keys.Count() > 1 and (keys.Count() > 10 or registry.GetSpaceAvailable() < 4096)
        oldest = invalid
        oldestAt = invalid
        for each k in keys
            if k <> key
                kept = ParseJson(section.Read(k))
                at = 0
                if kept <> invalid then at = msOr(kept.at, 0)
                if oldest = invalid or at < oldestAt
                    oldest = k
                    oldestAt = at
                end if
            end if
        end for
        if oldest = invalid then exit while
        section.Delete(oldest)
        section.Flush()
        keys = section.GetKeyList()
    end while
    section.Flush()
end sub

sub onResumeClosed()
    ' Back on the question: stay in the library.
    if m.opening <> invalid and m.top.dialog <> invalid and m.top.dialog.buttonSelected = invalid then m.opening = invalid
end sub

' Seconds as h:mm:ss (or m:ss under an hour).
function clockOf(seconds as float) as string
    t = Int(seconds)
    h = Int(t / 3600)
    mm = Int((t mod 3600) / 60)
    ss = t mod 60
    if h > 0 then return h.ToStr() + ":" + twoDigits(mm) + ":" + twoDigits(ss)
    return mm.ToStr() + ":" + twoDigits(ss)
end function

function twoDigits(n as integer) as string
    return Right("0" + n.ToStr(), 2)
end function

' session: a kept session to restore (spec 6.5.1), or invalid; resume: start by resuming it.
sub startMenu(session = invalid as dynamic, resume = false as boolean)
    m.menu = CreateObject("roSGNode", "MenuView")
    if m.opening.lastStop <> invalid then m.menu.lastStop = m.opening.lastStop
    if session <> invalid
        m.menu.session = session
        m.menu.resume = resume
    end if
    ' Where the menus' session is kept, whatever happens in them from here.
    m.sessionKey = sessionKey(m.opening.entry["id"])
    m.sessionStamp = m.opening.stamp
    m.menu.observeField("sessionOut", "onSessionOut")
    m.menu.manifest = m.opening.manifest
    m.menu.manifestUrl = m.menuBase + m.opening.entry["url"]
    m.menu.authHeaders = assetHeaders()
    m.menu.observeField("playRequest", "onPlayRequest")
    m.menu.observeField("seekRequest", "onSeekRequest")
    m.menu.observeField("holdEnd", "onHoldEnd")
    m.menu.observeField("navTimerRequest", "onNavTimerRequest")
    m.menu.observeField("tracksRequest", "onTracksRequest")
    m.menu.observeField("exit", "onMenuExit")
    m.menu.observeField("stopRequest", "onMenuStop")
    m.menu.observeField("overlayShowing", "onOverlayShowing")
    m.top.findNode(m.menuFrom.screen).visible = false
    setStatus("")
    m.direct = invalid
    m.top.appendChild(m.menu)
    m.menu.control = "start"
    m.opening = invalid
end sub

sub onSessionOut()
    if m.menu <> invalid then sessionWrite(m.sessionKey, m.sessionStamp, m.menu.sessionOut)
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
    print "ovmplayer: this Roku plays video "; v; "; audio "; a
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
        "SubtitleProfiles": [{ "Format": "srt", "Method": "External" }, { "Format": "ttml", "Method": "External" }]
    }
end function

' Jellyfin's index of the Nth (1-based) audio stream of a media source, for AudioStreamIndex.
function audioStreamIndex(item as object, sourceId as string, n as dynamic) as dynamic
    return streamIndex(item, sourceId, n, "Audio")
end function

' The same for the Nth subtitle stream (SubtitleStreamIndex).
function streamIndex(item as object, sourceId as string, n as dynamic, kind as string) as dynamic
    if n = invalid or item["MediaSources"] = invalid then return invalid
    for each src in item["MediaSources"]
        if src["Id"] = sourceId and src["MediaStreams"] <> invalid
            count = 0
            for each stream in src["MediaStreams"]
                if stream["Type"] = kind
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
    playMenuRequest(m.menu.playRequest)
end sub

' Level 2: a seek from the menus (spec 6.3). A file seeks in place; a title made of segments starts
' over at that point, which finds the segment that holds it.
sub onSeekRequest()
    if m.player = invalid or m.menuReq = invalid then return
    position = m.menu.seekRequest
    ' A seek from a held frame (an overlay button over a still) moves on without the hold's cues.
    if m.cueClock <> invalid and m.cueClock.holding <> invalid
        m.cueClock.holding = invalid
        m.cueClock.endAfterHold = false
        m.holdTimer.control = "stop"
        m.player.control = "resume"
    end if
    if m.segPlay = invalid
        if m.cueClock <> invalid then m.cueClock.last = position
        m.player.seek = position
        return
    end if
    req = msCopyAA(m.menuReq)
    req.start = position
    playMenuRequest(req)
end sub

' Level 2: a track changed during playback (spec 6.3). A directly played file switches its soundtrack
' in place; anything else (subtitles, another angle, a transcode) starts over where the title is.
sub onTracksRequest()
    if m.player = invalid or m.menuReq = invalid then return
    change = m.menu.tracksRequest
    if change.audio <> invalid and change.subtitle = invalid and change.angle = invalid and m.playing <> invalid and m.playing.method = "DirectPlay" and m.segPlay = invalid
        m.menuReq.audio = change.audio
        m.playing.req.audio = change.audio
        onAudioTracks()
        return
    end if
    req = msCopyAA(m.menuReq)
    for each key in ["audio", "subtitle", "angle"]
        if change[key] <> invalid then req[key] = change[key]
    end for
    req.start = titlePosition()
    playMenuRequest(req)
end sub

' Play what the menus asked for: { media, start, audio?, subtitle?, angle?, end?, cues? }.
sub playMenuRequest(req as object)
    m.menuReq = req
    m.recoverClock = invalid
    ' A cue started this while a title plays (it returns there afterwards): that one stops first.
    if m.player <> invalid
        if m.playing <> invalid then rememberStop(msOr(m.playing.stopItem, m.playing.item), playerPosition(), m.playing.req)
        report("/Sessions/Playing/Stopped")
        closePlayer()
    end if
    m.segPlay = invalid
    ' Level 2: a title made of segments of other media (spec 4.2).
    info = m.menu.manifest["media"][req.media]
    if info <> invalid and info["segments"] <> invalid
        startSegments(req, info["segments"])
        return
    end if
    target = m.menuMedia[req.media]
    if target = invalid
        setStatus("That title isn't in your library.")
        titleStopped(req.start)
        return
    end if
    ' Angle 2 and up of angle files is another file; without a match it plays angle 1 (spec 4.1).
    ' Angles kept as video tracks play the default track: Roku can't switch video tracks.
    main = target
    if req.angle <> invalid and req.angle > 1 and m.menuAngles <> invalid and m.menuAngles[req.media] <> invalid
        other = m.menuAngles[req.media][req.angle - 2]
        if other <> invalid then target = other
        print "ovmplayer: angle "; req.angle; " of "; req.media; " -> "; target.path
    end if
    startTitle(req, target)
    ' Stops are remembered on the media itself: the angles line up in time.
    if m.playing <> invalid then m.playing.stopItem = main.item
end sub

' Play one file: req { start, audio?, subtitle? } from a menu button or a library tile.
sub startTitle(req as object, target as object)
    if isPlex()
        plexPlay(req, target)
        return
    end if
    m.playing = { req: req, item: target.item, source: target.source, path: target.path, session: invalid, method: "DirectPlay", started: false, target: target }
    body = { "UserId": m.login.userId, "MediaSourceId": target.source, "DeviceProfile": m.profile, "EnableDirectPlay": true, "EnableDirectStream": true, "EnableTranscoding": true, "AutoOpenLiveStream": true, "IsPlayback": true }
    ' Always name the audio track that will play (the menu's choice, else the file's default, else
    ' its first). Jellyfin only checks an explicitly requested track against the device profile;
    ' left to choose, it narrows candidates to "default"-flagged tracks, and with none (the example
    ' Finale's DTS track has no default flag) it skips the audio check and approves direct play of
    ' audio this TV can't decode (StreamBuilder.cs, Jellyfin 12.1, "Collect candidate audio streams").
    audio = audioStreamIndex(target.item, target.source, req.audio)
    if audio = invalid then audio = defaultAudioIndex(target.item, target.source)
    if audio <> invalid then body["AudioStreamIndex"] = audio
    ' The menu's subtitle choice: the Nth subtitle track, or none (0). Jellyfin then delivers a
    ' text track as a separate file (the device profile's External method) or burns it in.
    if req.subtitle <> invalid
        m.playing.subtitleIndex = streamIndex(target.item, target.source, req.subtitle, "Subtitle")
        body["SubtitleStreamIndex"] = -1
        if m.playing.subtitleIndex <> invalid then body["SubtitleStreamIndex"] = m.playing.subtitleIndex
    end if
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
    print "ovmplayer: PlaybackInfo directPlay="; source["SupportsDirectPlay"]; " directStream="; source["SupportsDirectStream"]; " transcode="; source["SupportsTranscoding"]; " url="; source["TranscodingUrl"] <> invalid; " reasons="; FormatJson(msOr(source["TranscodeReasons"], [])); " container="; source["Container"]
    m.playing.session = msOr(res.json["PlaySessionId"], CreateObject("roDeviceInfo").GetRandomUUID().Replace("-", ""))
    content = CreateObject("roSGNode", "ContentNode")
    if source["TranscodingUrl"] <> invalid and source["SupportsDirectPlay"] <> true
        m.playing.method = "Transcode"
        content.url = m.login.url + source["TranscodingUrl"]
        content.StreamFormat = "hls"
        print "ovmplayer: transcoding ("; msOr(source["TranscodeReasons"], []).Join(", "); ")"
    else
        content.url = m.login.url + "/Videos/" + m.playing.item["Id"] + "/stream?static=true&mediaSourceId=" + m.playing.source + "&playSessionId=" + m.playing.session
        content.StreamFormat = streamFormat(m.playing.path)
    end if
    content.HttpHeaders = ["Authorization:" + authHeader(m.login.token)]
    content.PlayStart = req.start
    content.title = m.playing.item["Name"]
    for each stream in msOr(source["MediaStreams"], [])
        if stream["Index"] = m.playing.subtitleIndex and stream["Type"] = "Subtitle"
            print "ovmplayer: subtitle "; stream["Index"]; " delivered "; stream["DeliveryMethod"]
            if stream["DeliveryMethod"] = "External" and stream["DeliveryUrl"] <> invalid
                sideloadSubtitle(content, m.login.url + stream["DeliveryUrl"], stream["Language"], stream["DisplayTitle"])
            end if
        end if
    end for
    openPlayer(content)
end sub

' Full-screen playback of a title, over the menu.
sub openPlayer(content as object)
    m.player = CreateObject("roSGNode", "Video")
    m.player.width = 1920
    m.player.height = 1080
    m.player.observeField("state", "onPlayerState")
    m.player.observeField("availableAudioTracks", "onAudioTracks")
    ' Every title: its position is kept as it plays (see playerPosition), and cues are timed on it.
    m.player.observeField("position", "onPlayerPosition")
    m.player.content = content
    m.top.appendChild(m.player)
    ' Level 2: a menu's overlay buttons draw over the title, so the menu goes above the player (it
    ' shows nothing else while a title plays), and its cues are timed on the player's position.
    if m.menu <> invalid
        m.top.removeChild(m.menu)
        m.top.appendChild(m.menu)
        req = m.playing.req
        if m.segPlay <> invalid
            m.cueClock = m.segClock
            m.player.notificationInterval = 0.25
        else if m.recoverClock <> invalid
            ' The same title reopened after it broke off: its cues go on from where they were.
            m.cueClock = m.recoverClock
            m.recoverClock = invalid
            m.player.notificationInterval = 0.25
        else if req.cues <> invalid or req["end"] <> invalid or req.holds <> invalid
            m.cueClock = newCueClock(req)
            m.player.notificationInterval = 0.25
        end if
    end if
    m.player.setFocus(true)
    m.player.control = "play"
end sub

' Level 2: cue reports for the menu, as CueClock in packages/renderer-web/src/adapters.ts: point cues
' when playback passes them in ordinary play (not seeking), overlays while inside their window, and
' the item's end as the title ending.
sub onPlayerPosition()
    if m.player = invalid then return
    if m.playing <> invalid and m.player.position > 0 then m.playing.lastPosition = msOr(m.playing.offset, 0) + m.player.position
    if m.menu = invalid then return
    ' A segment that ends inside its file hands over to the next one there.
    if m.segPlay <> invalid
        piece = m.segPlay.pieces[m.segPlay.index]
        if piece["end"] <> invalid and playerPosition() >= piece["end"]
            nextSegment()
            return
        end if
    end if
    clock = m.cueClock
    if clock = invalid or clock.holding <> invalid then return
    position = titlePosition()
    if clock.floor <> invalid
        if position < clock.floor then return
        clock.floor = invalid
    end if
    last = clock.last
    played = position >= last and position - last <= 2
    ' Ordinary play reached a hold: play up to just before it, then hold there (spec 6.3).
    if played
        for each h in clock.holds
            if last < h["at"] and h["at"] <= position
                cueClockAdvance(clock, last, h["at"] - m.holdBefore, true)
                startHold(h)
                return
            end if
        end for
    end if
    cueClockAdvance(clock, last, position, played)
end sub

' Level 2: a clock for a title's cues, end and holds (see CueClock in packages/renderer-web/src/adapters.ts).
function newCueClock(req as object) as object
    return { cues: msOr(req.cues, []), end: req["end"], holds: msOr(req.holds, []), last: req.start, floor: req.start, inside: {}, holding: invalid, endAfterHold: false }
end function

' Move the clock from last to position: cues passed in ordinary play, overlay windows, the item's end.
sub cueClockAdvance(clock as object, last as dynamic, position as dynamic, played as boolean)
    clock.last = position
    for each c in clock.cues
        if c["at"] <> invalid
            if played and last < c["at"] and c["at"] <= position then m.menu.cue = c["id"]
        else
            inWindow = c["from"] <= position and position < c["to"]
            if inWindow and not clock.inside.DoesExist(c["id"])
                clock.inside[c["id"]] = true
                m.menu.cue = c["id"]
            else if not inWindow and clock.inside.DoesExist(c["id"])
                clock.inside.Delete(c["id"])
                m.menu.cueEnd = c["id"]
            end if
        end if
    end for
    if clock["end"] <> invalid and position >= clock["end"] and m.playing <> invalid
        ' Stop here, as if the title had finished.
        m.cueClock = invalid
        report("/Sessions/Playing/Stopped")
        rememberStop(msOr(m.playing.stopItem, m.playing.item), position, m.playing.req)
        m.playing = invalid
        closePlayer()
        titleEnded()
    end if
end sub

' Level 3: the menus start or stop the navigation timer.
sub onNavTimerRequest()
    request = m.menu.navTimerRequest
    m.navTimer.control = "stop"
    if request.type = "startNavTimer"
        m.navTimer.duration = request.after
        m.navTimer.control = "start"
    end if
end sub

' The navigation timer ran out: tell the menus where the title is, if one plays.
sub onNavTimer()
    if m.menu = invalid then return
    position = -1
    if m.player <> invalid then position = titlePosition()
    m.menu.navTimerFired = position
end sub

' Level 2: stop on the hold's frame, for its time or until the viewer goes on (OK, or an overlay button).
sub startHold(hold as object)
    m.cueClock.holding = hold
    m.player.control = "pause"
    if m.segPlay = invalid then m.player.seek = hold["at"]
    if not msNavIsText(hold["for"])
        m.holdTimer.duration = hold["for"]
        m.holdTimer.control = "start"
    end if
    ' The scene takes OK while a frame is held (the menu has the keys while an overlay shows).
    if m.menu = invalid or not m.menu.overlayShowing then m.top.setFocus(true)
end sub

' The hold is over: cues at its time run, then the title plays on (or ends, if it ran out into the hold).
function endHold() as boolean
    clock = m.cueClock
    if clock = invalid or clock.holding = invalid then return false
    at = clock.holding["at"]
    clock.holding = invalid
    m.holdTimer.control = "stop"
    cueClockAdvance(clock, at - m.holdBefore, at, true)
    if m.player = invalid then return true
    if clock.endAfterHold
        clock.endAfterHold = false
        finishTitle()
        return true
    end if
    m.player.control = "resume"
    if m.menu = invalid or not m.menu.overlayShowing then m.player.setFocus(true)
    return true
end function

sub onHoldTimer()
    endHold()
end sub

sub onHoldEnd()
    endHold()
end sub

' The title ran out: a hold not reached yet happens first, as a disc shows its last still. True when
' that's so (the title ends when the hold does).
function holdBeforeEnd() as boolean
    clock = m.cueClock
    if clock = invalid then return false
    if clock.holding <> invalid
        clock.endAfterHold = true
        return true
    end if
    for each h in clock.holds
        if h["at"] > clock.last
            cueClockAdvance(clock, clock.last, h["at"] - m.holdBefore, true)
            clock.endAfterHold = true
            clock.holding = h
            if not msNavIsText(h["for"])
                m.holdTimer.duration = h["for"]
                m.holdTimer.control = "start"
            end if
            m.top.setFocus(true)
            return true
        end if
    end for
    return false
end function

' The title played to its end.
sub finishTitle()
    report("/Sessions/Playing/Stopped")
    forgetStop(m.playing.item)
    m.playing = invalid
    closePlayer()
    titleEnded()
end sub

' Level 2: the menu left playback by itself (a cue opened a menu): stop, remembering where.
sub onMenuStop()
    if m.player = invalid then return
    if m.playing <> invalid then rememberStop(msOr(m.playing.stopItem, m.playing.item), playerPosition(), m.playing.req)
    report("/Sessions/Playing/Stopped")
    closePlayer()
    m.segPlay = invalid
    m.menu.setFocus(true)
end sub

' Level 2: while an overlay's buttons show, the menu takes the arrows and OK (Back still stops).
sub onOverlayShowing()
    if m.player = invalid or m.menu = invalid then return
    if m.menu.overlayShowing then m.menu.setFocus(true) else m.player.setFocus(true)
end sub

' A subtitle file the server delivers separately (srt/ttml): offered to the player as a track.
sub sideloadSubtitle(content as object, url as string, language as dynamic, label as dynamic)
    content.SubtitleTracks = [{ Language: msOr(language, "und"), Description: msOr(label, "Subtitles"), TrackName: url }]
    m.playing.subtitleTrack = url
end sub

' The menu's subtitle choice, on the player: a chosen track shown (captions on for this title), or
' none (captions off). Without a choice the viewer's own setting stands. Burned-in subtitles need
' nothing here.
sub applySubtitles()
    req = m.playing.req
    if req.subtitle = invalid then return
    if req.subtitle = 0
        m.captionModeBefore = m.player.globalCaptionMode
        m.player.globalCaptionMode = "Off"
    else if m.playing.subtitleTrack <> invalid
        m.captionModeBefore = m.player.globalCaptionMode
        m.player.globalCaptionMode = "On"
        m.player.subtitleTrack = m.playing.subtitleTrack
        print "ovmplayer: subtitles on: "; m.player.subtitleTrack
    end if
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
        m.playing.startedAt = UpTime(0)
        touchStop(m.playing.item)
        applySubtitles()
        report("/Sessions/Playing")
        m.progressTimer.control = "start"
        ' Reopened while a frame was held: it holds again.
        if m.cueClock <> invalid and m.cueClock.holding <> invalid then m.player.control = "pause"
    else if (state = "paused" or state = "playing") and m.playing.started
        reportProgress()
    else if state = "finished" and endedEarly()
        if not reopenPlayback("the stream ended early") then playbackFailed("The stream ended before the title did.")
    else if state = "finished" and m.segPlay <> invalid
        nextSegment()
    else if state = "finished"
        if holdBeforeEnd() then return
        finishTitle()
    else if state = "error"
        print "ovmplayer: playback error "; m.player.errorCode; " msg "; m.player.errorMsg; " info "; FormatJson(m.player.errorInfo)
        if not reopenPlayback("playback error") then playbackFailed("Couldn't play this title: " + m.player.errorMsg)
    end if
end sub

' The file's length in seconds, as the server gives it (0 when it doesn't).
function itemDuration(item as object) as float
    if isPlex() then return msOr(item["duration"], 0) / 1000
    return msOr(item["RunTimeTicks"], 0) / 10000000
end function

' "finished" well before the file's end: the stream broke off (after a long pause, Plex may have ended
' its transcode), it didn't really end.
function endedEarly() as boolean
    if m.playing = invalid or not m.playing.started then return false
    duration = itemDuration(m.playing.item)
    return duration > 0 and playerPosition() < duration - 10
end function

' Playback broke off partway through, which happens after a long pause: the server has closed the
' connection or dropped its transcode. Open the same title again where it was, with a new session (a new
' playback decision), as Plex's and Jellyfin's own apps do. Twice at most in a row; true when reopening.
function reopenPlayback(why as string) as boolean
    p = m.playing
    if p = invalid or not p.started or p.target = invalid then return false
    ' A minute of good playback since the last reopen starts the count again.
    if m.reopenTries = invalid or UpTime(0) - msOr(m.reopenedAt, 0) > 60 then m.reopenTries = 0
    if m.reopenTries >= 2 then return false
    m.reopenTries = m.reopenTries + 1
    m.reopenedAt = UpTime(0)
    position = playerPosition()
    print "ovmplayer: "; why; " at "; position; " s: opening it again (try "; m.reopenTries; ")"
    rememberStop(msOr(p.stopItem, p.item), position, p.req)
    onTitle = titlePosition()
    report("/Sessions/Playing/Stopped")
    clock = m.cueClock
    closePlayer()
    if m.segPlay <> invalid
        ' A title made of segments: the same segment, from where it was. Its clock is kept, and ignores
        ' readings before the restart, as below.
        if m.segClock <> invalid then m.segClock.floor = onTitle
        playSegment(m.segPlay.index, position - m.segPlay.pieces[m.segPlay.index].start)
    else
        if clock <> invalid
            ' The player reads 0 until its seek lands: the clock ignores readings before the restart.
            clock.floor = position
            m.recoverClock = clock
        end if
        req = {}
        for each key in p.req
            req[key] = p.req[key]
        end for
        req.start = position
        startTitle(req, p.target)
    end if
    if m.playing <> invalid then m.playing.stopItem = p.stopItem
    return true
end function

' Playback couldn't go on: back to the menu or the library, keeping where the viewer got to (never as if
' the title had been watched to the end).
sub playbackFailed(message as string)
    setStatus(message)
    p = m.playing
    if p = invalid or not p.started
        closePlayer()
        if p <> invalid then titleStopped(p.req.start)
        return
    end if
    rememberStop(msOr(p.stopItem, p.item), playerPosition(), p.req)
    stoppedAt = titlePosition()
    report("/Sessions/Playing/Stopped")
    closePlayer()
    titleStopped(stoppedAt)
end sub

sub stopTitle()
    position = playerPosition()
    if m.playing <> invalid then rememberStop(msOr(m.playing.stopItem, m.playing.item), position, m.playing.req)
    ' The menu gets the stop on the title's timeline (they differ for a title made of segments).
    stoppedAt = titlePosition()
    report("/Sessions/Playing/Stopped")
    closePlayer()
    titleStopped(stoppedAt)
end sub

' Playback is over: back to the menu that asked for it, or to the tile that was picked (whose
' resume point and watched mark are updated here, as the server now has them).
sub titleStopped(position as float)
    m.segPlay = invalid
    m.recoverClock = invalid
    if m.menu <> invalid
        m.menu.playbackStopped = position
    else
        backFromTitle(position, false)
    end if
end sub

sub titleEnded()
    m.segPlay = invalid
    m.recoverClock = invalid
    if m.menu <> invalid
        m.menu.playbackEnded = true
    else
        backFromTitle(0, true)
    end if
end sub

sub closePlayer()
    m.progressTimer.control = "stop"
    m.cueClock = invalid
    ' Captions are a TV-wide setting: put back what the viewer had.
    if m.captionModeBefore <> invalid
        m.player.globalCaptionMode = m.captionModeBefore
        m.captionModeBefore = invalid
    end if
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
' Level 2: a title made of segments plays them one after another (the Video node can't clip a file,
' so there's a short pause at each join, which the spec allows). m.segPlay: the pieces with their files
' and places on the title's timeline, and which one plays; the cue clock spans the whole title.
sub startSegments(req as object, segments as object)
    pieces = []
    offset = 0
    for each seg in segments
        target = m.menuMedia[seg["media"]]
        ' The play's angle applies to segments whose media have angle files.
        if req.angle <> invalid and req.angle > 1 and m.menuAngles <> invalid and m.menuAngles[seg["media"]] <> invalid
            other = m.menuAngles[seg["media"]][req.angle - 2]
            if other <> invalid then target = other
        end if
        if target = invalid
            setStatus("That title isn't in your library.")
            titleStopped(req.start)
            return
        end if
        start = msOr(seg["start"], 0)
        pieces.push({ target: target, start: start, end: seg["end"], offset: offset })
        if seg["end"] <> invalid then offset = offset + seg["end"] - start
    end for
    m.segPlay = { pieces: pieces, index: 0, req: req }
    m.segClock = invalid
    if req.cues <> invalid or req["end"] <> invalid or req.holds <> invalid
        m.segClock = newCueClock(req)
    end if
    i = 0
    while i < pieces.Count() - 1 and req.start >= pieces[i + 1].offset
        i = i + 1
    end while
    playSegment(i, req.start - pieces[i].offset)
end sub

sub playSegment(i as integer, into as float)
    piece = m.segPlay.pieces[i]
    m.segPlay.index = i
    req = m.segPlay.req
    startTitle({ start: piece.start + into, audio: req.audio, subtitle: req.subtitle }, piece.target)
end sub

' The segment playing reached its end (or its file finished): the next one, or the title has ended.
sub nextSegment()
    report("/Sessions/Playing/Stopped")
    m.playing = invalid
    closePlayer()
    i = m.segPlay.index + 1
    if i < m.segPlay.pieces.Count()
        playSegment(i, 0)
    else
        titleEnded()
    end if
end sub

' Where playback is on the title's timeline (for a title made of segments), else in the file.
function titlePosition() as float
    if m.segPlay = invalid then return playerPosition()
    piece = m.segPlay.pieces[m.segPlay.index]
    into = playerPosition() - piece.start
    if into < 0 then into = 0
    return piece.offset + into
end function

function playerPosition() as float
    if m.player = invalid or m.playing = invalid then return 0
    if m.player.position > 0 then return msOr(m.playing.offset, 0) + m.player.position
    ' The player reads 0 before its first frame, and again once a stream has broken off (after a long
    ' pause): then it's the last real reading, else where playback started.
    if m.playing.lastPosition <> invalid then return m.playing.lastPosition
    return m.playing.req.start
end function

sub reportProgress()
    if m.playing = invalid or m.player = invalid then return
    report("/Sessions/Playing/Progress")
    ' Home closes the app without warning, so where the viewer is gets saved as they watch.
    rememberStop(msOr(m.playing.stopItem, m.playing.item), playerPosition(), m.playing.req)
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
        m.holdTimer.control = "stop"
        stopTitle()
        return true
    end if
    ' Level 2: OK goes on from a frame held until the viewer is ready.
    if key = "OK" and m.player <> invalid and endHold() then return true
    ' Level 2: * is the remote's Menu key; the menus decide what it does during their titles.
    if key = "options" and m.player <> invalid and m.menu <> invalid
        m.menu.menuKey = titlePosition()
        return true
    end if
    if key = "back" and m.top.findNode("code").visible
        m.pollTimer.control = "stop"
        m.pin = invalid
        m.pending = invalid ' a sign-in still under way is dropped
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
