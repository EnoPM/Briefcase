[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputDirectory = 'artifacts\release',

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Remove-SafeDirectory([string]$Path, [string]$AllowedRoot) {
    if (-not (Test-Path -LiteralPath $Path)) { return }

    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe cleanup path: $resolved"
    }

    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Copy-DirectoryContents([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Package directory is missing: $Source"
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName `
            -Destination (Join-Path $Destination $item.Name) -Recurse -Force
    }
}

function Assert-File([string]$Root, [string]$RelativePath) {
    $path = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required release file is missing: $RelativePath"
    }
}

function Assert-EmptyModsDirectory([string]$PackageRoot) {
    $mods = Join-Path $PackageRoot 'Briefcase\Mods'
    if (-not (Test-Path -LiteralPath $mods -PathType Container)) {
        throw "Required empty mod directory is missing: $mods"
    }

    if (@(Get-ChildItem -LiteralPath $mods -Force).Count -ne 0) {
        throw "A framework release must not contain user mods: $mods"
    }
}

function Assert-BinaryServerSnapshot([string]$PackageRoot) {
    $metadataDirectory = Join-Path $PackageRoot 'Briefcase\Core\Sdk\Metadata'
    if (-not (Test-Path -LiteralPath $metadataDirectory -PathType Container)) {
        throw "Server SDK metadata directory is missing: $metadataDirectory"
    }

    $binarySnapshots = @(Get-ChildItem -LiteralPath $metadataDirectory -Filter '*.bsnap' -File)
    if ($binarySnapshots.Count -ne 1) {
        throw "Expected exactly one binary server SDK snapshot, found $($binarySnapshots.Count)."
    }

    $jsonSnapshots = @(Get-ChildItem -LiteralPath $metadataDirectory -Filter '*.json' -File)
    if ($jsonSnapshots.Count -ne 0) {
        throw "A release server package must not contain JSON SDK snapshots."
    }
}

function Assert-CoreLibraryLayout(
    [string]$PackageRoot,
    [bool]$RequireThirdPartyLibraries) {
    $framework = Join-Path $PackageRoot 'Briefcase'
    $core = [IO.Path]::GetFullPath((Join-Path $framework 'Core'))
    $thirdParty = [IO.Path]::GetFullPath((Join-Path $core 'ThirdPartyLibraries'))
    if ($RequireThirdPartyLibraries -and
        -not (Test-Path -LiteralPath $thirdParty -PathType Container)) {
        throw 'Client package is missing Core\ThirdPartyLibraries.'
    }

    $symbols = @(Get-ChildItem -LiteralPath $framework -Filter '*.pdb' -File -Recurse)
    if ($symbols.Count -ne 0) {
        throw "The package contains debug symbols: $($symbols.FullName -join ', ')"
    }

    $dotNetPrefix = [IO.Path]::GetFullPath((Join-Path $core 'DotNet')).TrimEnd('\') + '\'
    $thirdPartyPrefix = $thirdParty.TrimEnd('\') + '\'
    $misplaced = @(Get-ChildItem -LiteralPath $core -Filter '*.dll' -File -Recurse |
        Where-Object {
            $full = [IO.Path]::GetFullPath($_.FullName)
            -not $full.StartsWith($dotNetPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            -not $full.StartsWith($thirdPartyPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            $_.Name -notmatch '^(Briefcase\.|ServerAdminControl\.)'
        })
    if ($misplaced.Count -ne 0) {
        throw "Third-party libraries are outside Core\ThirdPartyLibraries: $($misplaced.FullName -join ', ')"
    }
}

function New-ReleaseArchive(
    [string]$Source,
    [string]$Destination,
    [string[]]$RequiredEntries) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $Source,
        $Destination,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)

    $archive = [IO.Compression.ZipFile]::OpenRead($Destination)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in $RequiredEntries) {
            if ($entries -notcontains $required) {
                throw "Archive entry is missing from $Destination`: $required"
            }
        }

        if (@($entries | Where-Object { $_ -match '(^|/)Briefcase-(Client|Server)-v[^/]+/' }).Count -ne 0) {
            throw "The archive contains an unwanted wrapper directory: $Destination"
        }

        if (@($entries | Where-Object { $_ -like '*.pdb' }).Count -ne 0) {
            throw "The archive contains debug symbols: $Destination"
        }
    }
    finally {
        $archive.Dispose()
    }
}

