sub showContent()
    c = m.top.itemContent
    m.top.findNode("poster").uri = c.HDPosterUrl
    m.top.findNode("title").text = c.title
    m.top.findNode("sub").text = c.shortDescriptionLine2
    m.top.findNode("placeholder").visible = (c.HDPosterUrl = invalid or c.HDPosterUrl = "")
    m.top.findNode("placeholder").text = c.title
end sub
