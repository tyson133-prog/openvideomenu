# The installer's helper: the work that's easier in PowerShell than in Inno Setup's Pascal (talking to Plex and
# Jellyfin, JSON, the service account, file permissions, the firewall). The installer extracts it to a temporary
# folder and runs one command at a time:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File setup-helper.ps1 -Command <name> -In <file> -Out <file>
#
# -In is a KEY=VALUE file the installer writes (so secrets never appear on a command line); -Out is where the
# result goes, one line per value. The exit code is 0 on success; on failure the first line of -Out is the reason.
param(
    [Parameter(Mandatory = $true)][string]$Command,
    [string]$In,
    [string]$Out
)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ServiceId = "OpenVideoMenuServer"
$RuleName = "Open Video Menu menu server"
$DataDir = Join-Path $env:ProgramData "Open Video Menu"
$ConfigFile = Join-Path $DataDir "menu-server.env"
$LogDir = Join-Path $DataDir "logs"
$ClientName = "Open Video Menu menu server"

function Read-Input {
    $values = @{}
    if ($In -and (Test-Path -LiteralPath $In)) {
        foreach ($line in Get-Content -LiteralPath $In -Encoding UTF8) {
            $i = $line.IndexOf("=")
            if ($i -gt 0) { $values[$line.Substring(0, $i)] = $line.Substring($i + 1) }
        }
    }
    return $values
}

function Write-Output-Lines([string[]]$lines) {
    if ($Out) { [IO.File]::WriteAllLines($Out, $lines, (New-Object Text.UTF8Encoding($true))) }
}

# Plex's headers for a client: who is asking, and the token when there is one.
function Plex-Headers($clientId, $token) {
    $h = @{
        "Accept"                   = "application/json"
        "X-Plex-Product"           = $ClientName
        "X-Plex-Client-Identifier" = $clientId
    }
    if ($token) { $h["X-Plex-Token"] = $token }
    return $h
}

function Jellyfin-Auth($token) {
    $a = "MediaBrowser Client=`"$ClientName`", Device=`"$env:COMPUTERNAME`", DeviceId=`"ovm-menu-server-setup`", Version=`"1.0`""
    if ($token) { $a += ", Token=`"$token`"" }
    return @{ "Authorization" = $a }
}

# A local folder on a drive letter mapped to a share (from the installing user's persistent mappings, which an
# elevated installer can't see any other way) becomes the share's \\server\share path.
function Mapped-Drives {
    $map = @{}
    if (Test-Path "HKCU:\Network") {
        foreach ($k in Get-ChildItem "HKCU:\Network") {
            $remote = (Get-ItemProperty $k.PSPath).RemotePath
            if ($remote) { $map[$k.PSChildName.ToUpper()] = $remote }
        }
    }
    return $map
}

function Grant($path, [string[]]$grants) {
    # Only these accounts, nothing inherited: the settings hold a Plex token or a Jellyfin API key.
    & icacls $path /inheritance:r @($grants | ForEach-Object { "/grant:r"; $_ }) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed on $path" }
}

