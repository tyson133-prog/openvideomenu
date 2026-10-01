' Draws the navigator's state and carries out its effects. See MenuView.xml.

sub init()
    m.canvas = m.top.findNode("canvas")
    m.fill = m.top.findNode("fill")
    m.still = m.top.findNode("still")
    m.motion = m.top.findNode("motion")
    m.buttons = m.top.findNode("buttons")
    m.ring = m.top.findNode("ring")
    m.still.observeField("loadStatus", "drawButtons")
    m.music = m.top.findNode("music")
    m.still.loadDisplayMode = "scaleToFill"

    m.motion.observeField("state", "onMotionState")
    m.motion.observeField("position", "onMotionPosition")
    m.motion.notificationInterval = 0.1 ' fine enough to catch the last 0.3 s before a loop point
    m.music.observeField("state", "onMusicState")

    m.flash = CreateObject("roSGNode", "Timer")
    m.flash.duration = 0.15
    m.flash.observeField("fire", "onFlashDone")
    m.buttonsTimer = CreateObject("roSGNode", "Timer")
    m.buttonsTimer.observeField("fire", "onButtonsTimer")
    ' A menu's timeout countdown (Level 2), started by the navigator's startTimer effect.
    m.menuTimer = CreateObject("roSGNode", "Timer")
    m.menuTimer.observeField("fire", "onMenuTimer")
    m.menuTimerMenu = invalid

    m.images = {}       ' asset URL -> local file, once downloaded
    m.wanted = {}       ' poster id -> the asset URL it should show now
    m.downloads = {}    ' task id -> asset URL
    m.seq = 0
    m.shownMenu = invalid
    m.videoRole = invalid  ' "background" or "transition"
    m.buttonViews = []     ' per button of the menu shown: { button, poster, fill?, shadow?, label? }
    m.overlay = m.top.findNode("overlay")
    m.overlayButtons = m.top.findNode("overlayButtons")
    m.overlayViews = []    ' the same, for the overlay showing over a playing title (Level 2)
    m.shownOverlay = invalid
    m.activatedId = invalid ' the button showing its activated state, briefly
end sub

sub onControl()
    if m.top.control <> "start" then return
    m.manifest = m.top.manifest
    m.nav = msNavInitialState()
    seed = m.top.lastStop
    if seed <> invalid and seed.media <> invalid then m.nav.lastStop = { media: seed.media, position: seed.position }
    stepWith({ type: "start", player: playerState(), seed: Rnd(2147483646) })
    m.top.setFocus(true)
end sub

' What this Roku knows of the viewer's setup, as "@" player state (spec §6.1).
function playerState() as object
    info = CreateObject("roDeviceInfo")
    state = {}
    state.SetModeCaseSensitive()
    ' The locale is "en_US": the language and the country.
    parts = info.GetCurrentLocale().Split("_")
    if parts.Count() > 0 and Len(parts[0]) = 2
        lang = LCase(parts[0])
        state["@audioLanguage"] = lang
        state["@subtitleLanguage"] = lang
        state["@menuLanguage"] = lang
    end if
    if parts.Count() > 1 and Len(parts[1]) = 2 then state["@country"] = UCase(parts[1])
    if info.GetDisplayAspectRatio() = "4x3" then state["@aspect"] = "4:3"
    return state
end function

' --- the navigator loop ------------------------------------------------------------------------

sub stepWith(event as object)
    before = m.nav
    result = msNavStep(m.manifest, m.nav, event)
    m.nav = result.state
    ' A cue that opens a menu leaves playback without the scene reporting a stop: ask it to stop.
    if before.mode = "playing" and m.nav.mode <> "playing" and event.type <> "ended" and event.type <> "stopped" then m.top.stopRequest = true
    fresh = before.mode <> m.nav.mode or before.menu <> m.nav.menu or event.type = "start" or before.intro <> m.nav.intro
    render(fresh)
    for each effect in result.effects
        apply(effect)
    end for
end sub

