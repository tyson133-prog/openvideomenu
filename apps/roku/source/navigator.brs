' MenuStream menu navigation for Roku: a port of packages/core/src/navigator.ts.
'
' A pure state machine, step(manifest, state, event) -> { state, effects }: no SceneGraph, no I/O.
' The player feeds it remote keys and playback events and carries out the effects. The conformance
' cases in conformance/cases check it exactly as they check the TypeScript reference (see
' conformance.brs), so both behave the same.
'
' Manifest data is read with bracket access (m["menus"]) because ids are author-chosen strings.

function msNavInitialState() as object
    return { mode: "idle", history: [], intro: false, buttonsReady: true, vars: {} }
end function

function msNavStep(manifest as object, state as object, event as object) as object
    effects = []
    s = msCopyAA(state)
    s.history = msCopyArray(state.history)
    t = event["type"]

    if t = "start"
        s.history = []
        s.playing = invalid
        s.vars = msCopyAA(msOr(manifest["vars"], {}))
        if manifest["firstPlay"] <> invalid
            msNavRun(manifest, s, manifest["firstPlay"], effects, false)
        else
            msNavOpenRoot(manifest, s)
        end if

    else if t = "press" or t = "select" or t = "activate"
        ' v0.2: any key or click skips a transition or a menu intro; the viewer is always in control.
        ' Pointer hover ("select") is ignored meanwhile, so moving the mouse doesn't skip anything.
        waiting = (s.mode = "transition" or (s.mode = "menu" and not s.buttonsReady))
        if waiting and t = "select"
            ' ignore
        else if s.mode = "transition"
            msNavEndTransition(manifest, s)
        else if s.mode = "menu" and not s.buttonsReady
            s.buttonsReady = true
            effects.push({ type: "skipIntro", menu: s.menu })
        else
            msNavHandleInput(manifest, s, event, effects)
        end if

    else if t = "buttonsReady"
        if s.mode = "menu" then s.buttonsReady = true

    else if t = "transitionEnded"
        if s.mode = "transition" then msNavEndTransition(manifest, s)

    else if t = "ended"
        if s.mode = "playing" and s.playing <> invalid
            if s.playing.queue.count() > 0
                p = msCopyAA(s.playing)
                q = msCopyArray(p.queue)
                nextItem = q.shift()
                p.item = nextItem
                p.queue = q
                s.playing = p
                effects.push(msNavPlayEffect(manifest, nextItem, s.vars))
            else
                msNavFinishPlayback(manifest, s, effects, true, invalid)
            end if
        end if

    else if t = "stopped"
        if s.mode = "playing" then msNavFinishPlayback(manifest, s, effects, false, event["position"])
    end if

    return { state: s, effects: effects }
end function

sub msNavHandleInput(manifest as object, s as object, event as object, effects as object)
    t = event["type"]
    if t = "press"
        key = event["key"]
        if s.mode = "playing"
            ' Players report the position with "stopped"; a bare "back" stops at an unknown position.
            if key = "back" then msNavFinishPlayback(manifest, s, effects, false, invalid)
            return
        end if
        if s.mode <> "menu" then return
        if key = "enter"
            msNavActivate(manifest, s, s.focus, effects)
        else if key = "back"
            msNavRemoteBack(manifest, s, effects)
        else
            msNavMove(manifest, s, key, effects)
        end if
    else if t = "select"
        if s.mode <> "menu" or event["button"] = s.focus then return
        button = msNavFindButton(msNavMenu(manifest, s.menu), event["button"])
        if button <> invalid then msNavFocusButton(manifest, s, button, effects)
    else if t = "activate"
        if s.mode <> "menu" then return
        if msNavFindButton(msNavMenu(manifest, s.menu), event["button"]) <> invalid
            s.focus = event["button"]
            msNavActivate(manifest, s, event["button"], effects)
        end if
    end if
end sub

' --- helpers ---------------------------------------------------------------------------------

function msNavMenu(manifest as object, id as string) as object
    return manifest["menus"][id]
end function

function msNavFindButton(menu as object, id as dynamic) as dynamic
    if menu = invalid or id = invalid then return invalid
    for each b in menu["buttons"]
        if b["id"] = id then return b
    end for
    return invalid
end function

sub msNavOpenRoot(manifest as object, s as object)
    if manifest["rootMenu"] = invalid or manifest["menus"] = invalid
        s.mode = "fallback"
        s.menu = invalid
        s.focus = invalid
        return
    end if
    msNavEnterMenu(manifest, s, manifest["rootMenu"], invalid, true)
end sub

