' Open Video Menu navigation for Roku: a port of packages/core/src/navigator.ts.
'
' A pure state machine, step(manifest, state, event) -> { state, effects }: no SceneGraph, no I/O.
' The player feeds it remote keys and playback events and carries out the effects. The conformance
' cases in conformance/cases check it exactly as they check the TypeScript reference (see
' conformance.brs), so both behave the same.
'
' Manifest data is read with bracket access (m["menus"]) because ids are author-chosen strings.

function msNavInitialState() as object
    return { mode: "idle", history: [], intro: false, buttonsReady: true, vars: msNavNewVars(), player: msNavNewVars(), seed: 1&, seq: 0 }
end function

' Level 2: the most actions one event runs (spec 6.4), so a named action that runs itself still ends.
function msNavStepBudget() as integer
    return 10000
end function

function msNavStep(manifest as object, state as object, event as object) as object
    effects = []
    s = msCopyAA(state)
    s.history = msCopyArray(state.history)
    ' Actions this event may still run; not part of the state, so it goes before returning.
    s.stepsLeft = msNavStepBudget()
    ' Whether this event saved or cleared the resume point itself (Level 2); not part of the state either.
    s.resumeSet = false
    t = event["type"]

    if t = "start"
        s.history = []
        s.playing = invalid
        s.vars = msNavCopyVars(msOr(manifest["vars"], {}))
        ' What the player knows of the viewer's setup ("@" player state) and a random seed (Level 2).
        player = msNavCopyVars(s.player)
        if event["player"] <> invalid then player.Append(event["player"])
        s.player = player
        if event["seed"] <> invalid then s.seed = event["seed"] + 0&
        if manifest["firstPlay"] <> invalid
            msNavRun(manifest, s, manifest["firstPlay"], effects, false)
        else
            msNavOpenRoot(manifest, s, effects)
        end if

    else if t = "press" or t = "select" or t = "activate"
        ' Any key or click skips a transition or a menu intro; the viewer is always in control.
        ' Pointer hover ("select") is ignored meanwhile, so moving the mouse doesn't skip anything.
        waiting = (s.mode = "transition" or (s.mode = "menu" and not s.buttonsReady))
        inMenu = invalid
        if s.mode = "menu" then inMenu = s.menu
        if waiting and t = "select"
            ' ignore
        else if s.mode = "transition"
            msNavEndTransition(manifest, s, effects)
        else if s.mode = "menu" and not s.buttonsReady
            s.buttonsReady = true
            effects.push({ type: "skipIntro", menu: s.menu })
        else
            msNavHandleInput(manifest, s, event, effects)
        end if
        ' Input restarts the countdown of a menu still showing (unless restartOnInput is false), but
        ' not when the viewer is leaving (exit).
        if inMenu <> invalid and s.mode = "menu" and s.menu = inMenu and not (waiting and t = "select")
            timeout = msNavMenu(manifest, inMenu)["timeout"]
            restarted = false
            for each e in effects
                if e.type = "startTimer" or e.type = "exit" then restarted = true
            end for
            if timeout <> invalid and not restarted and timeout["restartOnInput"] <> false
                effects.push({ type: "startTimer", menu: inMenu, after: timeout["after"] })
            end if
        end if

    else if t = "timeout"
        ' Only for the menu still showing: a countdown from a menu the viewer has left is stale. Nobody
        ' chose this, so it runs as a step up, like a menu's back.
        if s.mode = "menu" and s.menu = event["menu"]
            timeout = msNavMenu(manifest, s.menu)["timeout"]
            if timeout <> invalid then msNavRunOpts(manifest, s, timeout["action"], effects, false, true)
        end if

    else if t = "buttonsReady"
        if s.mode = "menu" then s.buttonsReady = true

    else if t = "transitionEnded"
        if s.mode = "transition" then msNavEndTransition(manifest, s, effects)

    else if t = "ended"
        if s.mode = "playing" and s.playing <> invalid
            if s.playing.queue.count() > 0
                p = msCopyAA(s.playing)
                q = msCopyArray(p.queue)
                nextItem = q.shift()
                p.item = nextItem
                p.queue = q
                s.playing = p
                s.overlay = invalid
                effects.push(msNavPlaying(manifest, s, nextItem))
            else
                msNavFinishPlayback(manifest, s, effects, true, invalid)
            end if
        end if

    else if t = "stopped"
        if s.mode = "playing" then msNavFinishPlayback(manifest, s, effects, false, event["position"])

    else if t = "player"
        ' Level 2: player state changed outside the menus (e.g. the viewer picked another soundtrack).
        player = msNavCopyVars(s.player)
        player.Append(event["values"])
        s.player = player

    else if t = "cue"
        ' Level 2: playback passed a point cue's at (its action runs), or an overlay's from (its buttons show).
        if s.mode = "playing" and s.playing <> invalid
            cue = msNavCueById(manifest, s.playing.item, event["id"])
            if cue <> invalid and cue["at"] <> invalid
                msNavRunCue(manifest, s, cue["action"], effects, cue["at"])
            else if cue <> invalid
                ' A later overlay replaces one still showing.
                focus = msNavButtonRef(s, cue, cue["defaultButton"])
                if focus = invalid then focus = cue["buttons"][0]["id"]
                s.overlay = { cue: event["id"], focus: focus }
            end if
        end if

    else if t = "cueEnd"
        if s.overlay <> invalid and s.overlay.cue = event["id"] then s.overlay = invalid
    end if

    s.Delete("stepsLeft")
    s.Delete("resumeSet")
    return { state: s, effects: effects }
