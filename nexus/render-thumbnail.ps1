# Renders thumbnail.html to thumbnail.png (1920x1080) with Microsoft Edge in headless mode.
# Usage: powershell -File nexus\render-thumbnail.ps1
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe" }
$html = (Resolve-Path (Join-Path $PSScriptRoot "thumbnail.html")).Path
$png = Join-Path $PSScriptRoot "thumbnail.png"
# The virtual time budget lets the web font load before the shot.
& $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --window-size=1920,1080 `
    --virtual-time-budget=8000 --screenshot="$png" "file:///$($html -replace '\\', '/')" | Out-Null
Get-Item $png | Select-Object Name, Length
