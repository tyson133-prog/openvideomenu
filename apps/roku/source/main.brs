' ovmplayer for Roku (proof of concept). Launched with conformance=1 (dev builds), it only runs
' the conformance cases and prints the results to the debug console.
sub Main(args as dynamic)
    ' Store builds leave the cases out (tools/package.mjs), so there test mode never starts.
    if args <> invalid and args["conformance"] = "1" and CreateObject("roFileSystem").Exists("pkg:/conformance")
        msRunConformance()
        return
    end if

    screen = CreateObject("roSGScreen")
    port = CreateObject("roMessagePort")
    screen.SetMessagePort(port)
    scene = screen.CreateScene("MainScene")
    screen.Show()
    ' Deep links (Roku search, the certification test): a title by its server ID, at launch or, through
    ' roInput, while the app runs. MainScene opens it once the library is ready.
    deepLink(scene, args)
    input = CreateObject("roInput")
    input.SetMessagePort(port)
    ' Roku certification (2026-10): apps must take low-memory warnings. The monitor needs Roku OS
    ' 15.2, a step above the minimum, so it may be missing; the device's general memory level is older.
    memory = CreateObject("roAppMemoryMonitor")
    if memory <> invalid
        memory.SetMessagePort(port)
        memory.EnableMemoryWarningEvent(true)
        limit = memory.GetChannelMemoryLimit()
        print "ovmplayer: memory limit "; limit.maxForegroundMemory; " KB, available "; memory.GetChannelAvailableMemory(); " KB"
    end if
    device = CreateObject("roDeviceInfo")
    device.SetMessagePort(port)
    device.EnableLowGeneralMemoryEvent(true)
    while true
        msg = Wait(0, port)
        kind = type(msg)
        if kind = "roSGScreenEvent" and msg.IsScreenClosed() then return
        if kind = "roInputEvent" and msg.IsInput() then deepLink(scene, msg.GetInfo())
        if kind = "roAppMemoryNotificationEvent"
            print "ovmplayer: memory at "; memory.GetMemoryLimitPercent(); "% of the app's limit, "; memory.GetChannelAvailableMemory(); " KB available"
        end if
        if kind = "roDeviceInfoEvent" and msg.GetInfo().generalMemoryLevel <> invalid
            print "ovmplayer: device memory "; msg.GetInfo().generalMemoryLevel
        end if
    end while
end sub

sub deepLink(scene as object, info as dynamic)
    if info = invalid or info.contentId = invalid or info.contentId = "" then return
    scene.deepLink = { contentId: info.contentId, mediaType: info.mediaType }
end sub