end function

sub msNavHandleInput(manifest as object, s as object, event as object, effects as object)
    t = event["type"]
    if t = "press"
        key = event["key"]
        if msNavIsMenuKey(key)
            msNavMenuKey(manifest, s, key, event["position"], effects)
            return
        end if
        if s.mode = "playing"
            ' Players report the position with "stopped"; a bare "back" stops at an unknown position.
            if key = "back"
                msNavFinishPlayback(manifest, s, effects, false, invalid)
            else if s.overlay <> invalid
                ' With an overlay showing, the arrows and Enter are its; otherwise they're the video player's.
                if key = "enter"
                    msNavActivateOverlay(manifest, s, s.overlay.focus, effects)
                else
                    msNavMoveOverlay(manifest, s, key, effects)
                end if
            end if
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
        if s.mode = "playing" and s.overlay <> invalid
            overlay = msNavOverlayOf(manifest, s)
            if overlay <> invalid and event["button"] <> s.overlay.focus and msNavFindButton(overlay, event["button"]) <> invalid
                s.overlay = { cue: s.overlay.cue, focus: event["button"] }
            end if
            return
        end if
        if s.mode <> "menu" or event["button"] = s.focus then return
        button = msNavFindButton(msNavMenu(manifest, s.menu), event["button"])
        if button <> invalid then msNavFocusButton(manifest, s, button, effects)
    else if t = "activate"
        if s.mode = "playing" and s.overlay <> invalid
            overlay = msNavOverlayOf(manifest, s)
            if overlay <> invalid and msNavFindButton(overlay, event["button"]) <> invalid then msNavActivateOverlay(manifest, s, event["button"], effects)
            return
        end if
        if s.mode <> "menu" then return
        if msNavFindButton(msNavMenu(manifest, s.menu), event["button"]) <> invalid
            s.focus = event["button"]
            msNavActivate(manifest, s, event["button"], effects)
        end if
    end if
end sub

' --- helpers ---------------------------------------------------------------------------------

' Level 2: the cues that apply while an item plays, with their ids: the media's, then the item's.
' Level 2: the remote's menu keys (spec 6.6).
function msNavIsMenuKey(key as dynamic) as boolean
    for each k in ["menu", "title", "audioMenu", "subtitleMenu", "angleMenu", "chapterMenu"]
        if key = k then return true
    end for
    return false
end function

' The action a keys table (or invalid) has for a key.
function msNavKeyAction(keys as dynamic, key as string) as dynamic
    if keys = invalid then return invalid
    return keys[key]
end function

