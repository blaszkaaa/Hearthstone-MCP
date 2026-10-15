<#
.SYNOPSIS
Instaluje lokalna wtyczke Hearthstone MCP do Hearthstone Deck Tracker.
.DESCRIPTION
Nie pobiera plikow ani nie modyfikuje gry. Jesli -Build, kompiluje plugin wzgledem
zainstalowanego HDT. Domyslnie kopiuje do %APPDATA%\HearthstoneDeckTracker\Plugins.
#>
[CmdletBinding()]
param(
    [string]$DllPath = "",
    [string]$PluginsPath = "",
    [switch]$Build,
    [string]$HdtPath = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$defaultDll = Join-Path $repo "plugin\bin\Release\net472\HearthstoneMcpPlugin.dll"

if ($Build) {
    & (Join-Path $PSScriptRoot "build-plugin.ps1") -HdtPath $HdtPath
    if ($LASTEXITCODE -ne 0) { throw "Kompilacja pluginu nie powiodla sie." }
}
if ([string]::IsNullOrWhiteSpace($DllPath)) { $DllPath = $defaultDll }
if (-not (Test-Path -LiteralPath $DllPath -PathType Leaf)) {
    throw "Brak skompilowanej DLL: $DllPath. Uruchom -Build lub pobierz artifact z GitHub Actions i wskaz -DllPath."
}
if ([string]::IsNullOrWhiteSpace($PluginsPath)) {
    if ([string]::IsNullOrWhiteSpace($env:APPDATA)) { throw "Brak zmiennej APPDATA. Podaj -PluginsPath." }
    $folders = @(
        (Join-Path $env:APPDATA "HearthstoneDeckTracker"),
        (Join-Path $env:APPDATA "Hearthstone Deck Tracker")
    )
    $hdtFolder = $folders | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
    if (-not $hdtFolder) {
        throw "Nie znaleziono danych HDT w APPDATA. Uruchom najpierw Hearthstone Deck Tracker, potem podaj -PluginsPath, jesli korzysta z innej lokalizacji."
    }
    $PluginsPath = Join-Path $hdtFolder "Plugins"
}

$hdtRunning = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq "Hearthstone Deck Tracker" -or $_.ProcessName -eq "HearthstoneDeckTracker" }).Count -gt 0
if ($hdtRunning) {
    throw "Hearthstone Deck Tracker jest uruchomiony. Zamknij HDT przed instalacja i uruchom skrypt ponownie."
}

New-Item -ItemType Directory -Path $PluginsPath -Force | Out-Null
$destination = Join-Path $PluginsPath "HearthstoneMcpPlugin.dll"
$sourcePath = (Resolve-Path -LiteralPath $DllPath).Path
if ($sourcePath -ne $destination) {
    Copy-Item -LiteralPath $sourcePath -Destination $destination -Force
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash -ne (Get-FileHash -Algorithm SHA256 -LiteralPath $destination).Hash) {
    throw "Bledna suma kontrolna DLL po skopiowaniu."
}
Write-Host "Plugin zainstalowany: $destination"
Write-Host "Uruchom HDT, wlacz plugin w Options > Tracker > Plugins i rozpocznij mecz."
Write-Host "Nastepnie uruchom: python -m mcp_server.doctor (w katalogu repozytorium)."
