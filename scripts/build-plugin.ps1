param(
    [string]$HdtPath = ""
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\plugin\HearthstoneMcpPlugin.csproj"

if (-not $HdtPath) {
    $candidates = @(
        "$env:LOCALAPPDATA\HearthstoneDeckTracker",
        "$env:LOCALAPPDATA\Hearthstone Deck Tracker",
        "$env:APPDATA\HearthstoneDeckTracker",
        "$env:APPDATA\Hearthstone Deck Tracker"
    )
    $HdtPath = $candidates |
        Where-Object { Test-Path (Join-Path $_ "Hearthstone Deck Tracker.exe") } |
        Select-Object -First 1
}

if (-not $HdtPath) {
    throw "Nie znaleziono folderu z Hearthstone Deck Tracker.exe. Uruchom skrypt z -HdtPath 'C:\sciezka\do\programu'."
}

$required = @("Hearthstone Deck Tracker.exe", "HearthDb.dll", "Newtonsoft.Json.dll")
foreach ($filename in $required) {
    if (-not (Test-Path (Join-Path $HdtPath $filename))) {
        throw "Brak $filename w folderze: $HdtPath. Wskaz katalog z plikami aplikacji HDT, nie folder Plugins."
    }
}

Write-Host "Kompiluje wtyczke dla HDT w: $HdtPath"
dotnet build $project -c Release "-p:HdtPath=$HdtPath"
if ($LASTEXITCODE -ne 0) {
    throw "Kompilacja nie powiodla sie. Sprawdz .NET Framework 4.7.2 Targeting Pack i sciezke do HDT."
}

$dll = Join-Path $PSScriptRoot "..\plugin\bin\Release\net472\HearthstoneMcpPlugin.dll"
Write-Host ""
Write-Host "Gotowe: $dll"
Write-Host "W HDT otworz Options > Tracker > Plugins > Plugins Folder i skopiuj tam powyzszy plik DLL."
