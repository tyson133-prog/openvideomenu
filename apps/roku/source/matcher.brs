' Matching manifest media paths to the media server's file paths: a port of the Android app's
' MenuMatcher/MenuIndex (apps/androidtv/.../menu/). The two machines see the same folders under
' different roots ("/media/Movies/The Matrix (1999)/x.mp4" vs "The Matrix (1999)/x.mp4"), so paths
' match by their trailing segments, case-insensitively: at least the file name plus its folder, so
' two films' "Trailer.mp4" never match each other.

function msSegments(path as string) as object
    parts = []
    for each seg in path.Replace("\", "/").Split("/")
        if seg <> "" then parts.push(LCase(seg))
    end for
    return parts
end function

' Number of equal trailing path segments.
function msSuffixScore(a as string, b as string) as integer
    x = msSegments(a)
    y = msSegments(b)
    n = 0
    while n < x.Count() and n < y.Count() and x[x.Count() - 1 - n] = y[y.Count() - 1 - n]
        n = n + 1
    end while
    return n
end function

function msPathsMatch(a as string, b as string) as boolean
    return msSuffixScore(a, b) >= 2
end function

' The manifests (catalog entries) whose media include `file`, best match first.
function msManifestsFor(file as string, entries as object) as object
    scored = []
    for each e in entries
        if e["valid"] = true and e["hasMenus"] = true
            best = 0
            for each media in e["media"]
                if media["path"] <> invalid
                    score = msSuffixScore(media["path"], file)
                    if score > best then best = score
                end if
            end for
            if best >= 2 then scored.push({ entry: e, score: best })
        end if
    end for
    scored.SortBy("score", "r")
    result = []
    for each s in scored
        result.push(s.entry)
    end for
    return result
end function

' For each manifest media id, the file (from `files`: [{ path, item, source }]) it refers to.
function msResolveMedia(entry as object, files as object) as object
    resolved = {}
    for each media in entry["media"]
        best = msBestFile(media["path"], files)
        if best <> invalid then resolved[media["id"]] = best
    end for
    return resolved
end function

' For each manifest media id with angle files (Level 2), the file for angle 2, 3, … (invalid where
' none matches; that angle then plays angle 1).
function msResolveAngles(entry as object, files as object) as object
    resolved = {}
    for each media in entry["media"]
        if media["angles"] <> invalid
            list = []
            for each angle in media["angles"]
                list.push(msBestFile(angle["path"], files))
            end for
            resolved[media["id"]] = list
        end if
    end for
    return resolved
end function

function msBestFile(path as dynamic, files as object) as dynamic
    if path = invalid then return invalid
    best = invalid
    bestScore = 1
    for each f in files
        score = msSuffixScore(path, f.path)
        if score > bestScore
            bestScore = score
            best = f
        end if
    end for
    return best
end function