' Level 2: a menu key (spec 6.6). During playback it runs like a cue's action at the position it was
' pressed: a menu it opens leaves playback, remembered for resume; playback it starts returns there.
' The item's keys win over the media's, the media's over the manifest's. On a menu, the menu's keys
' win over the manifest's, run as a step up. Without an action, Menu and Title open the root menu.
sub msNavMenuKey(manifest as object, s as object, key as string, position as dynamic, effects as object)
    fallback = invalid
    if (key = "menu" or key = "title") and manifest["rootMenu"] <> invalid then fallback = { type: "goto", menu: manifest["rootMenu"] }
    if s.mode = "playing" and s.playing <> invalid
        item = s.playing.item
        action = msNavKeyAction(item["keys"], key)
        media = manifest["media"][item["media"]]
        if action = invalid and media <> invalid then action = msNavKeyAction(media["keys"], key)
        if action = invalid then action = msNavKeyAction(manifest["keys"], key)
        if action = invalid then action = fallback
        if position = invalid then position = msNavStartTime(manifest, item)
        if action <> invalid then msNavRunCue(manifest, s, action, effects, position)
    else if s.mode = "menu" and s.menu <> invalid
        action = msNavKeyAction(msNavMenu(manifest, s.menu)["keys"], key)
        if action = invalid then action = msNavKeyAction(manifest["keys"], key)
        if action = invalid then action = fallback
        if action <> invalid then msNavRunOpts(manifest, s, action, effects, false, true)
    end if
end sub

function msNavCuesOf(manifest as object, item as object) as object
    list = []
    media = manifest["media"][item["media"]]
    if media <> invalid and media["cues"] <> invalid
        for i = 0 to media["cues"].Count() - 1
            list.push({ id: "media/" + i.ToStr(), cue: media["cues"][i] })
        end for
    end if
    if item["cues"] <> invalid
        for i = 0 to item["cues"].Count() - 1
            list.push({ id: "item/" + i.ToStr(), cue: item["cues"][i] })
        end for
    end if
    return list
end function

function msNavCueById(manifest as object, item as object, id as string) as dynamic
    for each c in msNavCuesOf(manifest, item)
        if c.id = id then return c.cue
    end for
    return invalid
end function

function msNavOverlayOf(manifest as object, s as object) as dynamic
    if s.overlay = invalid or s.playing = invalid then return invalid
    cue = msNavCueById(manifest, s.playing.item, s.overlay.cue)
    if cue = invalid or cue["from"] = invalid then return invalid
    return cue
end function

sub msNavMoveOverlay(manifest as object, s as object, dir as string, effects as object)
    overlay = msNavOverlayOf(manifest, s)
    if overlay = invalid then return
    current = msNavFindButton(overlay, s.overlay.focus)
    targetId = invalid
    if current["nav"] <> invalid then targetId = current["nav"][dir]
    if targetId = invalid and overlay["autoNav"] = true
        nearest = msNavNearestButton(overlay, current, dir)
        if nearest <> invalid then targetId = nearest["id"]
    end if
    if targetId = invalid or targetId = current["id"] then return
    s.overlay = { cue: s.overlay.cue, focus: targetId }
    if msNavFindButton(overlay, targetId)["autoAction"] = true then msNavActivateOverlay(manifest, s, targetId, effects)
end sub

' Press an overlay button: its action runs at the overlay's end time (where interrupted playback resumes).
sub msNavActivateOverlay(manifest as object, s as object, buttonId as string, effects as object)
    overlay = msNavOverlayOf(manifest, s)
    if overlay = invalid then return
    button = msNavFindButton(overlay, buttonId)
    if button = invalid then return
    s.overlay = { cue: s.overlay.cue, focus: button["id"] }
    effects.push({ type: "activated", cue: s.overlay.cue, button: button["id"] })
    msNavRunCue(manifest, s, button["action"], effects, overlay["to"])
end sub

' Run a cue's action during playback. s.cueAt (the time interrupted playback resumes from) is set only
' while it runs: playback it starts interrupts the current one, a goto leaves playback, back stops it.
sub msNavRunCue(manifest as object, s as object, action as object, effects as object, at as dynamic)
    s.cueAt = at
    msNavRunOpts(manifest, s, action, effects, false, false)
    s.Delete("cueAt")
end sub

