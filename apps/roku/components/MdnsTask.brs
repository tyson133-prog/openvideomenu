' A legacy unicast mDNS query (RFC 6762 §6.7): sent from an ordinary port to the multicast group,
' and answered straight back to that port, so the app needs neither port 5353 nor group membership.
' The server's address is where the answer came from (like Jellyfin discovery), with the SRV port.

sub init()
    m.top.functionName = "execute"
end sub

sub execute()
    port = CreateObject("roMessagePort")
    socket = CreateObject("roDatagramSocket")
    socket.SetMessagePort(port)
    socket.NotifyReadable(true)
    target = CreateObject("roSocketAddress")
    target.SetAddress("224.0.0.251:5353")
    socket.SetSendToAddress(target)
    query = browseQuery("_openvideomenu._tcp.local")
    found = {}
    list = []
    deadline = CreateObject("roTimespan")
    sent = 0
    while deadline.TotalMilliseconds() < 3000
        ' Ask again after a second: UDP may drop the first query or its answer.
        if sent = 0 or (sent = 1 and deadline.TotalMilliseconds() > 1000)
            socket.Send(query, 0, query.Count())
            sent = sent + 1
        end if
        msg = Wait(250, port)
        if type(msg) = "roSocketEvent" and socket.IsReadable()
            data = CreateObject("roByteArray")
            data[8191] = 0
            n = socket.Receive(data, 0, 8192)
            from = socket.GetReceivedFromAddress()
            if n > 12 and from <> invalid
                ip = from.GetAddress()
                ip = Left(ip, Instr(1, ip, ":") - 1)
                for each s in parseServices(data, n)
                    key = ip + ":" + s.port.ToStr()
                    if not found.DoesExist(key)
                        found[key] = true
                        list.push({ name: s.name, url: "http://" + key })
                    end if
                end for
            end if
        end if
    end while
    socket.Close()
    m.top.servers = list
end sub

' A DNS query: ID 0, one question, name as length-prefixed labels, type PTR (12), class IN (1).
function browseQuery(name as string) as object
    b = CreateObject("roByteArray")
    for each v in [0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]
        b.Push(v)
    end for
    for each label in name.Split(".")
        bytes = CreateObject("roByteArray")
        bytes.FromAsciiString(label)
        b.Push(bytes.Count())
        b.Append(bytes)
    end for
    for each v in [0, 0, 12, 0, 1]
        b.Push(v)
    end for
    return b
end function

' Service instances in one answer: [{ name, port }], from its PTR and SRV records.
function parseServices(b as object, n as integer) as object
    at = 12
    for i = 1 to u16(b, 4) ' skip the echoed questions
        at = readName(b, at, n).next + 4
    end for
    counts = u16(b, 6) + u16(b, 8) + u16(b, 10) ' answers, authority, additionals
    instances = []
    ports = {}
    for i = 1 to counts
        if at + 10 > n then exit for
        owner = readName(b, at, n)
        at = owner.next
        rtype = u16(b, at)
        rdlen = u16(b, at + 8)
        rdata = at + 10
        if rtype = 12 and LCase(owner.name) = "_openvideomenu._tcp.local"
            instances.Push(readName(b, rdata, n).name)
        else if rtype = 33
            ports[LCase(owner.name)] = u16(b, rdata + 4) ' SRV: priority, weight, port, target
        end if
        at = rdata + rdlen
    end for
    out = []
    for each fqdn in instances
        p = ports[LCase(fqdn)]
        if p <> invalid then out.Push({ name: Left(fqdn, Instr(1, fqdn, "._openvideomenu.") - 1), port: p })
    end for
    return out
end function

function u16(b as object, i as integer) as integer
    return b[i] * 256 + b[i + 1]
end function

' A (possibly compressed) domain name at at: { name, next } where next follows it in the record.
function readName(b as object, at as integer, n as integer) as object
    labels = []
    nextPos = -1
    hops = 0
    while at < n and hops < 32
        length = b[at]
        if length = 0
            at = at + 1
            exit while
        else if length >= 192 ' pointer to an earlier name
            if nextPos < 0 then nextPos = at + 2
            at = (length - 192) * 256 + b[at + 1]
            hops = hops + 1
        else
            label = CreateObject("roByteArray")
            for i = at + 1 to at + length
                label.Push(b[i])
            end for
            labels.Push(label.ToAsciiString())
            at = at + 1 + length
        end if
    end while
    if nextPos < 0 then nextPos = at
    return { name: labels.Join("."), next: nextPos }
end function
