sub init()
    m.top.functionName = "execute"
end sub

sub execute()
    req = m.top.request
    xfer = CreateObject("roUrlTransfer")
    port = CreateObject("roMessagePort")
    xfer.SetMessagePort(port)
    xfer.SetUrl(req.url)
    xfer.EnableEncodings(true)
    xfer.RetainBodyOnError(true)
    ' HTTPS (plex.tv, plex.direct addresses) needs Roku's certificate bundle.
    if LCase(Left(req.url, 6)) = "https:"
        xfer.SetCertificatesFile("common:/certs/ca-bundle.crt")
        xfer.InitClientCertificates()
    end if
    if req.headers <> invalid then xfer.SetHeaders(req.headers)
    method = "GET"
    if req.method <> invalid then method = req.method
    timeoutMs = 20000
    if req.timeoutMs <> invalid then timeoutMs = req.timeoutMs
    started = false
    if req.saveTo <> invalid
        started = xfer.AsyncGetToFile(req.saveTo)
    else if method = "POST" or method = "PUT"
        body = ""
        if req.body <> invalid then body = req.body
        if method = "PUT" then xfer.SetRequest("PUT")
        started = xfer.AsyncPostFromString(body)
    else
        started = xfer.AsyncGetToString()
    end if
    if not started
        m.top.response = { code: 0, body: "", error: "Couldn't start the request" }
        return
    end if
    msg = Wait(timeoutMs, port)
    if type(msg) <> "roUrlEvent"
        xfer.AsyncCancel()
        m.top.response = { code: 0, body: "", error: "Timed out" }
        return
    end if
    code = msg.GetResponseCode()
    body = msg.GetString()
    if body = invalid then body = ""
    m.top.response = { code: code, body: body, error: msg.GetFailureReason() }
end sub