try {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $version = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "VERSION is not a valid release version: $version"
    }

    if (-not $SkipBuild) {
        foreach ($buildScript in @(
            'scripts\build\BuildFramework.ps1',
            'scripts\build\BuildServerFramework.ps1')) {
            $scriptPath = Join-Path $root $buildScript
            & powershell.exe -NoProfile -ExecutionPolicy Bypass `
                -File $scriptPath -Configuration $Configuration
            if ($LASTEXITCODE -ne 0) {
                throw "Build failed with exit code $LASTEXITCODE`: $buildScript"
            }
        }
    }

    $artifactRoot = Join-Path $root 'artifacts'
    $outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
        [IO.Path]::GetFullPath($OutputDirectory)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
    }
    $artifactPrefix = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\') + '\'
    if (-not $outputRoot.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release output must stay below the artifacts directory: $outputRoot"
    }

    $stagingRoot = Join-Path $artifactRoot 'release-staging'
    Remove-SafeDirectory $outputRoot $artifactRoot
    Remove-SafeDirectory $stagingRoot $artifactRoot
    New-Item -ItemType Directory -Path $outputRoot, $stagingRoot -Force | Out-Null

    $clientStage = Join-Path $stagingRoot 'Client'
    $serverStage = Join-Path $stagingRoot 'Server'
    Copy-DirectoryContents (Join-Path $root 'dist\Briefcase') $clientStage
    Copy-DirectoryContents (Join-Path $root 'dist\Briefcase.Server') $serverStage
    foreach ($stage in @($clientStage, $serverStage)) {
        Copy-Item -LiteralPath (Join-Path $root 'LICENSE') `
            -Destination (Join-Path $stage 'LICENSE') -Force
    }

    # The build package contains launchers for both deployment locations. A
    # release is extracted directly in Win64, so it exposes only that launcher
    # under the short, obvious name below.
    foreach ($launcher in @('StartBriefcaseServer.bat', 'StartBriefcaseServerNoUI.bat')) {
        $packagedLauncher = Join-Path $serverStage $launcher
        if (Test-Path -LiteralPath $packagedLauncher) {
            Remove-Item -LiteralPath $packagedLauncher -Force
        }
    }
    Copy-Item -LiteralPath (Join-Path $root 'scripts\server\StartBriefcaseServerNoUI.bat') `
        -Destination (Join-Path $serverStage 'StartBriefcaseServer.bat') -Force


    $clientReadme = @"
Briefcase Client $version

Installation
1. Close Deceive Inc.
2. Extract this archive directly into DeceiveInc\Binaries\Win64.
3. Start the game normally.

The archive contains the version.dll proxy and the complete Briefcase runtime,
including its private .NET runtime. Open the configuration menu with F1.
Place user mod DLLs in Briefcase\Mods.
"@
    [IO.File]::WriteAllText(
        (Join-Path $clientStage 'README-Briefcase.txt'),
        $clientReadme,
        [Text.UTF8Encoding]::new($false))

    $serverReadme = @"
Briefcase Server $version

Installation
1. Stop the Deceive Inc. dedicated server.
2. Extract this archive directly into
   DeceiveInc\Binaries\Win64 in the dedicated-server installation.
3. Run StartBriefcaseServer.bat from that Win64 directory.

The launcher starts DeceiveIncServer-Win64-Shipping.exe in the current terminal
without the graphical configuration launcher. Place server mod DLLs in
Briefcase\Mods.
"@
    [IO.File]::WriteAllText(
        (Join-Path $serverStage 'README-Briefcase.txt'),
        $serverReadme,
        [Text.UTF8Encoding]::new($false))

    foreach ($stage in @($clientStage, $serverStage)) {
        Assert-File $stage 'LICENSE'
        Assert-File $stage 'version.dll'
        Assert-File $stage 'Briefcase\loader.json'
        Assert-File $stage 'Briefcase\VERSION'
        Assert-File $stage 'Briefcase\Core\Briefcase.ManagedHost.dll'
        Assert-File $stage 'Briefcase\Core\Briefcase.Updater.dll'
        Assert-File $stage 'Briefcase\Core\Updater\Briefcase.UpdateInstaller.exe'
        Assert-File $stage 'Briefcase\Core\Briefcase.ModApi.dll'
        Assert-File $stage 'Briefcase\Core\Briefcase.SdkSnapshots.dll'
        Assert-File $stage 'Briefcase\Core\Native\Briefcase.UnrealRuntime.dll'
        Assert-EmptyModsDirectory $stage
        $packagedVersion = [IO.File]::ReadAllText((Join-Path $stage 'Briefcase\VERSION')).Trim()
        if ($packagedVersion -ne $version) {
            throw "Packaged VERSION '$packagedVersion' does not match '$version'."
        }
    }
    Assert-CoreLibraryLayout $clientStage $true
    Assert-CoreLibraryLayout $serverStage $false
    Assert-File $clientStage 'Briefcase\Core\Briefcase.ClientModApi.dll'
    Assert-File $clientStage 'Briefcase\Core\Native\Briefcase.Native.Rendering.dll'
    Assert-File $clientStage 'Briefcase\Core\Ui\Avalonia\Briefcase.AvaloniaUi.dll'
    Assert-File $clientStage 'Briefcase\Core\Ui\Avalonia\Briefcase.AvaloniaMenu.dll'
    Assert-File $clientStage 'Briefcase\Core\ThirdPartyLibraries\Avalonia.Base.dll'
    Assert-File $clientStage 'Briefcase\Core\ThirdPartyLibraries\libSkiaSharp.dll'
    Assert-File $clientStage 'Briefcase\Core\ThirdPartyLibraries\Inter.OFL.txt'
    Assert-File $serverStage 'StartBriefcaseServer.bat'
    Assert-BinaryServerSnapshot $serverStage

    $forbiddenServerFiles = @(Get-ChildItem -LiteralPath $serverStage -File -Recurse |
        Where-Object { $_.Name -match '^(ImGui|cimgui|Briefcase\.(Rendering|ClientModApi|AvaloniaUi|AvaloniaMenu)|Avalonia\.|SkiaSharp|Vortice\.|SharpGen\.)' })
    if ($forbiddenServerFiles.Count -ne 0) {
        throw "The server archive contains rendering files: $($forbiddenServerFiles.Name -join ', ')"
    }

    $clientArchive = Join-Path $outputRoot "Briefcase-Client-v$version.zip"
    $serverArchive = Join-Path $outputRoot "Briefcase-Server-v$version.zip"
    New-ReleaseArchive $clientStage $clientArchive @(
        'version.dll',
        'Briefcase/loader.json',
        'Briefcase/VERSION',
        'Briefcase/Core/Briefcase.ManagedHost.dll',
        'Briefcase/Core/Briefcase.Updater.dll',
        'Briefcase/Core/Updater/Briefcase.UpdateInstaller.exe',
        'Briefcase/Core/Briefcase.ClientModApi.dll',
        'Briefcase/Core/Native/Briefcase.Native.Rendering.dll',
        'Briefcase/Core/Ui/Avalonia/Briefcase.AvaloniaUi.dll',
        'Briefcase/Core/Ui/Avalonia/Briefcase.AvaloniaMenu.dll',
        'Briefcase/Core/ThirdPartyLibraries/Avalonia.Base.dll',
        'Briefcase/Core/ThirdPartyLibraries/libSkiaSharp.dll',
        'Briefcase/Core/ThirdPartyLibraries/Inter.OFL.txt',
        'Briefcase/Core/Briefcase.SdkSnapshots.dll',
        'Briefcase/Core/Native/Briefcase.UnrealRuntime.dll',
        'LICENSE',
        'README-Briefcase.txt')
    New-ReleaseArchive $serverStage $serverArchive @(
        'version.dll',
        'Briefcase/loader.json',
        'Briefcase/VERSION',
        'Briefcase/Core/Briefcase.ManagedHost.dll',
        'Briefcase/Core/Briefcase.Updater.dll',
        'Briefcase/Core/Updater/Briefcase.UpdateInstaller.exe',
        'Briefcase/Core/Briefcase.SdkSnapshots.dll',
        'Briefcase/Core/Native/Briefcase.UnrealRuntime.dll',
        'StartBriefcaseServer.bat',
        'LICENSE',
        'README-Briefcase.txt')

    $archives = @(Get-ChildItem -LiteralPath $outputRoot -Filter '*.zip' -File)
    if ($archives.Count -ne 2) {
        throw "Expected exactly two release archives, found $($archives.Count)."
    }

    Remove-SafeDirectory $stagingRoot $artifactRoot
    foreach ($archive in $archives | Sort-Object Name) {
        Write-Host "[OK] $($archive.Name) ($([Math]::Round($archive.Length / 1MB, 2)) MiB)"
    }
    exit 0
}
catch {
    Write-Host "[ERROR] $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    exit 1
}