sub apply(effect as object)
    t = effect.type
    if t = "activated"
        m.activatedId = effect.button
        drawButtons()
        drawOverlay()
        m.flash.control = "start"
    else if t = "play"
        stopBackground()
        m.top.playRequest = effect
    else if t = "exit"
        stopBackground()
        m.top.exit = true
    else if t = "transition"
        playVideo(effect.src, 0, "transition")
    else if t = "startTimer"
        m.menuTimer.control = "stop"
        m.menuTimerMenu = effect.menu
        m.menuTimer.duration = effect.after
        m.menuTimer.control = "start"
    else if t = "skipIntro"
        menu = m.manifest["menus"][effect.menu]
        at = msOr(menu["buttonsAt"], 0)
        if m.videoRole = "background" then m.motion.seek = at
        drawButtons()
    else if t = "seek"
        ' Level 2: the scene moves the title (spec 6.3).
        m.top.seekRequest = effect.position
    else if t = "tracks"
        ' Level 2: the scene changes a track of the title now.
        m.top.tracksRequest = effect
    else if t = "budgetExceeded"
        ' A named action ran itself without end; the navigator dropped the rest. The menus carry on.
        print "Open Video Menu: an action loop reached the step budget and was stopped."
    end if
end sub

' The countdown ran out: the navigator ignores it if that menu isn't showing any more.
sub onMenuTimer()
    if m.menuTimerMenu = invalid then return
    stepWith({ type: "timeout", menu: m.menuTimerMenu })
end sub

sub onFlashDone()
    m.activatedId = invalid
    drawButtons()
    drawOverlay()
end sub

' Level 2: the scene reports cues of the title playing.
sub onCue()
    stepWith({ type: "cue", id: m.top.cue })
end sub

sub onCueEnd()
    stepWith({ type: "cueEnd", id: m.top.cueEnd })
end sub