sub msNavEnterMenu(manifest as object, s as object, menuId as string, buttonId as dynamic, fresh as boolean)
    menu = msNavMenu(manifest, menuId)
    s.mode = "menu"
    s.menu = menuId
    if buttonId <> invalid
        s.focus = buttonId
    else if menu["defaultButton"] <> invalid
        s.focus = menu["defaultButton"]
    else
        s.focus = menu["buttons"][0]["id"]
    end if
    s.intro = fresh
    buttonsAt = msOr(menu["buttonsAt"], 0)
    s.buttonsReady = not (fresh and buttonsAt > 0)
    s.transitionTo = invalid
end sub

sub msNavEndTransition(manifest as object, s as object)
    target = s.transitionTo
    msNavEnterMenu(manifest, s, target.menu, target.button, true)
end sub

sub msNavFocusButton(manifest as object, s as object, button as object, effects as object)
    s.focus = button["id"]
    if button["autoAction"] = true then msNavActivate(manifest, s, button["id"], effects)
end sub

sub msNavMove(manifest as object, s as object, dir as string, effects as object)
    menu = msNavMenu(manifest, s.menu)
    current = msNavFindButton(menu, s.focus)
    targetId = invalid
    if current["nav"] <> invalid then targetId = current["nav"][dir]
    if targetId = invalid and menu["autoNav"] = true
        nearest = msNavNearestButton(menu, current, dir)
        if nearest <> invalid then targetId = nearest["id"]
    end if
    if targetId = invalid or targetId = current["id"] then return
    msNavFocusButton(manifest, s, msNavFindButton(menu, targetId), effects)
end sub

sub msNavActivate(manifest as object, s as object, buttonId as string, effects as object)
    button = msNavFindButton(msNavMenu(manifest, s.menu), buttonId)
    effects.push({ type: "activated", menu: s.menu, button: button["id"] })
    msNavRun(manifest, s, button["action"], effects, true)
end sub

' v0.2: the remote's Back key on a menu. A menu's back action says where Back goes (like a disc's
' "Go Up"), run as a step up: no history is added, and a target already in history unwinds it.
' Without back, Back returns through history, except on the root menu, where it leaves the disc
' however the viewer got there. Back never traps: if back leaves the viewer on the same menu, they
' step back (or leave, on the root menu).
sub msNavRemoteBack(manifest as object, s as object, effects as object)
    menu = msNavMenu(manifest, s.menu)
    if menu["back"] = invalid
        msNavBackWithoutTarget(manifest, s, effects)
        return
    end if
    from = s.menu
    msNavRunOpts(manifest, s, menu["back"], effects, false, true)
    if s.mode = "menu" and s.menu = from then msNavBackWithoutTarget(manifest, s, effects)
end sub

sub msNavBackWithoutTarget(manifest as object, s as object, effects as object)
    if s.menu = manifest["rootMenu"]
        effects.push({ type: "exit" })
    else
        msNavGoBack(manifest, s, effects)
    end if
end sub

sub msNavGoBack(manifest as object, s as object, effects as object)
    if s.history.count() = 0
        effects.push({ type: "exit" })
        return
    end if
    previous = s.history.pop()
    msNavEnterMenu(manifest, s, previous.menu, previous.focus, false)
end sub

function msNavCurrentPosition(s as object) as dynamic
    if s.mode = "menu" and s.menu <> invalid and s.focus <> invalid then return { menu: s.menu, focus: s.focus }
    return invalid
end function

sub msNavRun(manifest as object, s as object, action as object, effects as object, pushHistory as boolean)
    msNavRunOpts(manifest, s, action, effects, pushHistory, false)
end sub

' up: running a menu's back (see msNavRemoteBack): gotos unwind history instead of adding to it.
sub msNavRunOpts(manifest as object, s as object, action as object, effects as object, pushHistory as boolean, up as boolean)
    t = action["type"]
    if t = "goto"
        here = msNavCurrentPosition(s)
        if up
            ' Going up to a menu already in history: drop it and everything after it.
            i = s.history.Count() - 1
            while i >= 0 and s.history[i].menu <> action["menu"]
                i = i - 1
            end while
            if i >= 0
                kept = []
                for j = 0 to i - 1
                    kept.push(s.history[j])
                end for
                s.history = kept
            end if
        else if pushHistory and here <> invalid and action["history"] <> "replace"
            s.history.push(here)
        end if
        if action["transition"] <> invalid
            s.mode = "transition"
            s.transitionTo = { menu: action["menu"], button: action["button"] }
            effects.push({ type: "transition", src: action["transition"] })
        else
            msNavEnterMenu(manifest, s, action["menu"], action["button"], true)
        end if
    else if t = "back"
        msNavGoBack(manifest, s, effects)
    else if t = "play"
        msNavStartPlayback(manifest, s, [action], action["after"], effects)
    else if t = "playAll"
        msNavStartPlayback(manifest, s, action["items"], action["after"], effects)
    else if t = "resume"
        if s.lastStop <> invalid
            msNavStartPlayback(manifest, s, [{ media: s.lastStop.media, start: s.lastStop.position }], invalid, effects)
        end if
    else if t = "set"
        vars = msCopyAA(s.vars)
        vars[action["var"]] = action["value"]
        s.vars = vars
        if action["then"] <> invalid then msNavRunOpts(manifest, s, action["then"], effects, pushHistory, up)
    else if t = "activate"
        ' Press a button of the current menu, as if the viewer had: focus, activated state, action.
        if s.menu = invalid then return
        button = msNavFindButton(msNavMenu(manifest, s.menu), action["button"])
        if button = invalid then return
        s.focus = button["id"]
        effects.push({ type: "activated", menu: s.menu, button: button["id"] })
        msNavRunOpts(manifest, s, button["action"], effects, pushHistory, up)
    end if