' A cue opened a menu: playback ends, remembered for resume at the cue's time.
sub msNavLeavePlayback(manifest as object, s as object, at as dynamic)
    media = s.playing.item["media"]
    ' Unless the cue saved or cleared the resume point itself (a disc's CallSS names its resume cell).
    if not s.resumeSet then s.lastStop = msNavResumePointOf(s.playing, at)
    player = msNavCopyVars(s.player)
    player["@lastChapter"] = msNavChapterAt(manifest, media, at)
    s.player = player
    s.playing = invalid
    s.overlay = invalid
end sub

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

sub msNavOpenRoot(manifest as object, s as object, effects as object)
    if manifest["rootMenu"] = invalid or manifest["menus"] = invalid
        s.mode = "fallback"
        s.menu = invalid
        s.focus = invalid
        return
    end if
    msNavEnterMenu(manifest, s, manifest["rootMenu"], invalid, true, effects)
end sub

' A button reference (an id, or {"var": name} holding one) resolved to a button of the menu, if it is one.
function msNavButtonRef(s as object, menu as object, ref as dynamic) as dynamic
    if ref = invalid then return invalid
    id = ref
    if GetInterface(ref, "ifAssociativeArray") <> invalid then id = msNavText(msNavRead(s, ref["var"]))
    if msNavFindButton(menu, id) <> invalid then return id
    return invalid
end function

sub msNavEnterMenu(manifest as object, s as object, menuId as string, buttonId as dynamic, fresh as boolean, effects as object)
    menu = msNavMenu(manifest, menuId)
    s.mode = "menu"
    s.menu = menuId
    s.seq = s.seq + 1
    s.focus = msNavButtonRef(s, menu, buttonId)
    if s.focus = invalid then s.focus = msNavButtonRef(s, menu, menu["defaultButton"])
    if s.focus = invalid then s.focus = menu["buttons"][0]["id"]
    s.intro = fresh
    buttonsAt = msOr(menu["buttonsAt"], 0)
    s.buttonsReady = not (fresh and buttonsAt > 0)
    s.transitionTo = invalid
    ' Every time the menu appears (fresh or returned to), its countdown starts over (Level 2).
    if menu["timeout"] <> invalid then effects.push({ type: "startTimer", menu: menuId, after: menu["timeout"]["after"] })
end sub

sub msNavEndTransition(manifest as object, s as object, effects as object)
    target = s.transitionTo
    msNavEnterMenu(manifest, s, target.menu, target.button, true, effects)
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

' The remote's Back key on a menu. A menu's back action says where Back goes (its "Go Up"
' target), run as a step up: no history is added, and a target already in history unwinds it.
' Without back, Back returns through history, except on the root menu, where it leaves the title
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
    msNavEnterMenu(manifest, s, previous.menu, previous.focus, false, effects)
end sub

function msNavCurrentPosition(s as object) as dynamic
    if s.mode = "menu" and s.menu <> invalid and s.focus <> invalid then return { menu: s.menu, focus: s.focus }
    return invalid
end function

sub msNavRun(manifest as object, s as object, action as object, effects as object, pushHistory as boolean)
    msNavRunOpts(manifest, s, action, effects, pushHistory, false)
end sub

' up: running a menu's back (see msNavRemoteBack): gotos unwind history instead of adding to it.
' An action in tail position (set's then, if's branches, do's last step, resume's otherwise, a pressed
' button's action, a named action) continues this loop instead of nesting a call, so a disc's loop,
' written as a named action that runs itself, goes round as often as the budget allows.
sub msNavRunOpts(manifest as object, s as object, first as object, effects as object, pushHistory as boolean, up as boolean)
    action = first
    while action <> invalid
        if s.stepsLeft <= 0
            if s.stepsLeft = 0 then effects.push({ type: "budgetExceeded" })
            s.stepsLeft = -1
            return
        end if
        s.stepsLeft = s.stepsLeft - 1
        action = msNavRunOne(manifest, s, action, effects, pushHistory, up)
    end while
end sub

