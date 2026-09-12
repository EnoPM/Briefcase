[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration='Release',
    [string]$GameWin64='')

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

try {
    $root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    if([string]::IsNullOrWhiteSpace($GameWin64)) {
        $GameWin64=$env:BRIEFCASE_CLIENT_GAME_DIR
    }
    if([string]::IsNullOrWhiteSpace($GameWin64)) {
        $GameWin64='D:\SteamLibrary\steamapps\common\DeceiveInc\DeceiveInc\Binaries\Win64'
    }
    $game=[IO.Path]::GetFullPath($GameWin64)
    if(Get-Process -Name 'DeceiveInc-Win64-Shipping' -ErrorAction SilentlyContinue) {
        throw 'Close Deceive Inc. before installing Briefcase.'
    }

    $package=Join-Path $root 'dist\Briefcase'
    $proxy=Join-Path $package 'version.dll'
    $sourceFramework=Join-Path $package 'Briefcase'
    $sourceCore=Join-Path $sourceFramework 'Core'
    $sourceRuntime=Join-Path $sourceCore 'Native\Briefcase.UnrealRuntime.dll'
    $sourceMods=Join-Path $sourceFramework 'Mods'
    $sourceLoaderConfiguration=Join-Path $sourceFramework 'loader.json'
    $sourceVersion=Join-Path $sourceFramework 'VERSION'
    foreach($required in @($proxy,$sourceCore,$sourceRuntime,$sourceMods,$sourceLoaderConfiguration,$sourceVersion)) {
        if(-not (Test-Path -LiteralPath $required)) {
            throw "Missing build output: $required. Run scripts\build\build_framework.bat $Configuration first."
        }
    }

    Copy-Item -LiteralPath $proxy -Destination (Join-Path $game 'version.dll') -Force

    $framework=Join-Path $game 'Briefcase'
    $core=Join-Path $framework 'Core'
    $mods=Join-Path $framework 'Mods'
    New-Item -ItemType Directory -Path $framework,$core,$mods -Force | Out-Null
    Copy-Item -LiteralPath $sourceVersion -Destination (Join-Path $framework 'VERSION') -Force

    # Core is framework-owned and must mirror the new package. Preserve only
    # runtime-generated SDK/cache data; copying over an old Core leaves removed
    # dependencies loadable and makes architectural migrations ineffective.
    $coreFull=[IO.Path]::GetFullPath($core).TrimEnd('\')
    Get-ChildItem -LiteralPath $core -Force |
        Where-Object { $_.Name -notin @('Cache','Sdk') } |
        ForEach-Object {
            $candidate=[IO.Path]::GetFullPath($_.FullName)
            if([IO.Path]::GetDirectoryName($candidate).TrimEnd('\') -ne $coreFull) {
                throw "Unsafe Core cleanup path: $candidate"
            }
            Remove-Item -LiteralPath $candidate -Recurse -Force
        }
    Copy-Item -Path (Join-Path $sourceCore '*') -Destination $core -Recurse -Force

    # Preserve third-party mods and their JSON settings. Framework-owned builds
    # are copied individually, so deployment never erases the Mods folder.
    Get-ChildItem -LiteralPath $sourceMods -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $mods $_.Name) -Force
    }
    foreach($legacyName in @(
        'CommunityBalancing.Client.dll',
        'CommunityBalancing.Client.pdb',
        'ServerAdminControl.Client.dll',
        'ServerAdminControl.Client.pdb',
        'Briefcase.DevMenu.dll',
        'Briefcase.DevMenu.pdb')) {
        $legacy=Join-Path $mods $legacyName
        if(Test-Path -LiteralPath $legacy) { Remove-Item -LiteralPath $legacy -Force }
    }
    $loaderConfiguration=Join-Path $framework 'loader.json'
    if(-not (Test-Path -LiteralPath $loaderConfiguration)) {
        Copy-Item -LiteralPath $sourceLoaderConfiguration -Destination $loaderConfiguration
    }

    Get-ChildItem -LiteralPath $framework -Filter '*.pdb' -File -Recurse |
        Remove-Item -Force

    Write-Host '[OK] Installed Briefcase proxy and native/managed Core.'
    Write-Host "[OK] Native runtime: $(Join-Path $core 'Native\Briefcase.UnrealRuntime.dll')"
    Write-Host "[OK] Managed mod directory: $mods"
    Write-Host "[OK] Loader configuration: $loaderConfiguration"
    Write-Host "[INFO] Runtime log: $(Join-Path $framework 'Briefcase.log')"
    exit 0
} catch {
    Write-Host "[ERROR] $($_.Exception.Message)"
    exit 1
}