function onKeyEvent(key as string, press as boolean) as boolean
    if not press then return false
    ' The * key is the remote's Menu key (Level 2): Roku remotes have no other.
    map = { up: "up", down: "down", left: "left", right: "right", OK: "enter", back: "back", options: "menu" }
    k = map[key]
    if k = invalid then return false
    ' The scene owns playback keys, except the arrows and OK while an overlay's buttons show (Back
    ' still goes to the scene, which stops the title).
    if m.nav.mode = "playing"
        if m.nav.overlay = invalid or k = "back" then return false
        stepWith({ type: "press", key: k })
        return true
    end if
    stepWith({ type: "press", key: k })
    return true
end function

' Level 2: the scene passes the Menu key (*) pressed during playback, with the title's position.
sub onMenuKey()
    stepWith({ type: "press", key: "menu", position: m.top.menuKey })
end sub

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
    if mode <> "menu" then m.menuTimer.control = "stop" ' the countdown belongs to a menu on screen
    ' While a title plays this view is over it (see MainScene openPlayer): only an overlay shows.
    m.top.findNode("black").visible = mode <> "playing"
    m.canvas.visible = mode <> "playing"
    renderOverlay()
    if mode = "menu"
        menu = m.manifest["menus"][m.nav.menu]
        if fresh or m.shownMenu <> m.nav.menu then showMenu(menu)
        drawButtons()
    else if mode = "transition"
        hideCursor()
    else if mode = "playing"
        hideCursor()
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
    buildButtons(menu)
    prefetchMenu(menu)

    m.motion.translation = [0, 0]
    m.motion.width = dw
    m.motion.height = dh
    video = bg["video"]
    m.motionShown = false
    if video <> invalid
        start = 0
        if not m.nav.intro then start = msOr(video["loopStart"], 0)
        playVideo(video["src"], start, "background", msOr(video["loopStart"], 0))
    else
        stopBackground()
    end if

    ' Roku plays one stream at a time, so with a motion background the video wins; its own audio
    ' is the menu's sound (spec §5.1: music that goes with a motion loop belongs in the clip).
    music = bg["audio"]
    if music <> invalid and video = invalid
        m.musicLoop = msOr(music["loopStart"], 0)
        start = 0
        if not m.nav.intro then start = m.musicLoop
        content = CreateObject("roSGNode", "ContentNode")
        content.url = assetUrl(music["src"])
        content.HttpHeaders = headerList(m.top.authHeaders)
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

' One layer per button, in the menu's order: a poster for its state image and, for showLabel
' buttons, a fill and the label the player draws (otherwise the label is part of the art).
sub buildButtons(menu as object)
    for each view in m.buttonViews
        m.wanted.Delete(view.poster.id)
    end for
    m.buttons.removeChildrenIndex(m.buttons.getChildCount(), 0)
    m.buttonViews = []
    m.seq = m.seq + 1
    i = 0
    for each button in menu["buttons"]
        view = { button: button }
        view.poster = m.buttons.createChild("Poster")
        view.poster.id = "b" + m.seq.ToStr() + "-" + i.ToStr()
        view.poster.loadDisplayMode = "scaleToFill"
        view.poster.visible = false
        if button["showLabel"] = true
            r = button["rect"]
            x = r[0] * m.sx
            y = r[1] * m.sy
            w = r[2] * m.sx
            h = r[3] * m.sy
            view.fill = m.buttons.createChild("Rectangle")
            view.fill.translation = [x, y]
            view.fill.width = w
            view.fill.height = h
            ' Font size half the button's height, as the web renderer, smaller if the text would be wider
            ' than the button; a soft shadow keeps it legible on art.
            size = fitFontSize(button["label"], w, h * 0.5)
            view.shadow = labelNode(button["label"], x + 2, y + 3, w, h, size)
            view.shadow.color = "0x000000CC"
            view.label = labelNode(button["label"], x, y, w, h, size)
        end if
        m.buttonViews.push(view)
        i = i + 1
    end for
end sub

' --- Level 2: overlays, buttons over the playing title ------------------------------------------

sub renderOverlay()
    overlay = invalid
    if m.nav.mode = "playing" then overlay = msNavOverlayOf(m.manifest, m.nav)
    id = invalid
    if overlay <> invalid then id = m.nav.overlay.cue
    if id <> m.shownOverlay
        m.shownOverlay = id
        for each view in m.overlayViews
            m.wanted.Delete(view.poster.id)
        end for
        m.overlayButtons.removeChildrenIndex(m.overlayButtons.getChildCount(), 0)
        m.overlayViews = []
        if overlay <> invalid
            canvas = overlay["canvas"]
            aspect = aspectOf(canvas["aspect"], canvas["width"] / canvas["height"])
            dw = 1920
            dh = 1920 / aspect
            if dh > 1080
                dh = 1080
                dw = 1080 * aspect
            end if
            m.overlay.translation = [(1920 - dw) / 2, (1080 - dh) / 2]
            m.osx = dw / canvas["width"]
            m.osy = dh / canvas["height"]
            m.seq = m.seq + 1
            i = 0
            for each button in overlay["buttons"]
                m.overlayViews.push(buttonView(m.overlayButtons, button, "o" + m.seq.ToStr() + "-" + i.ToStr(), m.osx, m.osy))
                i = i + 1
            end for
        end if
    end if
    m.overlay.visible = overlay <> invalid
    m.top.overlayShowing = overlay <> invalid
    drawOverlay()
end sub

sub drawOverlay()
    if m.nav.mode <> "playing" or m.nav.overlay = invalid then return
    for each view in m.overlayViews
        visual = "normal"
        if view.button["id"] = m.activatedId
            visual = "activated"
        else if view.button["id"] = m.nav.overlay.focus
            visual = "selected"
        end if
        drawView(view, visual, m.osx, m.osy)
    end for
end sub

' One button's layers (state image, and for showLabel a fill and label) in a group, scaled by sx/sy.
function buttonView(group as object, button as object, id as string, sx as float, sy as float) as object
    view = { button: button }
    view.poster = group.createChild("Poster")
    view.poster.id = id
    view.poster.loadDisplayMode = "scaleToFill"
    view.poster.visible = false
    if button["showLabel"] = true
        r = button["rect"]
        x = r[0] * sx
        y = r[1] * sy
        w = r[2] * sx
        h = r[3] * sy
        view.fill = group.createChild("Rectangle")
        view.fill.translation = [x, y]
        view.fill.width = w
        view.fill.height = h
        size = fitFontSize(button["label"], w, h * 0.5)
        view.shadow = labelNode(button["label"], x + 2, y + 3, w, h, size, group)
        view.shadow.color = "0x000000CC"
        view.label = labelNode(button["label"], x, y, w, h, size, group)
    end if
    return view
end function

' Show a button in one state: its image, rect, label colour and fill.
sub drawView(view as object, visual as string, sx as float, sy as float)
    button = view.button
    s = invalid
    if button["states"] <> invalid then s = button["states"][visual]
    if s = invalid then s = {}
    if s["image"] <> invalid
        rect = button["rect"]
        if s["rect"] <> invalid then rect = s["rect"]
        view.poster.translation = [rect[0] * sx, rect[1] * sy]
        view.poster.width = rect[2] * sx
        view.poster.height = rect[3] * sy
        showImage(view.poster, s["image"])
    else
        view.poster.visible = false
        m.wanted.Delete(view.poster.id)
    end if
    if view.label <> invalid
        ' Unstyled labels are 85% white, full white with focus (the web renderer's defaults).
        fallback = "0xFFFFFFD9"
        if visual = "selected" then fallback = "0xFFFFFFFF"
        view.label.color = cssColor(s["textColor"], fallback)
        view.fill.color = cssColor(s["fill"], "0x00000000")
        view.fill.visible = s["fill"] <> invalid
    end if
