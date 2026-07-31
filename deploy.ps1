# Build, install, restart. One command per iteration while we are still shaping the UI.
# Deliberately does not run tests.
$ErrorActionPreference = 'Stop'

$dest = "$env:LOCALAPPDATA\Programs\ScreenTray"

Stop-Process -Name ScreenTray -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

dotnet publish src/ScreenTray/ScreenTray.csproj -c Release -o publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item "publish\ScreenTray.exe" $dest -Force

Start-Process -FilePath "$dest\ScreenTray.exe"
Start-Sleep -Milliseconds 2500

$proc = Get-Process ScreenTray -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc) { "running: $($proc.Path)" } else { "WARNING: it did not stay running" }
