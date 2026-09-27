sub showContent()
    c = m.top.itemContent
    ' Tiles are reused as rows scroll: follow only this content's second line (progress changes
    ' after playback).
    if m.content <> invalid then m.content.unobserveFieldScoped("shortDescriptionLine2")
    m.content = c
    c.observeFieldScoped("shortDescriptionLine2", "showSub")
    w = 264
    h = 396
    if c.wide = true
        w = 400
        h = 225
    end if
    for each id in ["frame", "poster"]
        m.top.findNode(id).width = w
        m.top.findNode(id).height = h
    end for
    placeholder = m.top.findNode("placeholder")
    placeholder.width = w - 24
    placeholder.height = h
    m.top.findNode("badge").translation = [w - 264, 0]
    m.top.findNode("badge").visible = (c.badge = true)
    for each id in ["title", "sub"]
        m.top.findNode(id).width = w
    end for
    m.top.findNode("title").translation = [0, h + 8]
    m.top.findNode("sub").translation = [0, h + 40]
    m.top.findNode("poster").uri = c.HDPosterUrl
    m.top.findNode("title").text = c.title
    ' Under the poster: seen while it loads, or when there's no image (episodes have their title below).
    placeholder.visible = c.wide <> true
    placeholder.text = c.title
    showSub()
end sub

sub showSub()
    m.top.findNode("sub").text = m.content.shortDescriptionLine2
end sub