end sub

' The largest size up to maxSize at which the text fits in width (a little padding each side).
function fitFontSize(text as dynamic, width as float, maxSize as float) as float
    size = maxSize
    probe = CreateObject("roSGNode", "Label")
    probe.text = msOr(text, "")
    font = CreateObject("roSGNode", "Font")
    font.uri = "font:SystemFontFile"
    font.size = Int(size)
    probe.font = font
    textWidth = probe.boundingRect().width
    if textWidth > width * 0.94 and textWidth > 0 then size = size * width * 0.94 / textWidth
    if size < 12 then size = 12
    return size
end function

function labelNode(text as dynamic, x as float, y as float, w as float, h as float, size as float, group = invalid as dynamic) as object
    if group = invalid then group = m.buttons
    label = group.createChild("Label")
    label.text = msOr(text, "")
    label.translation = [x, y]
    label.width = w
    label.height = h
    label.horizAlign = "center"
    label.vertAlign = "center"
    font = CreateObject("roSGNode", "Font")
    font.uri = "font:SystemFontFile"
    font.size = Int(size)
    label.font = font
    return label
end function

' Show each button in exactly one state (spec: activated briefly when pressed, selected while it
' has focus, otherwise normal), each with its own image, rect, label colour and fill.
sub drawButtons()
    if m.nav.mode <> "menu" or not m.nav.buttonsReady or not menuShowing()
        hideCursor()
        return
    end if
    m.buttons.visible = true
    ring = invalid
    for each view in m.buttonViews
        button = view.button
        visual = "normal"
        if button["id"] = m.activatedId
            visual = "activated"
        else if button["id"] = m.nav.focus
            visual = "selected"
        end if
        s = invalid
        if button["states"] <> invalid then s = button["states"][visual]
        if s = invalid then s = {}
        if s["image"] <> invalid
            rect = button["rect"]
            if s["rect"] <> invalid then rect = s["rect"]
            view.poster.translation = [rect[0] * m.sx, rect[1] * m.sy]
            view.poster.width = rect[2] * m.sx
            view.poster.height = rect[3] * m.sy
            showImage(view.poster, s["image"])
        else
            view.poster.visible = false
            m.wanted.Delete(view.poster.id)
        end if
        if view.label <> invalid
            ' Unstyled labels are 85% white, full white with focus (the web renderer's defaults).
            fallback = "0xFFFFFFD9"
            if visual = "selected" then fallback = "0xFFFFFFFF"
            view.label.color = cssColor(s["textColor"], fallback)
            view.fill.color = cssColor(s["fill"], "0x00000000")
            view.fill.visible = s["fill"] <> invalid
        end if
        ' Without highlight art or a drawn label the viewer couldn't see focus, so draw a ring.
        if visual <> "normal" and view.label = invalid and stateImage(button, "selected") = invalid then ring = { rect: button["rect"], visual: visual }
    end for
    if ring = invalid
        m.ring.visible = false
    else
        showRing(ring.rect, ring.visual)
    end if
