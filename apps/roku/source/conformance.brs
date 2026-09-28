' Runs the Open Video Menu conformance cases (conformance/cases, bundled at pkg:/conformance) against
' navigator.brs, like conformance/conformance.test.ts does for the TypeScript reference.
' Results go to the debug console (telnet <roku> 8085), one line per case:
'   MSCONF PASS 03-autonav.json
'   MSCONF FAIL 03-autonav.json step 2: focus is "tl", expected "tr"
'   MSCONF DONE passed=11 failed=0 skipped=7
' Only valid cases with steps are run; validation itself is the TypeScript validator's job.

sub msRunConformance()
    passed = 0
    failed = 0
    skipped = 0
    ' ListDir returns an roList, which has no Sort(): copy it into an array first.
    files = []
    for each name in ListDir("pkg:/conformance")
        files.push(name)
    end for
    files.Sort()
    for each file in files
        if LCase(Right(file, 5)) = ".json"
            c = ParseJson(ReadAsciiFile("pkg:/conformance/" + file))
            if c = invalid
                print "MSCONF FAIL " + file + ": not valid JSON"
                failed = failed + 1
            else if c["valid"] <> true or c["steps"] = invalid
                skipped = skipped + 1
            else
                problem = msConformanceCase(c)
                if problem = ""
                    print "MSCONF PASS " + file
                    passed = passed + 1
                else
                    print "MSCONF FAIL " + file + " " + problem
                    failed = failed + 1
                end if
            end if
        end if
    end for
    print "MSCONF DONE passed=" + passed.ToStr() + " failed=" + failed.ToStr() + " skipped=" + skipped.ToStr()
end sub

' "" when every step matches, else a description of the first mismatch.
function msConformanceCase(c as object) as string
    manifest = c["manifest"]
    state = msNavInitialState()
    i = 0
    for each stepCase in c["steps"]
        result = msNavStep(manifest, state, stepCase["event"])
        state = result.state
        want = stepCase["expect"]
        if want <> invalid
            for each field in ["mode", "menu", "focus", "intro", "buttonsReady"]
                if want.DoesExist(field) and not msDeepEqual(state[field], want[field])
                    return "step " + i.ToStr() + " (" + FormatJson(stepCase["event"]) + "): " + field + " is " + msShow(state[field]) + ", expected " + msShow(want[field]) + msMismatchNote()
                end if
            end for
            if want.DoesExist("effects") and not msDeepEqual(result.effects, want["effects"])
                return "step " + i.ToStr() + " (" + FormatJson(stepCase["event"]) + "): effects " + msShow(result.effects) + ", expected " + msShow(want["effects"]) + msMismatchNote()
            end if
        end if
        i = i + 1
    end for
    return ""
end function

' Structural equality: same keys and values, arrays in order, numbers by value (1 = 1.0).
function msDeepEqual(a as dynamic, b as dynamic) as boolean
    if a = invalid or b = invalid then return a = invalid and b = invalid
    aa = GetInterface(a, "ifAssociativeArray") <> invalid
    ba = GetInterface(b, "ifAssociativeArray") <> invalid
    if aa or ba
        if not (aa and ba) or a.Count() <> b.Count() then return false
        for each key in a
            if not b.DoesExist(key) or not msDeepEqual(a[key], b[key]) then return false
        end for
        return true
    end if
    al = GetInterface(a, "ifArray") <> invalid
    bl = GetInterface(b, "ifArray") <> invalid
    if al or bl
        if not (al and bl) or a.Count() <> b.Count() then return false
        for i = 0 to a.Count() - 1
            if not msDeepEqual(a[i], b[i]) then return false
        end for
        return true
    end if
    ' Values from ParseJson are boxed (roString, roBoolean, roInt) while literals are intrinsic
    ' (String, Boolean, Integer), so compare by kind, not by exact type.
    if msIsNumber(a) and msIsNumber(b) then return a = b
    if msIsString(a) and msIsString(b) then return a = b
    if msIsBoolean(a) and msIsBoolean(b) then return a = b
    m.msConformanceMismatch = type(a) + " vs " + type(b)
    return false
end function

function msIsBoolean(v as dynamic) as boolean
    t = type(v)
    return t = "Boolean" or t = "roBoolean"
end function

function msIsNumber(v as dynamic) as boolean
    t = type(v)
    return t = "Integer" or t = "roInt" or t = "roInteger" or t = "Float" or t = "roFloat" or t = "Double" or t = "roDouble" or t = "LongInteger" or t = "roLongInteger"
end function

function msIsString(v as dynamic) as boolean
    t = type(v)
    return t = "String" or t = "roString"
end function

function msShow(v as dynamic) as string
    if v = invalid then return "invalid"
    if msIsString(v) then return Chr(34) + v + Chr(34)
    if type(v) = "Boolean" or type(v) = "roBoolean"
        if v then return "true"
        return "false"
    end if
    if msIsNumber(v) then return Str(v).Trim()
    return FormatJson(v)
end function

function msMismatchNote() as string
    if m.msConformanceMismatch = invalid then return ""
    return " [types: " + m.msConformanceMismatch + "]"
end function
