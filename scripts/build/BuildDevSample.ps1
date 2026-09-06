[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release')
. (Join-Path $PSScriptRoot '..\Common.ps1')
try {
    $root=Get-ModRoot
    $project=Join-Path $root 'samples\Briefcase.HotReloadSample\Briefcase.HotReloadSample.csproj'
    $result=Invoke-ModNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        'build',$project,'-c',$Configuration)
    if($result -ne 0) { exit $result }

    $source=Join-Path $root "samples\Briefcase.HotReloadSample\bin\$Configuration\net10.0\Briefcase.HotReloadSample.dll"
    $gameMods='D:\SteamLibrary\steamapps\common\DeceiveInc\DeceiveInc\Binaries\Win64\Briefcase\Mods'
    New-Item -ItemType Directory -Path $gameMods -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $gameMods 'Briefcase.HotReloadSample.dll') -Force
    Write-Host '[OK] C# development mod rebuilt and copied. A running game will reload it after the debounce delay.'
    exit 0
} catch { Write-Host "[ERROR] $($_.Exception.Message)"; exit 1 }
