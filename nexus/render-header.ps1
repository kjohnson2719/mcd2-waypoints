# Renders header.html to header.png (1300x372) with Microsoft Edge in headless mode.
# Usage: powershell -File nexus\render-header.ps1
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe" }
$html = (Resolve-Path (Join-Path $PSScriptRoot "header.html")).Path
$png = Join-Path $PSScriptRoot "header.png"
# The virtual time budget lets the web font load before the shot.
& $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --window-size=1300,372 `
    --virtual-time-budget=8000 --screenshot="$png" "file:///$($html -replace '\\', '/')" | Out-Null
Get-Item $png | Select-Object Name, Length
