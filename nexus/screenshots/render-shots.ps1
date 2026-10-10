# Renders the Nexus screenshots (1920x1080) from shot.html with Microsoft Edge in headless mode:
# a clean and an annotated version of each.
# Usage: powershell -ExecutionPolicy Bypass -File nexus\screenshots\render-shots.ps1
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe" }
$page = "file:///" + ((Resolve-Path (Join-Path $PSScriptRoot "shot.html")).Path -replace '\\', '/')
foreach ($shot in @(
    @{ Name = "01-map";           Query = "shot=map" },
    @{ Name = "01-map-notes";     Query = "shot=map&notes=1" },
    @{ Name = "02-world";         Query = "shot=world" },
    @{ Name = "02-world-notes";   Query = "shot=world&notes=1" })) {
    $png = Join-Path $PSScriptRoot "$($shot.Name).png"
    & $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --window-size=1920,1080 `
        --allow-file-access-from-files --virtual-time-budget=8000 --screenshot="$png" "$page`?$($shot.Query)" 2>$null | Out-Null
    Get-Item $png | Select-Object Name, Length
}