end sub

'  Whether the menu itself is on screen, so the cursor never floats over black while the still
' loads or the video buffers.
function menuShowing() as boolean
    bg = msOr(m.manifest["menus"][m.nav.menu]["background"], {})
    if bg["video"] <> invalid and m.motionShown = true then return true
    if bg["image"] <> invalid then return m.still.visible and m.still.loadStatus = "ready"
    return bg["video"] = invalid
end function

sub hideCursor()
    m.buttons.visible = false
    m.ring.visible = false
end sub

' A 4-pixel frame just outside the button's rect (canvas units scaled to the screen): yellow with
' focus, white while activated (the web renderer's ms-ring).
sub showRing(rect as object, visual = "selected" as string)
    t = 4
    x = rect[0] * m.sx - t
    y = rect[1] * m.sy - t
    w = rect[2] * m.sx + 2 * t
    h = rect[3] * m.sy + 2 * t
    sides = [["ringTop", x, y, w, t], ["ringBottom", x, y + h - t, w, t], ["ringLeft", x, y, t, h], ["ringRight", x + w - t, y, t, h]]
    colour = "0xFFD54AFF"
    if visual = "activated" then colour = "0xFFFFFFFF"
    for each side in sides
        node = m.ring.findNode(side[0])
        node.color = colour
        node.translation = [side[1], side[2]]
        node.width = side[3]
        node.height = side[4]
    end for
    m.ring.visible = true
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

' Play a menu clip. loopFrom (backgrounds only): where the loop restarts. Restarting a finished
' clip re-opens the stream (about 1.3 s of black on the onn Roku TV), so loops never let the clip
' finish: from 0 the player's own loop wraps internally; from later, the clip is sought back to the
' loop point just before its end, in the same stream (as the web renderer does).
sub playVideo(src as string, start as float, role as string, loopFrom = invalid as dynamic)
    m.videoRole = role
    m.loopFrom = loopFrom
    m.motion.loop = (loopFrom <> invalid and loopFrom = 0)
    ' Clips always stream. (Keeping a local copy for seamless loops was tried: downloading the 21 MB
    ' Matrix main-menu clip into tmp:, which is RAM on Roku, crashed the onn Roku TV.)
    content = CreateObject("roSGNode", "ContentNode")
    content.url = assetUrl(src)
    content.HttpHeaders = headerList(m.top.authHeaders)
    content.StreamFormat = "mp4"
    content.PlayStart = start
    m.motion.content = content
    ' While it buffers, a Video node draws black over everything below it, so a background clip
    ' stays hidden until it plays and the still (the poster) shows meanwhile.
    m.motion.visible = not (role = "background" and m.still.visible)
    m.motion.control = "play"
end sub

sub stopBackground()
    m.motion.control = "stop"
    m.motion.visible = false
    ' The still belongs to the menu shown: a menu without an image never shows the last one's.
    m.still.visible = false
    if m.shownMenu <> invalid then m.still.visible = msOr(m.manifest["menus"][m.shownMenu]["background"], {})["image"] <> invalid
    m.music.control = "stop"
    m.videoRole = invalid
end sub

