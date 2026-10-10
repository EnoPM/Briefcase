param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$OutputDirectory,
    [string]$NativeBuildDirectory,
    [switch]$EnableUpdates
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if ($destination -ne [IO.Path]::GetPathRoot($destination)) {
    $destination = $destination.TrimEnd([char]92, [char]47)
}
$stage = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist/LocalDeploymentStage'))
$separator = [IO.Path]::DirectorySeparatorChar
function Test-Inside([string]$path, [string]$root) {
    return $path.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
        $path.StartsWith($root + $separator, [StringComparison]::OrdinalIgnoreCase)
}
if ($destination -eq [IO.Path]::GetPathRoot($destination) -or
    (Test-Inside $projectRoot $destination) -or
    (Test-Inside $destination $stage) -or
    (Test-Inside $stage $destination)) {
    throw 'Choose a dedicated output folder outside the project and staging directories.'
}

$markerPath = Join-Path $destination '.briefcase-local-deployment.json'
if (Test-Path -LiteralPath $destination) {
    $folder = Get-Item -LiteralPath $destination -Force
    if (-not $folder.PSIsContainer -or ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The output path must be an ordinary directory: $destination"
    }
    if ((Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1) -and
        -not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw 'The output folder is not empty and was not created by this local deployment script.'
    }
    if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        if ($marker.schemaVersion -ne 1 -or $marker.kind -ne 'Briefcase.ServerApp.LocalAot') {
            throw 'The output folder has an unrecognized local deployment marker.'
        }
    }
}

foreach ($process in @(Get-Process -Name 'Briefcase.ServerApp', 'Briefcase.Launcher' -ErrorAction SilentlyContinue)) {
    try { $processPath = $process.Path } catch { $processPath = $null }
    if (-not $processPath -or (Test-Inside ([IO.Path]::GetFullPath($processPath)) $destination)) {
        throw 'Close Briefcase and its launcher in the output folder before deploying again.'
    }
}

$baseVersion = (Get-Content -LiteralPath (Join-Path $projectRoot 'VERSION') -Raw).Trim()
if ($baseVersion -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    throw 'VERSION must contain a three-part numeric version.'
}
$major = $Matches[1]
$minor = $Matches[2]
$patch = [long]::Parse($Matches[3])
$version = "$major.$minor.$patch"
while (Test-Path -LiteralPath (Join-Path $destination "App/versions/$version")) {
    $patch++
    $version = "$major.$minor.$patch"
}

$publishParameters = @{
    OutputDirectory = $stage
    VersionOverride = $version
    SkipArchive = $true
    EnableUpdates = $EnableUpdates
}
if (-not [string]::IsNullOrWhiteSpace($NativeBuildDirectory)) {
    $publishParameters.NativeBuildDirectory = $NativeBuildDirectory
}
& (Join-Path $PSScriptRoot 'Publish-ServerApp.ps1') @publishParameters

New-Item -ItemType Directory -Path $destination -Force | Out-Null
$marker = [ordered]@{ schemaVersion = 1; kind = 'Briefcase.ServerApp.LocalAot' }
[IO.File]::WriteAllText($markerPath, ($marker | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$targetApp = Join-Path $destination "App/versions/$version"
New-Item -ItemType Directory -Path $targetApp -Force | Out-Null
$sourceApp = Join-Path $stage "App/versions/$version"
foreach ($file in Get-ChildItem -LiteralPath $sourceApp -Recurse -File) {
    $relative = $file.FullName.Substring($sourceApp.Length + 1)
    $target = Join-Path $targetApp $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
}

$rootFiles = @('Briefcase.Launcher.exe', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll',
    'av_libglesv2.dll', 'Launcher/manifest.json', 'UpdateSource.json',
    'Translations/en.json', 'Translations/fr.json')
foreach ($relative in $rootFiles) {
    $source = Join-Path $stage $relative
    $target = Join-Path $destination $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force
}

# Select the new complete version only after every binary and resource was copied.
Copy-Item -LiteralPath (Join-Path $stage 'App/current.json') -Destination (Join-Path $destination 'App/current.json') -Force
Write-Output "Local AOT deployment ready: $destination"
Write-Output "Run: $([IO.Path]::Combine($destination, 'Briefcase.Launcher.exe'))"