' Runs one action; returns the action in its tail position to run next, or invalid.
function msNavRunOne(manifest as object, s as object, action as object, effects as object, pushHistory as boolean, up as boolean) as dynamic
    t = action["type"]
    duringPlayback = s.cueAt <> invalid and s.mode = "playing" and s.playing <> invalid
    if t = "goto"
        ' A cue opening a menu leaves playback, remembered for resume where the cue is.
        if duringPlayback then msNavLeavePlayback(manifest, s, s.cueAt)
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
            ' A variable button is read now, when the viewer chose it.
            s.transitionTo = { menu: action["menu"], button: msNavButtonRef(s, msNavMenu(manifest, action["menu"]), action["button"]) }
            effects.push({ type: "transition", src: action["transition"] })
        else
            msNavEnterMenu(manifest, s, action["menu"], action["button"], true, effects)
        end if
    else if t = "back"
        ' From a cue: like the remote's Back during playback.
        if duringPlayback
            msNavFinishPlayback(manifest, s, effects, false, s.cueAt)
        else
            msNavGoBack(manifest, s, effects)
        end if
    else if t = "play"
        msNavStartPlayback(manifest, s, [action], action["after"], effects)
    else if t = "playAll"
        msNavStartPlayback(manifest, s, action["items"], action["after"], effects)
    else if t = "resume"
        if s.lastStop <> invalid
            ' The stopped playback continues: the item from where it stopped (with the tracks as they
            ' are now, which the viewer may have changed while watching), then what was queued after
            ' it, then its after-action. A disc's resume point is used up by resuming (Level 2).
            point = s.lastStop
            item = { media: point.media, start: point.position }
            if point.item <> invalid
                for each key in ["end", "cues"]
                    if point.item[key] <> invalid then item[key] = point.item[key]
                end for
            end if
            items = [item]
            if point.queue <> invalid then items.Append(point.queue)
            if manifest["resumePoint"] = "untilUsed" then s.lastStop = invalid
            msNavStartPlayback(manifest, s, items, point.after, effects)
        else
            ' Nothing to resume: the fallback runs as if it were the action itself (a DVD's "if
            ' stopped, LinkRSM, else go to the picker").
            return action["otherwise"]
        end if
    else if t = "set"
        name = action["var"]
        nextValue = msNavApplyOp(s, msNavRead(s, name), msOr(action["op"], "set"), msNavValueOf(s, action["value"]))
        if nextValue <> invalid
            if name = "@focus"
                ' Highlight a button of the current menu (a DVD's SetHL_BTN).
                id = msNavText(nextValue)
                if s.menu <> invalid and msNavFindButton(msNavMenu(manifest, s.menu), id) <> invalid then s.focus = id
            else if Left(name, 1) = "@"
                player = msNavCopyVars(s.player)
                player[name] = nextValue
                s.player = player
            else
                vars = msNavCopyVars(s.vars)
                vars[name] = nextValue
                s.vars = vars
            end if
        end if
        return action["then"]
    else if t = "if"
        if msNavEvalTest(s, action["test"]) then return action["then"]
        return action["else"]
    else if t = "do"
        ' In order, until a step navigates: opens a menu, starts playback or a transition, or leaves.
        steps = action["steps"]
        last = steps.Count() - 1
        for k = 0 to last - 1
            seq = s.seq
            mode = s.mode
            count = effects.Count()
            msNavRunOpts(manifest, s, steps[k], effects, pushHistory, up)
            navigated = s.seq <> seq or s.mode <> mode or s.stepsLeft < 0
            for i = count to effects.Count() - 1
                if effects[i].type = "exit" or effects[i].type = "transition" then navigated = true
            end for
            if navigated then return invalid
        end for
        if last >= 0 then return steps[last]
    else if t = "activate"
        ' Press a button of the current menu, as if the viewer had: focus, activated state, action.
        if s.menu = invalid then return invalid
        button = msNavFindButton(msNavMenu(manifest, s.menu), action["button"])
        if button = invalid then return invalid
        s.focus = button["id"]
        effects.push({ type: "activated", menu: s.menu, button: button["id"] })
        return button["action"]
    else if t = "saveResume"
        ' Level 2: media from chapter or at; without media, the playback in progress ("here"), like a
        ' disc's CallSS naming its resume cell.
        if action["media"] <> invalid
            at = msOr(action["at"], 0)
            if action["chapter"] <> invalid then at = msNavStartTime(manifest, { media: action["media"], chapter: action["chapter"] })
            s.lastStop = { media: action["media"], position: at }
        else if s.mode = "playing" and s.playing <> invalid
            at = msOr(action["at"], msOr(s.cueAt, 0))
            if action["chapter"] <> invalid then at = msNavStartTime(manifest, { media: s.playing.item["media"], chapter: action["chapter"] })
            s.lastStop = msNavResumePointOf(s.playing, at)
        end if
        s.resumeSet = true
    else if t = "clearResume"
        s.lastStop = invalid
        s.resumeSet = true
    else if t = "run"
        ' Level 2: as if the named action were written here: same history rules, same cue time.
        named = manifest["actions"]
        if named <> invalid then return named[action["action"]]
    end if
    return invalid
