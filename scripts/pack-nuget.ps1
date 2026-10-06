# Packs the .NET packages (OpenVideoMenu, OpenVideoMenu.Dvd, and the ovm-dvd tool) into a local NuGet folder
# outside every repo, for apps that use them before they're on nuget.org (DVD Tamer's "local" package source).
# Run after changing either package, then bump the version in the app's project if it changed.
#
#   pwsh scripts/pack-nuget.ps1 [-Out L:\nuget-local]
param(
    [string]$Out = "L:\nuget-local"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$projects = @(
    "sdk\dotnet\OpenVideoMenu\OpenVideoMenu.csproj",
    "sdk\dotnet-dvd\OpenVideoMenu.Dvd\OpenVideoMenu.Dvd.csproj",
    "sdk\dotnet-dvd\ovm-dvd\ovm-dvd.csproj"
)
New-Item -ItemType Directory -Force $Out | Out-Null
foreach ($p in $projects) {
    $path = Join-Path $root $p
    dotnet pack $path -c Release -o $Out
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed: $p" }
    # NuGet caches a package by version: while a package is being worked on its version stays put, so drop the
    # cached copy and the next restore takes the one just packed.
    $id = (dotnet msbuild $path -getProperty:PackageId).Trim()
    $version = (dotnet msbuild $path -getProperty:Version).Trim()
    $cached = Join-Path $env:USERPROFILE ".nuget\packages\$($id.ToLowerInvariant())\$version"
    if (Test-Path $cached) { Remove-Item -Recurse -Force $cached }
    Write-Host "$id $version -> $Out"
}
