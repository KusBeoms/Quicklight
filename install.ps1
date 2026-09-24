# Installs dist\Quicklight.exe for the current user and starts it.
# Quicklight registers itself in HKCU\...\Run on start (tray menu: "Windows 시작 시 실행" to turn it off).
param([string]$Target = "$env:LOCALAPPDATA\Programs\Quicklight")

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'dist\Quicklight.exe'
if (-not (Test-Path $exe)) { throw "Run publish.bat first ($exe not found)." }

Get-Process Quicklight -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
New-Item -ItemType Directory -Force $Target | Out-Null
# Files from the older multi-file layout are no longer used.
Get-ChildItem $Target -Exclude 'Quicklight.exe' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
Copy-Item $exe $Target -Force

# The installed exe does not sit under this checkout, so point it at the LibreTranslate server set up here.
$lt = Join-Path $PSScriptRoot '.library\LibreTranslate'
$settingsPath = Join-Path $env:APPDATA 'Quicklight\settings.json'
if ((Test-Path (Join-Path $lt '.venv\Scripts\libretranslate.exe')) -and (Test-Path $settingsPath)) {
    $settings = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $settings.libreTranslateDir) {
        $settings | Add-Member -NotePropertyName libreTranslateDir -NotePropertyValue $lt -Force
        $settings | ConvertTo-Json -Depth 5 | Set-Content $settingsPath -Encoding UTF8
        Write-Host "Translation server: $lt"
    }
}

Start-Process (Join-Path $Target 'Quicklight.exe')
Write-Host "Installed $Target\Quicklight.exe and started it. Press Alt+Space."
Write-Host "MCP server: `"$Target\Quicklight.exe`" --mcp"
