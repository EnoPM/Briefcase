[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('major', 'minor', 'build')]
    [string]$Increment,

    [string]$CurrentVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($CurrentVersion)) {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $CurrentVersion = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim()
}
else {
    $CurrentVersion = $CurrentVersion.Trim()
}

$match = [regex]::Match($CurrentVersion, '^(\d+)\.(\d+)\.(\d+)$')
if (-not $match.Success) {
    throw "Version must contain exactly three numeric components: $CurrentVersion"
}

$major = [uint32]::Parse($match.Groups[1].Value)
$minor = [uint32]::Parse($match.Groups[2].Value)
$build = [uint32]::Parse($match.Groups[3].Value)
$maximumComponent = [uint32][uint16]::MaxValue
if ($major -gt $maximumComponent -or
    $minor -gt $maximumComponent -or
    $build -gt $maximumComponent) {
    throw "Version components cannot exceed $maximumComponent because the native ABI uses uint16 values."
}

switch ($Increment) {
    'major' {
        if ($major -eq $maximumComponent) { throw 'The major version cannot be incremented further.' }
        $major++
        $minor = 0
        $build = 0
    }
    'minor' {
        if ($minor -eq $maximumComponent) { throw 'The minor version cannot be incremented further.' }
        $minor++
        $build = 0
    }
    'build' {
        if ($build -eq $maximumComponent) { throw 'The build version cannot be incremented further.' }
        $build++
    }
}

"$major.$minor.$build"
