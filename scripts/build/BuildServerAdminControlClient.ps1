[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release')
. (Join-Path $PSScriptRoot '..\Common.ps1')
try {
    $root=Get-ModRoot
    if(Get-Process -Name 'DeceiveInc-Win64-Shipping' -ErrorAction SilentlyContinue) {
        throw 'Close Deceive Inc. before replacing a Briefcase Core built-in.'
    }
    $project=Join-Path $root 'managed\builtins\ServerAdminControl.Client\ServerAdminControl.Client.csproj'
    $result=Invoke-ModNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        'build',$project,'-c',$Configuration)
    if($result -ne 0){ exit $result }
    $gameFramework='D:\SteamLibrary\steamapps\common\DeceiveInc\DeceiveInc\Binaries\Win64\Briefcase'
    $gameCore=Join-Path $gameFramework 'Core\BuiltIns'
    $gameMods=Join-Path $gameFramework 'Mods'
    New-Item -ItemType Directory -Path $gameCore,$gameMods -Force | Out-Null
    $output=Join-Path $root "managed\builtins\ServerAdminControl.Client\bin\$Configuration\net10.0"
    Copy-Item -LiteralPath (Join-Path $output 'ServerAdminControl.Client.dll') -Destination $gameCore -Force
    Get-ChildItem -LiteralPath $gameFramework -Filter '*.pdb' -File -Recurse |
        Remove-Item -Force

    foreach($legacyName in @('CommunityBalancing.Client.dll','CommunityBalancing.Client.pdb','ServerAdminControl.Client.dll','ServerAdminControl.Client.pdb')) {
        $legacy=Join-Path $gameMods $legacyName
        if(Test-Path -LiteralPath $legacy) { Remove-Item -LiteralPath $legacy -Force }
    }
    Write-Host '[OK] Briefcase client administration built-in copied. Restart the game to load it.'
    exit 0
} catch {
    Write-Host "[ERROR] $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    exit 1
}
