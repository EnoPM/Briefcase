[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release')

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

try {
    $root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $server=[IO.Path]::GetFullPath(
        'D:\GameServers\steamcmd\steamapps\common\Deceive Inc. Dedicated Server\DeceiveInc\Binaries\Win64')
    if(Get-Process -Name 'DeceiveIncServer-Win64-Shipping' -ErrorAction SilentlyContinue) {
        throw 'Stop the Deceive Inc. dedicated server before installing Briefcase Core.'
    }

    $package=Join-Path $root 'dist\Briefcase.Server'
    $proxy=Join-Path $package 'version.dll'
    $sourceFramework=Join-Path $package 'Briefcase'
    $sourceCore=Join-Path $sourceFramework 'Core'
    $sourceMods=Join-Path $sourceFramework 'Mods'
    $sourceLoaderConfiguration=Join-Path $sourceFramework 'loader.json'
    $sourceLauncher=Join-Path $package 'StartBriefcaseServer.bat'
    $sourceWin64Launcher=Join-Path $package 'StartBriefcaseServerNoUI.bat'
    foreach($required in @(
        $proxy,$sourceCore,$sourceMods,$sourceLoaderConfiguration,
        $sourceLauncher,$sourceWin64Launcher)) {
        if(-not (Test-Path -LiteralPath $required)) {
            throw "Missing build output: $required. Run scripts\build\build_server_framework.bat $Configuration first."
        }
    }

    Copy-Item -LiteralPath $proxy -Destination (Join-Path $server 'version.dll') -Force
    $serverInstallRoot=[IO.Path]::GetFullPath((Join-Path $server '..\..\..'))
    Copy-Item -LiteralPath $sourceLauncher `
        -Destination (Join-Path $serverInstallRoot 'StartBriefcaseServer.bat') -Force
    Copy-Item -LiteralPath $sourceWin64Launcher `
        -Destination (Join-Path $server 'StartBriefcaseServerNoUI.bat') -Force
    $framework=Join-Path $server 'Briefcase'
    $core=Join-Path $framework 'Core'
    $mods=Join-Path $framework 'Mods'
    New-Item -ItemType Directory -Path $framework,$core,$mods -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceCore '*') -Destination $core -Recurse -Force

    Get-ChildItem -LiteralPath $sourceMods -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $mods $_.Name) -Force
    }
    foreach($legacyName in @(
        'CommunityBalancing.Server.dll',
        'CommunityBalancing.Server.pdb',
        'ServerAdminControl.Server.dll',
        'ServerAdminControl.Server.pdb')) {
        $legacy=Join-Path $mods $legacyName
        if(Test-Path -LiteralPath $legacy) { Remove-Item -LiteralPath $legacy -Force }
    }

    $loaderConfiguration=Join-Path $framework 'loader.json'
    if(-not (Test-Path -LiteralPath $loaderConfiguration)) {
        Copy-Item -LiteralPath $sourceLoaderConfiguration -Destination $loaderConfiguration
    }

    Write-Host '[OK] Installed Briefcase server proxy and headless Core.'
    Write-Host "[OK] Server mod directory: $mods"
    Write-Host "[OK] Core built-ins: $(Join-Path $core 'BuiltIns')"
    Write-Host "[OK] Headless launcher: $(Join-Path $serverInstallRoot 'StartBriefcaseServer.bat')"
    Write-Host "[OK] Win64 no-UI launcher: $(Join-Path $server 'StartBriefcaseServerNoUI.bat')"
    Write-Host "[INFO] Runtime log: $(Join-Path $framework 'Briefcase.log')"
    exit 0
} catch {
    Write-Host "[ERROR] $($_.Exception.Message)"
    exit 1
}