$v = Read-Input
try {
    switch ($Command) {
        "plex-local-token" {
            # Plex on this PC keeps its sign-in in the user's registry.
            $key = "HKCU:\Software\Plex, Inc.\Plex Media Server"
            $token = if (Test-Path $key) { (Get-ItemProperty $key).PlexOnlineToken } else { $null }
            if (-not $token) { throw "Plex isn't signed in on this PC." }
            Write-Output-Lines @($token)
        }
        "plex-pin-start" {
            $r = Invoke-RestMethod -Method Post -Uri "https://plex.tv/api/v2/pins?strong=false" -Headers (Plex-Headers $v.CLIENT_ID $null)
            Write-Output-Lines @([string]$r.id, [string]$r.code)
        }
        "plex-pin-check" {
            $r = Invoke-RestMethod -Uri "https://plex.tv/api/v2/pins/$($v.PIN_ID)" -Headers (Plex-Headers $v.CLIENT_ID $null)
            if (-not $r.authToken) { throw "The code hasn't been entered at plex.tv/link yet." }
            Write-Output-Lines @([string]$r.authToken)
        }
        "plex-folders" {
            $r = Invoke-RestMethod -Uri ($v.URL.TrimEnd("/") + "/library/sections") -Headers (Plex-Headers $v.CLIENT_ID $v.TOKEN) -TimeoutSec 15
            $paths = @()
            foreach ($d in $r.MediaContainer.Directory) {
                if ($d.type -in @("movie", "show")) { foreach ($l in $d.Location) { $paths += [string]$l.path } }
            }
            if ($paths.Count -eq 0) { throw "Plex has no movie or TV libraries." }
            Write-Output-Lines ($paths | Select-Object -Unique)
        }
        "jellyfin-key" {
            $base = $v.URL.TrimEnd("/")
            $body = @{ Username = $v.USER; Pw = $v.PASSWORD } | ConvertTo-Json
            $auth = Invoke-RestMethod -Method Post -Uri "$base/Users/AuthenticateByName" -Headers (Jellyfin-Auth $null) -ContentType "application/json" -Body $body -TimeoutSec 15
            if (-not $auth.User.Policy.IsAdministrator) { throw "$($v.USER) isn't an administrator of this Jellyfin server." }
            $h = Jellyfin-Auth $auth.AccessToken
            $app = "Open Video Menu menu server"
            Invoke-RestMethod -Method Post -Uri ("$base/Auth/Keys?app=" + [Uri]::EscapeDataString($app)) -Headers $h | Out-Null
            $keys = Invoke-RestMethod -Uri "$base/Auth/Keys" -Headers $h
            $key = $keys.Items | Where-Object { $_.AppName -eq $app } | Sort-Object DateCreated -Descending | Select-Object -First 1
            if (-not $key) { throw "Jellyfin didn't create the API key." }
            Write-Output-Lines @([string]$key.AccessToken)
        }
        "jellyfin-folders" {
            $r = Invoke-RestMethod -Uri ($v.URL.TrimEnd("/") + "/Library/VirtualFolders") -Headers (Jellyfin-Auth $v.TOKEN) -TimeoutSec 15
            $paths = @()
            foreach ($f in $r) {
                if ($f.CollectionType -in @("movies", "tvshows", "mixed", $null)) { $paths += @($f.Locations | ForEach-Object { [string]$_ }) }
            }
            if ($paths.Count -eq 0) { throw "Jellyfin has no movie or TV libraries." }
            Write-Output-Lines ($paths | Select-Object -Unique)
        }
        "classify" {
            # One line per folder from -In (FOLDER1=…, FOLDER2=…): "share|<path>", "mapped|<path>|<\\server\share>"
            # or "local|<path>". Folders that look like another OS's paths (/data/movies) are "other".
            $drives = Mapped-Drives
            $lines = @()
            foreach ($k in ($v.Keys | Where-Object { $_ -like "FOLDER*" } | Sort-Object { [int]($_.Substring(6)) })) {
                $p = $v[$k]
                if ($p.StartsWith("\\")) { $lines += "share|$p" }
                elseif ($p -match "^([A-Za-z]):") {
                    $letter = $Matches[1].ToUpper()
                    if ($drives.ContainsKey($letter)) { $lines += "mapped|$p|$($drives[$letter])" } else { $lines += "local|$p" }
                }
                else { $lines += "other|$p" }
            }
            Write-Output-Lines $lines
        }
        "check-account" {
            # Is this a real Windows account with this password? (LogonUser, without starting anything.)
            Add-Type -Namespace OVM -Name Logon -MemberDefinition @"
[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern bool LogonUser(string user, string domain, string password, int type, int provider, out IntPtr token);
[DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
"@
            $parts = $v.ACCOUNT.Split("\", 2)
            $domain = if ($parts.Count -eq 2) { $parts[0] } else { "." }
            $user = $parts[-1]
            $token = [IntPtr]::Zero
            # LOGON32_LOGON_NETWORK (3) checks the password without needing interactive or service rights.
            if (-not [OVM.Logon]::LogonUser($user, $domain, $v.PASSWORD, 3, 0, [ref]$token)) { throw "That Windows user name or password isn't right." }
            [OVM.Logon]::CloseHandle($token) | Out-Null
            Write-Output-Lines @("ok")
        }
        "write-config" {
            # Every KEY=VALUE from -In except the account goes into the server's settings file.
            New-Item -ItemType Directory -Force -Path $DataDir, $LogDir | Out-Null
            $account = if ($v.ACCOUNT) { $v.ACCOUNT } else { "NT AUTHORITY\LocalService" }
            $lines = @("# Open Video Menu menu server settings, written by its installer. Run the installer again to change them.")
            foreach ($k in @("PLEX_URL", "PLEX_TOKEN", "JELLYFIN_URL", "JELLYFIN_API_KEY", "MEDIA_ROOTS", "PATH_MAP", "SERVER_NAME", "PORT")) {
                if ($v[$k]) { $lines += "$k=$($v[$k])" }
            }
            [IO.File]::WriteAllLines($ConfigFile, $lines, (New-Object Text.UTF8Encoding($false)))
            Grant $ConfigFile @("*S-1-5-18:F", "*S-1-5-32-544:F", "${account}:R")
            Grant $LogDir @("*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "${account}:(OI)(CI)M")
            Write-Output-Lines @($ConfigFile)
        }
        "install-service" {
            $app = $v.APP_DIR
            $exe = Join-Path $app "$ServiceId.exe"
            $xml = Join-Path $app "$ServiceId.xml"
            $template = Get-Content -LiteralPath (Join-Path $app "$ServiceId.template.xml") -Raw -Encoding UTF8
            # Stop and remove an earlier install first (running setup again changes the settings).
            if (Get-Service -Name $ServiceId -ErrorAction SilentlyContinue) {
                & $exe stop | Out-Null
                & $exe uninstall | Out-Null
                Start-Sleep -Seconds 2
            }
            $account = ""
            if ($v.ACCOUNT) {
                $parts = $v.ACCOUNT.Split("\", 2)
                $domain = if ($parts.Count -eq 2) { $parts[0] } else { "." }
                $esc = { param($s) [Security.SecurityElement]::Escape($s) }
                # WinSW reads the password only while installing (and grants "Log on as a service"); Windows'
                # service manager keeps it from then on. The file is rewritten without it right after.
                $account = "<serviceaccount><domain>$(& $esc $domain)</domain><user>$(& $esc $parts[-1])</user><password>$(& $esc $v.PASSWORD)</password><allowservicelogon>true</allowservicelogon></serviceaccount>"
            }
            [IO.File]::WriteAllText($xml, $template.Replace("<!--ACCOUNT-->", $account), (New-Object Text.UTF8Encoding($false)))
            try {
                & $exe install | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "Installing the service failed." }
            }
            finally {
                [IO.File]::WriteAllText($xml, $template.Replace("<!--ACCOUNT-->", ""), (New-Object Text.UTF8Encoding($false)))
            }
            if (-not $v.ACCOUNT) {
                & sc.exe config $ServiceId obj= "NT AUTHORITY\LocalService" password= "" | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "Setting the service's account failed." }
            }
            & $exe start | Out-Null
            Write-Output-Lines @("ok")
        }
        "remove-service" {
            $exe = Join-Path $v.APP_DIR "$ServiceId.exe"
            if (Get-Service -Name $ServiceId -ErrorAction SilentlyContinue) {
                if (Test-Path $exe) { & $exe stop | Out-Null; & $exe uninstall | Out-Null }
                else { & sc.exe stop $ServiceId | Out-Null; & sc.exe delete $ServiceId | Out-Null }
            }
            Write-Output-Lines @("ok")
        }
        "firewall-add" {
            & netsh advfirewall firewall delete rule name="$RuleName" | Out-Null
            $port = if ($v.PORT) { $v.PORT } else { "8790" }
            # Home networks only (the Private profile): the server answers local addresses anyway.
            & netsh advfirewall firewall add rule name="$RuleName" dir=in action=allow protocol=TCP localport=$port profile=private | Out-Null
            & netsh advfirewall firewall add rule name="$RuleName" dir=in action=allow protocol=UDP localport=5353 profile=private | Out-Null
            Write-Output-Lines @("ok")
        }
        "firewall-remove" {
            & netsh advfirewall firewall delete rule name="$RuleName" | Out-Null
            Write-Output-Lines @("ok")
        }
        "health" {
            $port = if ($v.PORT) { $v.PORT } else { "8790" }
            $last = "The menu server didn't answer."
            # Up to about 30 s: the service starts, then the server reads the libraries and scans them.
            for ($i = 0; $i -lt 15; $i++) {
                try {
                    $h = Invoke-RestMethod -Uri "http://127.0.0.1:$port/v1/health" -TimeoutSec 2
                    if ($h.ok -and -not $h.scanning) { Write-Output-Lines @("ok", [string]$h.manifests, [string]$h.auth); exit 0 }
                    $last = "The menu server is still scanning."
                }
                catch { $last = "The menu server didn't answer: $($_.Exception.Message)" }
                Start-Sleep -Seconds 1
            }
            throw $last
        }
        default { throw "Unknown command: $Command" }
    }
    exit 0
}
catch {
    Write-Output-Lines @($_.Exception.Message)
    exit 1
}