end sub

sub msNavStartPlayback(manifest as object, s as object, items as object, after as dynamic, effects as object)
    queue = msCopyArray(items)
    first = queue.shift()
    s.playing = { item: first, queue: queue, after: after, returnTo: msNavCurrentPosition(s) }
    s.mode = "playing"
    effects.push(msNavPlayEffect(manifest, first, s.vars))
end sub

' completed: the item played to the end. Otherwise stoppedAt is the position (invalid = unknown).
sub msNavFinishPlayback(manifest as object, s as object, effects as object, completed as boolean, stoppedAt as dynamic)
    playing = s.playing
    s.playing = invalid
    if completed
        if s.lastStop <> invalid and s.lastStop.media = playing.item["media"] then s.lastStop = invalid
    else if stoppedAt <> invalid
        s.lastStop = { media: playing.item["media"], position: stoppedAt }
    end if

    ' Go back to where playback was started from, then run the after-action from there.
    if playing.returnTo <> invalid
        msNavEnterMenu(manifest, s, playing.returnTo.menu, playing.returnTo.focus, false)
    else
        msNavOpenRoot(manifest, s)
    end if
    if completed and playing.after <> invalid then msNavRun(manifest, s, playing.after, effects, true)
end sub

function msNavPlayEffect(manifest as object, item as object, vars as object) as object
    effect = { type: "play", media: item["media"], start: msNavStartTime(manifest, item) }
    audio = msNavVarOrNumber(item["audio"], vars)
    subtitle = msNavVarOrNumber(item["subtitle"], vars)
    if audio <> invalid then effect.audio = audio
    if subtitle <> invalid then effect.subtitle = subtitle
    return effect
end function

' A track number, or {"var": name} read from the session's variables.
function msNavVarOrNumber(v as dynamic, vars as object) as dynamic
    if v = invalid then return invalid
    if GetInterface(v, "ifAssociativeArray") <> invalid then return vars[v["var"]]
    return v
end function

' Start offset in seconds for a play item (chapter wins over start).
function msNavStartTime(manifest as object, item as object) as dynamic
    if item["chapter"] <> invalid
        media = manifest["media"][item["media"]]
        if media = invalid or media["chapters"] = invalid then return 0
        chapter = media["chapters"][item["chapter"] - 1]
        if chapter = invalid then return 0
        return chapter["time"]
    end if
    return msOr(item["start"], 0)
end function

' Nearest button in a direction, for menus with autoNav. Candidates must lie in the direction of
' travel (by centre); the score favours small distance along the axis and penalises sideways offset.
function msNavNearestButton(menu as object, from as object, dir as string) as dynamic
    fx = from["rect"][0] + from["rect"][2] / 2
    fy = from["rect"][1] + from["rect"][3] / 2
    best = invalid
    bestScore = 1e30
    for each b in menu["buttons"]
        if b["id"] <> from["id"]
            dx = b["rect"][0] + b["rect"][2] / 2 - fx
            dy = b["rect"][1] + b["rect"][3] / 2 - fy
            if dir = "left"
                along = -dx
                sideways = Abs(dy)
            else if dir = "right"
                along = dx
                sideways = Abs(dy)
            else if dir = "up"
                along = -dy
                sideways = Abs(dx)
            else
                along = dy
                sideways = Abs(dx)
            end if
            if along > 0
                score = along + 2 * sideways
                if score < bestScore
                    bestScore = score
                    best = b
                end if
            end if
        end if
    end for
    return best
end function

' --- small utilities ------------------------------------------------------------------------

function msOr(value as dynamic, fallback as dynamic) as dynamic
    if value = invalid then return fallback
    return value
end function

function msCopyAA(aa as object) as object
    copy = {}
    for each key in aa
        copy[key] = aa[key]
    end for
    return copy
end function

function msCopyArray(a as object) as object
    copy = []
    for each item in a
        copy.push(item)
    end for
    return copy
end function
