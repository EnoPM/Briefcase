[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release')

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

try {
    $root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $game=[IO.Path]::GetFullPath('D:\SteamLibrary\steamapps\common\DeceiveInc\DeceiveInc\Binaries\Win64')
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
    foreach($required in @($proxy,$sourceCore,$sourceRuntime,$sourceMods,$sourceLoaderConfiguration)) {
        if(-not (Test-Path -LiteralPath $required)) {
            throw "Missing build output: $required. Run scripts\build\build_framework.bat $Configuration first."
        }
    }

    Copy-Item -LiteralPath $proxy -Destination (Join-Path $game 'version.dll') -Force

    $framework=Join-Path $game 'Briefcase'
    $core=Join-Path $framework 'Core'
    $mods=Join-Path $framework 'Mods'
    New-Item -ItemType Directory -Path $framework,$core,$mods -Force | Out-Null
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
