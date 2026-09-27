' Draws the navigator's state and carries out its effects. See MenuView.xml.

sub init()
    m.canvas = m.top.findNode("canvas")
    m.fill = m.top.findNode("fill")
    m.still = m.top.findNode("still")
    m.motion = m.top.findNode("motion")
    m.highlight = m.top.findNode("highlight")
    m.music = m.top.findNode("music")
    m.still.loadDisplayMode = "scaleToFill"
    m.highlight.loadDisplayMode = "scaleToFill"

    m.motion.observeField("state", "onMotionState")
    m.motion.observeField("position", "onMotionPosition")
    m.music.observeField("state", "onMusicState")

    m.flash = CreateObject("roSGNode", "Timer")
    m.flash.duration = 0.15
    m.flash.observeField("fire", "onFlashDone")
    m.buttonsTimer = CreateObject("roSGNode", "Timer")
    m.buttonsTimer.observeField("fire", "onButtonsTimer")

    m.images = {}       ' asset URL -> local file, once downloaded
    m.wanted = {}       ' poster id -> the asset URL it should show now
    m.downloads = {}    ' task id -> asset URL
    m.seq = 0
    m.shownMenu = invalid
    m.videoRole = invalid  ' "background" or "transition"
end sub

sub onControl()
    if m.top.control <> "start" then return
    m.manifest = m.top.manifest
    m.nav = msNavInitialState()
    stepWith({ type: "start" })
    m.top.setFocus(true)
end sub

' --- the navigator loop ------------------------------------------------------------------------

sub stepWith(event as object)
    before = m.nav
    result = msNavStep(m.manifest, m.nav, event)
    m.nav = result.state
    fresh = before.mode <> m.nav.mode or before.menu <> m.nav.menu or event.type = "start" or before.intro <> m.nav.intro
    render(fresh)
    for each effect in result.effects
        apply(effect)
    end for
end sub

sub apply(effect as object)
    t = effect.type
    if t = "activated"
        button = findButton(effect.menu, effect.button)
        image = stateImage(button, "activated")
        if image <> invalid
            showImage(m.highlight, image)
            m.flash.control = "start"
        end if
    else if t = "play"
        stopBackground()
        m.top.playRequest = effect
    else if t = "exit"
        stopBackground()
        m.top.exit = true
    else if t = "transition"
        playVideo(effect.src, 0, "transition")
    else if t = "skipIntro"
        menu = m.manifest["menus"][effect.menu]
        at = msOr(menu["buttonsAt"], 0)
        if m.videoRole = "background" then m.motion.seek = at
        drawHighlight()
    end if
end sub

sub onFlashDone()
    drawHighlight()
end sub

function onKeyEvent(key as string, press as boolean) as boolean
    if not press then return false
    map = { up: "up", down: "down", left: "left", right: "right", OK: "enter", back: "back" }
    k = map[key]
    if k = invalid then return false
    if m.nav.mode = "playing" then return false ' the scene owns playback keys
    stepWith({ type: "press", key: k })
    return true
end function

sub onPlaybackEnded()
    stepWith({ type: "ended" })
    m.top.setFocus(true)
end sub

sub onPlaybackStopped()
    stepWith({ type: "stopped", position: m.top.playbackStopped })
    m.top.setFocus(true)
end sub

' --- drawing -----------------------------------------------------------------------------------

sub render(fresh as boolean)
    mode = m.nav.mode
    if mode = "menu"
        menu = m.manifest["menus"][m.nav.menu]
        if fresh or m.shownMenu <> m.nav.menu then showMenu(menu)
        drawHighlight()
    else if mode = "transition"
        m.highlight.visible = false
    else if mode = "playing"
        m.highlight.visible = false
    end if
end sub