end function

' Started by a cue during playback (s.cueAt set): interrupts the current playback, which continues
' from the cue's time when this ends or is stopped (Level 2).
sub msNavStartPlayback(manifest as object, s as object, items as object, after as dynamic, effects as object)
    queue = msCopyArray(items)
    first = queue.shift()
    if s.cueAt <> invalid and s.mode = "playing" and s.playing <> invalid
        s.playing = { item: first, queue: queue, after: invalid, returnTo: s.playing.returnTo, resumeTo: { playing: s.playing, at: s.cueAt } }
    else
        s.playing = { item: first, queue: queue, after: after, returnTo: msNavCurrentPosition(s) }
    end if
    s.mode = "playing"
    s.overlay = invalid
    s.seq = s.seq + 1
    effects.push(msNavPlaying(manifest, s, first))
end sub

' The play effect for an item, noting it as the last playback (@lastMedia, @lastChapter).
' from: start elsewhere than the item's own start (a resumed interruption).
function msNavPlaying(manifest as object, s as object, item as object, from = invalid as dynamic) as object
    effect = msNavPlayEffect(manifest, item, s)
    if from <> invalid then effect.start = from
    player = msNavCopyVars(s.player)
    player["@lastMedia"] = item["media"]
    player["@lastChapter"] = msNavChapterAt(manifest, item["media"], effect.start)
    s.player = player
    return effect
end function

' The 1-based chapter a time falls in (0 when the media has no chapters).
function msNavChapterAt(manifest as object, media as string, seconds as dynamic) as integer
    info = manifest["media"][media]
    if info = invalid or info["chapters"] = invalid then return 0
    n = 0
    for i = 0 to info["chapters"].Count() - 1
        if info["chapters"][i]["time"] <= seconds then n = i + 1
    end for
    return n
end function

' completed: the item played to the end. Otherwise stoppedAt is the position (invalid = unknown).
sub msNavFinishPlayback(manifest as object, s as object, effects as object, completed as boolean, stoppedAt as dynamic)
    playing = s.playing
    s.playing = invalid
    s.overlay = invalid
    ' Playback a cue started: the interrupted one continues where the cue was (Level 2).
    if playing.resumeTo <> invalid
        if completed and msNavForgetsOnEnd(manifest, s, playing) then s.lastStop = invalid
        s.playing = playing.resumeTo.playing
        s.mode = "playing"
        s.seq = s.seq + 1
        effects.push(msNavPlaying(manifest, s, s.playing.item, playing.resumeTo.at))
        return
    end if
    if completed
        if msNavForgetsOnEnd(manifest, s, playing) then s.lastStop = invalid
    else if stoppedAt <> invalid
        s.lastStop = msNavResumePointOf(playing, stoppedAt)
        player = msNavCopyVars(s.player)
        player["@lastChapter"] = msNavChapterAt(manifest, playing.item["media"], stoppedAt)
        s.player = player
    end if

    ' Go back to where playback was started from, then run the after-action from there.
    if playing.returnTo <> invalid
        msNavEnterMenu(manifest, s, playing.returnTo.menu, playing.returnTo.focus, false, effects)
    else
        msNavOpenRoot(manifest, s, effects)
    end if
    if completed and playing.after <> invalid then msNavRun(manifest, s, playing.after, effects, true)
end sub

' The resume point for playback stopped (or left) at a time: the item, what's queued after it, its after-action.
function msNavResumePointOf(playing as object, at as dynamic) as object
    point = { media: playing.item["media"], position: at, item: playing.item }
    if playing.queue <> invalid and playing.queue.Count() > 0 then point.queue = playing.queue
    if playing.after <> invalid then point.after = playing.after
    return point
end function

