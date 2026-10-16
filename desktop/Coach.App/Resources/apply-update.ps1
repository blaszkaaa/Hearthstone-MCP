# Embedded into the application; executed by a separate PowerShell process AFTER the app exits.
# No game files, user profile data, HDT plugin or ChatGPT tokens are modified.
param(
    [Parameter(Mandatory = $true)][int]$AppPid,
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [Parameter(Mandatory = $true)][string]$StageDir,
    [Parameter(Mandatory = $true)][string]$UpdateDir
)
$ErrorActionPreference = 'Stop'
$backupDir = Join-Path $UpdateDir 'previous'
$copied = New-Object 'System.Collections.Generic.List[string]'
$backedUp = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$exe = Join-Path $InstallDir 'HearthstoneCoach.exe'

try {
    # Never overwrite a running executable, and never force-kill the app.
    for ($i = 0; $i -lt 60; $i++) {
        if (-not (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 500
    }
    if (Get-Process -Id $AppPid -ErrorAction SilentlyContinue) {
        throw 'Aplikacja nie zamknęła się w ciągu 30 sekund.'
    }
    $items = @(Get-ChildItem -LiteralPath $StageDir -File)
    if (-not ($items | Where-Object { $_.Name -eq 'HearthstoneCoach.exe' })) {
        throw 'Brakuje pliku HearthstoneCoach.exe w przygotowanej aktualizacji.'
    }
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    # Back up ALL files before touching the original installation.
    foreach ($item in $items) {
        $destination = Join-Path $InstallDir $item.Name
        if (Test-Path -LiteralPath $destination) {
            Copy-Item -LiteralPath $destination -Destination (Join-Path $backupDir $item.Name) -Force
            [void]$backedUp.Add($item.Name)
        }
    }
    foreach ($item in $items) {
        $destination = Join-Path $InstallDir $item.Name
        $copied.Add($item.Name)
        Copy-Item -LiteralPath $item.FullName -Destination $destination -Force
        $actualHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        $expectedHash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) {
            throw "Nieudana weryfikacja pliku: $($item.Name)"
        }
    }
    Start-Process -FilePath $exe -WorkingDirectory $InstallDir | Out-Null
    # Best effort temp cleanup after the new app starts.
    Remove-Item -LiteralPath $UpdateDir -Recurse -Force -ErrorAction SilentlyContinue
}
catch {
    $failure = $_.ToString()
    # Restore previously installed app and remove any files introduced by this update.
    foreach ($name in $copied) {
        $destination = Join-Path $InstallDir $name
        try {
            if ($backedUp.Contains($name)) {
                Copy-Item -LiteralPath (Join-Path $backupDir $name) -Destination $destination -Force
            }
            elseif (Test-Path -LiteralPath $destination) {
                Remove-Item -LiteralPath $destination -Force
            }
        }
        catch { $failure += "`nBłąd cofania pliku $name : $_" }
    }
    try {
        $logDir = Join-Path $env:LOCALAPPDATA 'HearthstoneMCP'
        New-Item -Path $logDir -ItemType Directory -Force | Out-Null
        Add-Content -LiteralPath (Join-Path $logDir 'desktop-update-error.log') -Value "$(Get-Date -Format o) $failure"
    } catch {}
    if (Test-Path -LiteralPath $exe) {
        try { Start-Process -FilePath $exe -WorkingDirectory $InstallDir | Out-Null } catch {}
    }
    try {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show(
            "Nie udało się zaktualizować Hearthstone AI Coach. Przywrócono poprzednie pliki, jeśli było to możliwe.`n`n$failure",
            'Aktualizacja Hearthstone AI Coach',
            [System.Windows.MessageBoxButton]::OK,
            [System.Windows.MessageBoxImage]::Warning
        ) | Out-Null
    } catch {}
}