' Lay out the canvas (scaled to fit the screen at its display aspect, like the web renderer) and
' start its background: the still, then motion and music. A fresh entry plays the intro from 0;
' returning (back, end of playback) starts at the loop point with buttons ready.
sub showMenu(menu as object)
    m.shownMenu = m.nav.menu
    canvas = menu["canvas"]
    cw = canvas["width"]
    ch = canvas["height"]
    aspect = aspectOf(canvas["aspect"], cw / ch)
    dw = 1920
    dh = 1920 / aspect
    if dh > 1080
        dh = 1080
        dw = 1080 * aspect
    end if
    m.sx = dw / cw
    m.sy = dh / ch
    m.canvas.translation = [(1920 - dw) / 2, (1080 - dh) / 2]
    m.fill.width = dw
    m.fill.height = dh
    bg = msOr(menu["background"], {})
    m.fill.color = colorOf(bg["color"])
    m.still.width = dw
    m.still.height = dh
    m.still.visible = bg["image"] <> invalid
    if bg["image"] <> invalid then showImage(m.still, bg["image"])
    prefetchMenu(menu)

    m.motion.translation = [0, 0]
    m.motion.width = dw
    m.motion.height = dh
    video = bg["video"]
    if video <> invalid
        start = 0
        if not m.nav.intro then start = msOr(video["loopStart"], 0)
        playVideo(video["src"], start, "background")
    else
        stopBackground()
    end if

    music = bg["audio"]
    if music <> invalid
        m.musicLoop = msOr(music["loopStart"], 0)
        start = 0
        if not m.nav.intro then start = m.musicLoop
        content = CreateObject("roSGNode", "ContentNode")
        content.url = assetUrl(music["src"])
        content.HttpHeaders = ["Authorization:" + m.top.authHeader]
        content.PlayStart = start
        m.music.content = content
        m.music.control = "play"
    else
        m.music.control = "stop"
    end if

    ' Buttons appear at buttonsAt (on the video clock, or a timer without video).
    m.buttonsTimer.control = "stop"
    if not m.nav.buttonsReady and video = invalid
        m.buttonsTimer.duration = msOr(menu["buttonsAt"], 0)
        m.buttonsTimer.control = "start"
    end if
end sub

sub drawHighlight()
    if m.nav.mode <> "menu" or not m.nav.buttonsReady
        m.highlight.visible = false
        return
    end if
    button = findButton(m.nav.menu, m.nav.focus)
    image = stateImage(button, "selected")
    if image = invalid
        m.highlight.visible = false
        return
    end if
    rect = button["rect"]
    s = button["states"]["selected"]
    if s["rect"] <> invalid then rect = s["rect"]
    m.highlight.translation = [rect[0] * m.sx, rect[1] * m.sy]
    m.highlight.width = rect[2] * m.sx
    m.highlight.height = rect[3] * m.sy
    showImage(m.highlight, image)
end sub

function findButton(menuId as string, id as dynamic) as dynamic
    return msNavFindButton(m.manifest["menus"][menuId], id)
end function

function stateImage(button as dynamic, state as string) as dynamic
    if button = invalid or button["states"] = invalid then return invalid
    s = button["states"][state]
    if s = invalid then return invalid
    return s["image"]
end function

' --- video: menu motion and transitions ------------------------------------------------------

sub playVideo(src as string, start as float, role as string)
    m.videoRole = role
    content = CreateObject("roSGNode", "ContentNode")
    content.url = assetUrl(src)
    content.StreamFormat = "mp4"
    content.HttpHeaders = ["Authorization:" + m.top.authHeader]
    content.PlayStart = start
    m.motion.content = content
    m.motion.visible = true
    m.motion.control = "play"
end sub

sub stopBackground()
    m.motion.control = "stop"
    m.motion.visible = false
    m.still.visible = m.shownMenu <> invalid
    m.music.control = "stop"
    m.videoRole = invalid
end sub

sub onMotionState()
    state = m.motion.state
    ' The still is the poster shown while the video loads (spec §5.1). On Roku an opaque poster
    ' covers the video even from below it in the scene, so hide it once the video is showing.
    if state = "playing" then m.still.visible = false
    if state = "finished"
        if m.videoRole = "transition"
            stepWith({ type: "transitionEnded" })
        else if m.videoRole = "background" and m.nav.mode = "menu"
            ' Loop from loopStart for as long as the menu is shown.
            menu = m.manifest["menus"][m.nav.menu]
            playVideo(menu["background"]["video"]["src"], msOr(menu["background"]["video"]["loopStart"], 0), "background")
        end if
    else if state = "error"
        print "MenuStream: menu video error "; m.motion.errorMsg
        if m.videoRole = "transition" then stepWith({ type: "transitionEnded" })
    end if
end sub

sub onMotionPosition()
    if m.videoRole <> "background" or m.nav.mode <> "menu" or m.nav.buttonsReady then return
    menu = m.manifest["menus"][m.nav.menu]
    if m.motion.position >= msOr(menu["buttonsAt"], 0) then stepWith({ type: "buttonsReady" })
end sub

sub onButtonsTimer()
    stepWith({ type: "buttonsReady" })
end sub

sub onMusicState()
    if m.music.state = "finished" and m.nav.mode = "menu"
        content = m.music.content
        content.PlayStart = m.musicLoop
        m.music.content = content
        m.music.control = "play"
    end if
end sub

' --- files -------------------------------------------------------------------------------------

