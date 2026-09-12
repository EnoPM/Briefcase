[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration='Release',
    [string]$ServerWin64='')

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')

try {
    $root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $server=Resolve-BriefcaseLocalPath `
        -Value $ServerWin64 `
        -EnvironmentVariable 'BRIEFCASE_SERVER_GAME_DIR' `
        -LocalSetting 'BriefcaseServerGameWin64' `
        -CommandLineHint '-ServerWin64' `
        -Description 'server Win64'
    $shippingExecutable=Join-Path $server 'DeceiveIncServer-Win64-Shipping.exe'
    $activeServer=Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -eq $shippingExecutable
    }
    if($activeServer) {
        throw "Stop the Deceive Inc. dedicated server at $server before installing Briefcase Core."
    }

    $package=Join-Path $root 'dist\Briefcase.Server'
    $proxy=Join-Path $package 'version.dll'
    $sourceFramework=Join-Path $package 'Briefcase'
    $sourceCore=Join-Path $sourceFramework 'Core'
    $sourceRuntime=Join-Path $sourceCore 'Native\Briefcase.UnrealRuntime.dll'
    $sourceMods=Join-Path $sourceFramework 'Mods'
    $sourceLoaderConfiguration=Join-Path $sourceFramework 'loader.json'
    $sourceVersion=Join-Path $sourceFramework 'VERSION'
    $sourceLauncher=Join-Path $package 'StartBriefcaseServer.bat'
    $sourceWin64Launcher=Join-Path $package 'StartBriefcaseServerNoUI.bat'
    foreach($required in @(
        $proxy,$sourceCore,$sourceRuntime,$sourceMods,$sourceLoaderConfiguration,$sourceVersion,
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
    Copy-Item -LiteralPath $sourceVersion -Destination (Join-Path $framework 'VERSION') -Force

    # Mirror the framework-owned Core while preserving generated SDK and cache data.
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

    Get-ChildItem -LiteralPath $framework -Filter '*.pdb' -File -Recurse |
        Remove-Item -Force

    Write-Host '[OK] Installed Briefcase server proxy and headless Core.'
    Write-Host "[OK] Native runtime: $(Join-Path $core 'Native\Briefcase.UnrealRuntime.dll')"
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
