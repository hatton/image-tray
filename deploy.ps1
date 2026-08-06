# Build, install, restart. One command per iteration while we are still shaping the UI.
# Deliberately does not run tests.
$ErrorActionPreference = 'Stop'

$dest = "$env:LOCALAPPDATA\Programs\ImageTray"

Stop-Process -Name ImageTray -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

dotnet publish src/ImageTray/ImageTray.csproj -c Release -o publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item "publish\ImageTray.exe" $dest -Force

Start-Process -FilePath "$dest\ImageTray.exe"
Start-Sleep -Milliseconds 2500

$proc = Get-Process ImageTray -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc) { "running: $($proc.Path)" } else { "WARNING: it did not stay running" }
