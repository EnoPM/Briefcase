[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration='Release',
    [string]$GameWin64='')
. (Join-Path $PSScriptRoot '..\Common.ps1')
try {
    $root=Get-ModRoot
    $project=Join-Path $root 'samples\Briefcase.HotReloadSample\Briefcase.HotReloadSample.csproj'
    $result=Invoke-ModNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        'build',$project,'-c',$Configuration)
    if($result -ne 0) { exit $result }

    $source=Join-Path $root "samples\Briefcase.HotReloadSample\bin\$Configuration\net10.0\Briefcase.HotReloadSample.dll"
    $game=Resolve-BriefcaseLocalPath `
        -Value $GameWin64 `
        -EnvironmentVariable 'BRIEFCASE_CLIENT_GAME_DIR' `
        -LocalSetting 'BriefcaseClientGameWin64' `
        -CommandLineHint '-GameWin64' `
        -Description 'client Win64'
    $gameMods=Join-Path $game 'Briefcase\Mods'
    $package=Join-Path $gameMods 'briefcase.hot-reload-sample'
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $package 'Briefcase.HotReloadSample.dll') -Force
    $manifest=@'
{
  "schemaVersion": 1,
  "entryAssembly": "Briefcase.HotReloadSample.dll",
  "id": "briefcase.hot-reload-sample",
  "version": "1.0.0-dev",
  "dependencies": ["briefcase.hello-managed"]
}
'@
    [IO.File]::WriteAllText(
        (Join-Path $package 'briefcase.mod.json'),
        $manifest + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Write-Host '[OK] C# development mod rebuilt and copied. A running game will reload it after the debounce delay.'
    exit 0
} catch { Write-Host "[ERROR] $($_.Exception.Message)"; exit 1 }
