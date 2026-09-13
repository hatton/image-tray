# Raises <Version> in the app's project file and prints the new number.
#
# That one element is where every other version comes from: the build stamps it on
# the exe, and the installer reads it back off the exe with GetVersionNumbersString.
# Nothing else in the repo carries a version, so nothing else needs editing.
#
#   ./tools/set-version.ps1                    1.0.0 -> 1.1.0
#   ./tools/set-version.ps1 -Bump patch        1.0.0 -> 1.0.1
#   ./tools/set-version.ps1 -Version 2.0.0     exactly that
#   ./tools/set-version.ps1 -DryRun            says what it would do, writes nothing
[CmdletBinding()]
param(
    # Which part to raise. Ignored when -Version is given.
    [ValidateSet('major', 'minor', 'patch')]
    [string]$Bump = 'minor',

    # An exact version to set instead, such as 1.4.0.
    [string]$Version,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$project = (Resolve-Path (Join-Path $PSScriptRoot '../src/ImageTray/ImageTray.csproj')).Path
$text = [System.IO.File]::ReadAllText($project)

# PackageReference lines carry Version as an attribute, so matching the element
# keeps this to the one that means the app's own version. Anything other than a
# single hit means the file has changed shape and a blind replace would be a guess.
$found = [regex]::Matches($text, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
if ($found.Count -ne 1) {
    throw "Expected one <Version>x.y.z</Version> in $project, found $($found.Count)."
}

$hit = $found[0]
$major = [int]$hit.Groups[1].Value
$minor = [int]$hit.Groups[2].Value
$patch = [int]$hit.Groups[3].Value
$current = [version]"$major.$minor.$patch"

if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        throw "'$Version' is not a version of the form x.y.z."
    }

    $next = [version]$Version
}
else {
    $next = switch ($Bump) {
        'major' { [version]"$($major + 1).0.0" }
        'minor' { [version]"$major.$($minor + 1).0" }
        'patch' { [version]"$major.$minor.$($patch + 1)" }
    }
}

# Going backwards would give two different builds the same name, or a release that
# sorts under one already out there.
if ($next -le $current) {
    throw "$next does not come after the current version, $current."
}

if ($DryRun) {
    Write-Host "would raise $current to $next in $project"
}
else {
    $updated = $text.Remove($hit.Index, $hit.Length).Insert($hit.Index, "<Version>$next</Version>")

    # WriteAllText rather than Set-Content: the rest of the file comes back byte for
    # byte, including its line endings.
    [System.IO.File]::WriteAllText($project, $updated)
}

# The only thing on stdout, so a caller can read the new version straight out of it.
"$next"