' Playback of the resume point's media completed: forget it, unless it lasts until used (a disc's).
function msNavForgetsOnEnd(manifest as object, s as object, playing as object) as boolean
    if manifest["resumePoint"] = "untilUsed" or s.lastStop = invalid then return false
    return s.lastStop.media = playing.item["media"]
end function

' Without tracks of its own, playback uses the current ones (after a set on @audio/@subtitle, like a
' DVD's SetSTN, or as the player reported them).
function msNavPlayEffect(manifest as object, item as object, s as object) as object
    effect = { type: "play", media: item["media"], start: msNavStartTime(manifest, item) }
    audio = msNavVarOrNumber(item["audio"], s)
    if audio = invalid
        current = s.player["@audio"]
        if msNavIsNumber(current)
            if current > 0 then audio = current
        end if
    end if
    subtitle = msNavVarOrNumber(item["subtitle"], s)
    if subtitle = invalid and msNavIsNumber(s.player["@subtitle"]) then subtitle = s.player["@subtitle"]
    if audio <> invalid then effect.audio = audio
    if subtitle <> invalid then effect.subtitle = subtitle
    ' Media with angles plays the item's angle, otherwise @angle; one it doesn't have plays angle 1.
    angles = msNavAngleCount(manifest, item["media"])
    if angles > 1
        wanted = msNavVarOrNumber(item["angle"], s)
        if wanted = invalid then wanted = msNavRead(s, "@angle")
        if not msNavIsNumber(wanted) then wanted = 1
        if wanted < 1 or wanted > angles then wanted = 1
        effect.angle = wanted
    end if
    if item["end"] <> invalid then effect["end"] = item["end"]
    cues = []
    for each c in msNavCuesOf(manifest, item)
        if c.cue["at"] <> invalid
            cues.push({ id: c.id, at: c.cue["at"] })
        else
            cues.push({ id: c.id, from: c.cue["from"], to: c.cue["to"] })
        end if
    end for
    if cues.Count() > 0 then effect.cues = cues
    ' What the source forbids (the media's, then the item's), for players that honour it (spec 6.6).
    prohibits = []
    media = manifest["media"][item["media"]]
    for each list in [msOr(media["prohibits"], []), msOr(item["prohibits"], [])]
        for each name in list
            seen = false
            for each have in prohibits
                if have = name then seen = true
            end for
            if not seen then prohibits.push(name)
        end for
    end for
    if prohibits.Count() > 0 then effect.prohibits = prohibits
    return effect
end function

' How many angles a media item has (spec §4.1): 1 when it has none.
function msNavAngleCount(manifest as object, media as string) as integer
    info = manifest["media"][media]
    if info = invalid then return 1
    ' Made of segments: a play's angle applies to the segments whose media have angles (spec 4.2).
    if info["segments"] <> invalid
        most = 1
        for each seg in info["segments"]
            src = manifest["media"][seg["media"]]
            if src <> invalid and src["segments"] = invalid
                n = msNavAngleCount(manifest, seg["media"])
                if n > most then most = n
            end if
        end for
        return most
    end if
    if info["angles"] <> invalid then return 1 + info["angles"].Count()
    if info["angleTracks"] <> invalid then return info["angleTracks"]
    return 1
end function

' A track number, or {"var": name} holding one.
function msNavVarOrNumber(v as dynamic, s as object) as dynamic
    if v = invalid then return invalid
    if GetInterface(v, "ifAssociativeArray") <> invalid
        n = msNavRead(s, v["var"])
        if msNavIsNumber(n) then return n
        return invalid
    end if
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

' --- logic layer (Level 2; a port of packages/core/src/logic.ts) -----------------------------

' What an unset player-state variable reads as.
function msNavPlayerDefault(name as string) as dynamic
    defaults = { "@audio": 0, "@subtitle": 0, "@angle": 1, "@focus": "", "@audioLanguage": "", "@subtitleLanguage": "", "@menuLanguage": "", "@lastMedia": "", "@lastChapter": 0, "@region": 0, "@parentalLevel": 8, "@country": "", "@aspect": "16:9", "@resumeMedia": "" }
    if defaults.DoesExist(name) then return defaults[name]
    return 0
end function

