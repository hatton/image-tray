# Publishes Release and compiles installer/ImageTray.iss into dist/.
# Unlike deploy.ps1 this does not install or restart anything locally.
$ErrorActionPreference = 'Stop'

$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    throw "Inno Setup 6 not found at '$iscc'. Install it with: winget install JRSoftware.InnoSetup"
}

# A stale exe from an earlier name or version must not get picked up.
Remove-Item publish -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish src/ImageTray/ImageTray.csproj -c Release -o publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

& $iscc /Q installer\ImageTray.iss
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }

Get-ChildItem dist\*.exe | Select-Object Name, @{n = 'KB'; e = { [int]($_.Length / 1KB) } }, LastWriteTime
