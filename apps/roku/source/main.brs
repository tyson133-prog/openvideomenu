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
    ' Roku certification (2026-10): apps must take low-memory warnings. The monitor needs Roku OS
    ' 15.2, a step above the minimum, so it may be missing.
    memory = CreateObject("roAppMemoryMonitor")
    if memory <> invalid
        memory.SetMessagePort(port)
        memory.EnableMemoryWarningEvent(true)
    end if
    while true
        msg = Wait(0, port)
        if type(msg) = "roSGScreenEvent" and msg.IsScreenClosed() then return
        if type(msg) = "roAppMemoryNotificationEvent"
            print "ovmplayer: memory at "; memory.GetMemoryLimitPercent(); "% of the app's limit"
        end if
    end while
end sub