sub onMotionState()
    state = m.motion.state
    ' The still is the poster shown while the video loads (spec §5.1). On Roku an opaque poster
    ' covers the video even from below it in the scene, so hide it once the video is showing.
    if state = "playing"
        m.motion.visible = true
        m.still.visible = false
    end if
    if state = "playing" and m.videoRole = "background" and m.motionShown <> true
        m.motionShown = true ' stays true through loop seeks, so the cursor doesn't blink
        drawButtons()
    end if
    if state = "finished"
        if m.videoRole = "transition"
            stepWith({ type: "transitionEnded" })
        else if m.videoRole = "background" and m.nav.mode = "menu"
            ' Loop from loopStart for as long as the menu is shown.
            menu = m.manifest["menus"][m.nav.menu]
            ' Fallback only: loops are normally handled before the clip finishes (see playVideo).
            loopFrom = msOr(menu["background"]["video"]["loopStart"], 0)
            playVideo(menu["background"]["video"]["src"], loopFrom, "background", loopFrom)
        end if
    else if state = "error"
        print "ovmplayer: menu video error "; m.motion.errorMsg
        if m.videoRole = "transition" then stepWith({ type: "transitionEnded" })
    end if
end sub

sub onMotionPosition()
    ' Loop from a point after 0: seek back just before the end, so the stream never closes.
    if m.videoRole = "background" and m.loopFrom <> invalid and m.loopFrom > 0
        duration = m.motion.duration
        if duration > 0 and m.motion.position >= duration - 0.3 then m.motion.seek = m.loopFrom
    end if
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
    task.request = { url: url, headers: m.top.authHeaders, saveTo: file }
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
        print "ovmplayer: couldn't load "; pending.url; " (HTTP "; task.response.code; ")"
        return
    end if
    m.images[pending.url] = task.file
    posters = [m.still]
    for each view in m.buttonViews
        posters.push(view.poster)
    end for
    for each poster in posters
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

' ["Name:Value", ...] for a ContentNode's HttpHeaders.
function headerList(h as object) as object
    list = []
    for each k in h
        list.push(k + ":" + h[k])
    end for
    return list
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
    return cssColor(css, "0x000000FF")
end function

' A CSS colour as Roku's 0xRRGGBBAA: #rgb, #rgba, #rrggbb, #rrggbbaa, rgb(), rgba() and a few
' names. Anything else gives the fallback.
function cssColor(css as dynamic, fallback as string) as string
    if css = invalid or (type(css) <> "String" and type(css) <> "roString") then return fallback
    c = LCase(css.Trim())
    names = { black: "#000000", white: "#ffffff", red: "#ff0000", green: "#008000", blue: "#0000ff", yellow: "#ffff00", gray: "#808080", grey: "#808080", transparent: "#00000000" }
    if names.DoesExist(c) then c = names[c]
    if Left(c, 1) = "#"
        hex = Mid(c, 2)
        if Len(hex) = 3 or Len(hex) = 4
            long = ""
            for i = 1 to Len(hex)
                long = long + Mid(hex, i, 1) + Mid(hex, i, 1)
            end for
            hex = long
        end if
        if Len(hex) = 6 then hex = hex + "ff"
        if Len(hex) <> 8 then return fallback
        for i = 1 to 8
            if Instr(1, "0123456789abcdef", Mid(hex, i, 1)) = 0 then return fallback
        end for
        return "0x" + UCase(hex)
    end if
    if Left(c, 4) = "rgb(" or Left(c, 5) = "rgba("
        inner = Mid(c, Instr(1, c, "(") + 1)
        inner = inner.Replace(")", "").Replace("/", ",")
        parts = []
        for each part in inner.Split(",")
            if part.Trim() <> "" then parts.push(part.Trim())
        end for
        if parts.Count() < 3 then return fallback
        out = "0x"
        for i = 0 to 2
            v = Val(parts[i])
            if Right(parts[i], 1) = "%" then v = v * 2.55
            out = out + hexByte(v)
        end for
        a = 255
        if parts.Count() > 3
            a = Val(parts[3])
            if Right(parts[3], 1) = "%" then a = a / 100
            a = a * 255
        end if
        return out + hexByte(a)
    end if
    return fallback
end function

function hexByte(v as float) as string
    n = Int(v + 0.5)
    if n < 0 then n = 0
    if n > 255 then n = 255
    return Right("0" + UCase(StrI(n, 16)), 2)
end function