function msNavRead(s as object, name as string) as dynamic
    if name = "@focus" then return msOr(s.focus, "")
    if name = "@resumeMedia"
        if s.lastStop = invalid then return ""
        return s.lastStop.media
    end if
    if Left(name, 1) = "@"
        if s.player.DoesExist(name) then return s.player[name]
        return msNavPlayerDefault(name)
    end if
    if s.vars.DoesExist(name) then return s.vars[name]
    return 0
end function

function msNavValueOf(s as object, v as dynamic) as dynamic
    if GetInterface(v, "ifAssociativeArray") <> invalid then return msNavRead(s, v["var"])
    return v
end function

function msNavEvalTest(s as object, t as object) as boolean
    if t["all"] <> invalid
        for each x in t["all"]
            if not msNavEvalTest(s, x) then return false
        end for
        return true
    end if
    if t["any"] <> invalid
        for each x in t["any"]
            if msNavEvalTest(s, x) then return true
        end for
        return false
    end if
    if t["not"] <> invalid then return not msNavEvalTest(s, t["not"])
    lhs = msNavRead(s, t["var"])
    for each op in ["eq", "ne", "lt", "le", "gt", "ge", "has"]
        if t.DoesExist(op)
            rhs = msNavValueOf(s, t[op])
            same = msNavSame(lhs, rhs)
            if op = "eq" then return same
            if op = "ne" then return not same
            if not (msNavIsNumber(lhs) and msNavIsNumber(rhs)) then return false
            if op = "has" then return (lhs and rhs) <> 0
            if op = "lt" then return lhs < rhs
            if op = "le" then return lhs <= rhs
            if op = "gt" then return lhs > rhs
            return lhs >= rhs
        end if
    end for
    return false
end function

' Equal values of the same kind: an integer never equals text (and comparing them with = is an error here).
function msNavSame(a as dynamic, b as dynamic) as boolean
    if msNavIsNumber(a) and msNavIsNumber(b) then return a = b
    if msNavIsText(a) and msNavIsText(b) then return a = b
    return false
end function

' The next random number 1..max: seed = (seed * 1103515245 + 12345) mod 2^31, result 1 + seed mod max.
' Specified exactly so every navigator agrees; LongInteger keeps the product exact.
function msNavNextRandom(s as object, limit as integer) as integer
    s.seed = (s.seed * 1103515245& + 12345&) mod 2147483648&
    return CInt(1 + s.seed mod limit)
end function

' The new value after set, or invalid when the operation leaves the variable unchanged.
function msNavApplyOp(s as object, current as dynamic, op as string, operand as dynamic) as dynamic
    if op = "set" then return operand
    if not (msNavIsNumber(current) and msNavIsNumber(operand)) then return invalid
    if op = "add" then return current + operand
    if op = "sub" then return current - operand
    if op = "mul" then return current * operand
    if op = "div" or op = "mod"
        if operand = 0 then return invalid
        ' Division truncates toward zero; mod keeps the dividend's sign.
        q = msNavAbs(current) \ msNavAbs(operand)
        if (current < 0) <> (operand < 0) then q = -q
        if op = "div" then return q
        return current - q * operand
    end if
    if op = "and" then return current and operand
    if op = "or" then return current or operand
    if op = "xor" then return (current or operand) and not (current and operand)
    if op = "random"
        if operand < 1 then return invalid
        return msNavNextRandom(s, operand)
    end if
    return invalid
end function

function msNavAbs(n as dynamic) as dynamic
    if n < 0 then return -n
    return n
end function

function msNavIsNumber(v as dynamic) as boolean
    t = type(v)
    return t = "Integer" or t = "roInt" or t = "roInteger" or t = "LongInteger" or t = "roLongInteger"
end function

function msNavIsText(v as dynamic) as boolean
    t = type(v)
    return t = "String" or t = "roString"
end function

function msNavText(v as dynamic) as string
    if msNavIsText(v) then return v
    if msNavIsNumber(v) then return v.ToStr()
    return ""
end function

' Variable names are the author's and case-sensitive, unlike a plain BrightScript object's keys.
function msNavNewVars() as object
    vars = {}
    vars.SetModeCaseSensitive()
    return vars
end function

function msNavCopyVars(from as object) as object
    vars = msNavNewVars()
    for each key in from
        vars[key] = from[key]
    end for
    return vars
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