' Asset paths are relative to the manifest's URL; each segment is escaped (spaces, parentheses).
function assetUrl(path as string) as string
    parts = []
    for each seg in path.Split("/")
        parts.push(percentEncode(seg))
    end for
    return folderOf(m.top.manifestUrl) + parts.Join("/")
end function

' URL-encode one path segment (UTF-8). roUrlTransfer.Escape would do this, but roUrlTransfer can't
' be created on the render thread, where this runs.
function percentEncode(s as string) as string
    bytes = CreateObject("roByteArray")
    bytes.FromAsciiString(s)
    out = ""
    for each b in bytes
        c = Chr(b)
        if (b >= 48 and b <= 57) or (b >= 65 and b <= 90) or (b >= 97 and b <= 122) or c = "-" or c = "_" or c = "." or c = "~"
            out = out + c
        else
            out = out + "%" + Right("0" + UCase(StrI(b, 16)), 2)
        end if
    end for
    return out
end function

' Everything up to and including the last "/".
function folderOf(url as string) as string
    i = Len(url)
    while i > 0 and Mid(url, i, 1) <> "/"
        i = i - 1
    end while
    return Left(url, i)
end function

' Posters can't send an Authorization header, so images are downloaded with it into tmp: first.
' Show a menu image on a poster. Each poster remembers the image it wants now, so a download that
' finishes after the cursor has moved on never lands on the wrong button.
sub showImage(poster as object, path as string)
    url = assetUrl(path)
    poster.visible = true
    m.wanted[poster.id] = url
    if m.images.DoesExist(url)
        poster.uri = m.images[url]
        return
    end if
    fetchImage(url)
end sub

' Download an image (once) into tmp:. Named after its URL: Roku caches images by file name, so two
' different images must never share one.
sub fetchImage(url as string)
    file = "tmp:/ms-" + urlKey(url)
    if m.images.DoesExist(url) or m.downloads.DoesExist(file) then return
    task = CreateObject("roSGNode", "HttpTask")
    task.addFields({ url: url, file: file })
    task.request = { url: url, headers: { "Authorization": m.top.authHeader }, saveTo: file }
    task.observeField("response", "onDownloaded")
    m.downloads[file] = { task: task, url: url }
    task.control = "RUN"
end sub

sub onDownloaded(event as object)
    task = event.getRoSGNode()
    pending = m.downloads[task.file]
    m.downloads.Delete(task.file)
    if pending = invalid then return
    if task.response.code <> 200
        print "MenuStream: couldn't load "; pending.url; " (HTTP "; task.response.code; ")"
        return
    end if
    m.images[pending.url] = task.file
    for each poster in [m.still, m.highlight]
        if m.wanted[poster.id] = pending.url then poster.uri = task.file
    end for
end sub

' Fetch every image a menu uses up front, so moving the cursor never waits for the network.
sub prefetchMenu(menu as object)
    for each button in menu["buttons"]
        if button["states"] <> invalid
            for each state in ["selected", "activated", "normal"]
                s = button["states"][state]
                if s <> invalid and s["image"] <> invalid then fetchImage(assetUrl(s["image"]))
            end for
        end if
    end for
end sub

' A file name that is unique per asset URL. The path after /files/ is already percent-encoded, so it
' only contains unreserved characters, "%" and "/"; swapping those two for "=" and "," (which an
' encoded path never contains) is one-to-one, so two different assets can never share a file.
' (An earlier version hashed the URL, but BrightScript's mod works on 32-bit integers and the hash
' collapsed, which showed one page's background on another.)
function urlKey(url as string) as string
    i = InStr(1, url, "/files/")
    key = url
    if i > 0 then key = Mid(url, i + 7)
    return key.Replace("%", "=").Replace("/", ",")
end function

function fileExtension(path as string) as string
    dot = path.Split(".")
    if dot.Count() < 2 then return ""
    return "." + LCase(dot[dot.Count() - 1])
end function

function aspectOf(value as dynamic, fallback as float) as float
    if value = invalid then return fallback
    if type(value) = "String" or type(value) = "roString"
        parts = value.Split(":")
        if parts.Count() = 2 then return Val(parts[0]) / Val(parts[1])
        return fallback
    end if
    return value
end function

function colorOf(css as dynamic) as string
    if css = invalid then return "0x000000FF"
    hex = css.Replace("#", "")
    if Len(hex) = 3 then hex = Mid(hex, 1, 1) + Mid(hex, 1, 1) + Mid(hex, 2, 1) + Mid(hex, 2, 1) + Mid(hex, 3, 1) + Mid(hex, 3, 1)
    if Len(hex) <> 6 then return "0x000000FF"
    return "0x" + UCase(hex) + "FF"
end function
