[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release')
. (Join-Path $PSScriptRoot '..\Common.ps1')
try {
    $root=Get-ModRoot
    if(Get-Process -Name 'DeceiveIncServer-Win64-Shipping' -ErrorAction SilentlyContinue) {
        throw 'Stop the dedicated server before replacing a Briefcase Core built-in.'
    }
    $project=Join-Path $root 'managed\builtins\ServerAdminControl.Server\ServerAdminControl.Server.csproj'
    $result=Invoke-ModNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        'build',$project,'-c',$Configuration)
    if($result -ne 0){ exit $result }

    $serverFramework='D:\GameServers\steamcmd\steamapps\common\Deceive Inc. Dedicated Server\DeceiveInc\Binaries\Win64\Briefcase'
    $serverCore=Join-Path $serverFramework 'Core\BuiltIns'
    $serverMods=Join-Path $serverFramework 'Mods'
    New-Item -ItemType Directory -Path $serverCore,$serverMods -Force | Out-Null
    $output=Join-Path $root "managed\builtins\ServerAdminControl.Server\bin\$Configuration\net10.0"
    Copy-Item -LiteralPath (Join-Path $output 'ServerAdminControl.Server.dll') -Destination $serverCore -Force
    Get-ChildItem -LiteralPath $serverFramework -Filter '*.pdb' -File -Recurse |
        Remove-Item -Force

    foreach($legacyName in @('CommunityBalancing.Server.dll','CommunityBalancing.Server.pdb','ServerAdminControl.Server.dll','ServerAdminControl.Server.pdb')) {
        $legacy=Join-Path $serverMods $legacyName
        if(Test-Path -LiteralPath $legacy) { Remove-Item -LiteralPath $legacy -Force }
    }
    Write-Host '[OK] Briefcase server administration built-in copied. Restart the server to load it.'
    exit 0
} catch {
    Write-Host "[ERROR] $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    exit 1
}
