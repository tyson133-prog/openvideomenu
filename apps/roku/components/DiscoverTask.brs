sub init()
    m.top.functionName = "execute"
end sub

' The server's own idea of its address can be one the TV can't reach (e.g. a Docker container
' address), so the address used is where the answer came from, with the port it reports.
sub execute()
    found = {}
    list = []
    port = CreateObject("roMessagePort")
    socket = CreateObject("roDatagramSocket")
    socket.SetMessagePort(port)
    socket.SetBroadcast(true)
    socket.NotifyReadable(true)
    target = CreateObject("roSocketAddress")
    target.SetAddress("255.255.255.255:7359")
    socket.SetSendToAddress(target)
    socket.SendStr("who is JellyfinServer?")
    deadline = CreateObject("roTimespan")
    while deadline.TotalMilliseconds() < 2500
        msg = Wait(300, port)
        if type(msg) = "roSocketEvent" and socket.IsReadable()
            text = socket.ReceiveStr(4096)
            from = socket.GetReceivedFromAddress()
            reply = ParseJson(text)
            if reply <> invalid and reply["Id"] <> invalid and from <> invalid and not found.DoesExist(reply["Id"])
                portNum = "8096"
                address = reply["Address"]
                if address <> invalid
                    r = CreateObject("roRegex", ":(\d+)/?$", "")
                    mm = r.Match(address)
                    if mm.Count() > 1 then portNum = mm[1]
                end if
                found[reply["Id"]] = true
                ip = from.GetAddress() ' "192.168.1.20:7359"
                ip = Left(ip, Instr(1, ip, ":") - 1)
                list.push({ name: reply["Name"], url: "http://" + ip + ":" + portNum, id: reply["Id"] })
            end if
        end if
    end while
    socket.Close()
    m.top.servers = list
end sub
