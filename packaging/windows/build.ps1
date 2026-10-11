# Builds the Windows installer for the menu server, packaging/build/out/OpenVideoMenu-MenuServer-Setup.exe. The name
# has no version, so "latest" download links stay the same; the installer shows its version.
#
#   pwsh packaging/windows/build.ps1 [-Version 0.1.0] [-Iscc "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"]
#
# Needs: Node.js and the repository's npm packages (npm ci), Inno Setup 6 or 7. Downloads Node.js for Windows and
# WinSW once into packaging/build/cache, checking them: Node.js against nodejs.org's SHASUMS256.txt, WinSW against
# the hash pinned below.
#
# Signing (optional): set OVM_SIGN_CMD to a signing command line with $f for the file, e.g. for signtool:
#   $env:OVM_SIGN_CMD = 'signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /a $f'
# Inno Setup then signs the setup and its uninstaller. Unset, the installer is unsigned (Windows SmartScreen warns).
param(
    [string]$Version,
    [string]$Iscc,
    [string]$NodeVersion = "24.21.0"
)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$build = Join-Path $root "packaging\build"
$cache = Join-Path $build "cache"
$stage = Join-Path $build "windows-stage"
$out = Join-Path $build "out"
New-Item -ItemType Directory -Force -Path $cache, $out | Out-Null

if (-not $Version) { $Version = (Get-Content (Join-Path $root "packages\asset-server\package.json") -Raw | ConvertFrom-Json).version }

# WinSW 2.12.0, the build for .NET Framework 4.6.1 (part of Windows 10 and 11), and its license.
$winsw = @{
    Url  = "https://github.com/winsw/winsw/releases/download/v2.12.0/WinSW.NET461.exe"
    File = "WinSW.NET461-2.12.0.exe"
    Hash = "B5066B7BBDFBA1293E5D15CDA3CAAEA88FBEAB35BD5B38C41C913D492AADFC4F"
}
$winswLicense = @{
    Url  = "https://raw.githubusercontent.com/winsw/winsw/v2.12.0/LICENSE.txt"
    File = "WinSW-LICENSE-2.12.0.txt"
    Hash = "1CDF703C10A70E5973BF3ACF2A5EEABE7746237155B92DB2034AEAE26FDF7802"
}

function Get-Cached($url, $file) {
    $path = Join-Path $cache $file
    if (-not (Test-Path $path)) {
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile "$path.part" -UseBasicParsing
        Move-Item "$path.part" $path -Force
    }
    return $path
}

function Assert-Hash($path, $expected) {
    $actual = (Get-FileHash -Algorithm SHA256 $path).Hash
    if ($actual -ne $expected.ToUpper()) {
        Remove-Item $path -Force
        throw "Checksum mismatch for $(Split-Path $path -Leaf): expected $expected, got $actual. The download was removed."
    }
}

# 1. The server, bundled into one file.
Push-Location $root
try { node packaging/bundle.mjs; if ($LASTEXITCODE -ne 0) { throw "Bundling the menu server failed." } }
finally { Pop-Location }

# 2. Node.js for Windows, checked against nodejs.org's published checksums.
$nodeZipName = "node-v$NodeVersion-win-x64.zip"
$sums = Get-Cached "https://nodejs.org/dist/v$NodeVersion/SHASUMS256.txt" "node-v$NodeVersion-SHASUMS256.txt"
$nodeLine = Get-Content $sums | Where-Object { $_ -match "\s$([regex]::Escape($nodeZipName))$" }
if (-not $nodeLine) { throw "$nodeZipName isn't listed in SHASUMS256.txt." }
$nodeZip = Get-Cached "https://nodejs.org/dist/v$NodeVersion/$nodeZipName" $nodeZipName
Assert-Hash $nodeZip ($nodeLine -split "\s+")[0]

# 3. WinSW.
$winswExe = Get-Cached $winsw.Url $winsw.File
Assert-Hash $winswExe $winsw.Hash
$winswLic = Get-Cached $winswLicense.Url $winswLicense.File
Assert-Hash $winswLic $winswLicense.Hash

# 4. The files the installer installs.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($nodeZip)
try {
    foreach ($name in @("node.exe", "LICENSE")) {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq "node-v$NodeVersion-win-x64/$name" }
        if (-not $entry) { throw "$name not found in $nodeZipName." }
        $target = if ($name -eq "LICENSE") { Join-Path $stage "node-LICENSE.txt" } else { Join-Path $stage $name }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
}
finally { $zip.Dispose() }
Copy-Item (Join-Path $build "menu-server.mjs") $stage
Copy-Item $winswExe (Join-Path $stage "OpenVideoMenuServer.exe")
Copy-Item (Join-Path $PSScriptRoot "OpenVideoMenuServer.template.xml") $stage
Copy-Item (Join-Path $PSScriptRoot "setup-helper.mjs") $stage
Copy-Item (Join-Path $root "LICENSE") (Join-Path $stage "LICENSE.txt")
Copy-Item (Join-Path $root "NOTICE") (Join-Path $stage "NOTICE.txt")
$notices = @(
    "Open Video Menu menu server: third-party software included in this installer",
    "",
    "Node.js $NodeVersion (https://nodejs.org/), the runtime the menu server runs on:",
    "",
    (Get-Content (Join-Path $stage "node-LICENSE.txt") -Raw),
    "",
    "----------------------------------------------------------------------------------------------------",
    "",
    "WinSW 2.12.0 (https://github.com/winsw/winsw), which runs it as a Windows service (OpenVideoMenuServer.exe):",
    "",
    (Get-Content $winswLic -Raw),
    "",
    "----------------------------------------------------------------------------------------------------",
    "",
    "The menu server's own dependencies are bundled in menu-server.mjs with their license comments."
)
Set-Content -Path (Join-Path $stage "THIRD-PARTY-NOTICES.txt") -Value $notices -Encoding UTF8
Remove-Item (Join-Path $stage "node-LICENSE.txt")
Copy-Item (Join-Path $PSScriptRoot "install-readme.txt") (Join-Path $stage "README.txt")

# 5. The installer.
if (-not $Iscc) {
    $Iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { throw "Inno Setup's ISCC.exe wasn't found; pass -Iscc." }
}
$isccArgs = @("/Q", "/DAppVersion=$Version", "/DStage=$stage")
if ($env:OVM_SIGN_CMD) { $isccArgs += @("/DUseSign", "/Sovm=$env:OVM_SIGN_CMD") }
$isccArgs += (Join-Path $PSScriptRoot "menu-server.iss")
& $Iscc @isccArgs
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)." }

$setup = Join-Path $out "OpenVideoMenu-MenuServer-Setup.exe"
$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "$setup $Version ($size MB)"
