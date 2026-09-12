[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release')

. (Join-Path $PSScriptRoot '..\Common.ps1')

try {
    $root=Get-ModRoot
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $install=@(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if ($install.Count -ne 1) { throw 'MSVC v143 x64 build tools are required.' }
    $msbuild=Join-Path $install[0] 'MSBuild\Current\Bin\amd64\MSBuild.exe'
    $project=Join-Path $root 'loader\Briefcase.Launcher\Briefcase.Launcher.vcxproj'

    Write-Host "Building Briefcase launcher experiment | $Configuration | x64"
    $result=Invoke-ModNative -FilePath $msbuild -WorkingDirectory $root -Arguments @(
        $project,"/p:Configuration=$Configuration",'/p:Platform=x64','/m','/nologo','/verbosity:minimal','/nr:false')
    if($result -ne 0){ exit $result }

    $output=Join-Path $root 'artifacts\launcher-test'
    if(Test-Path -LiteralPath $output) {
        $resolved=[IO.Path]::GetFullPath($output)
        $allowed=[IO.Path]::GetFullPath((Join-Path $root 'artifacts')).TrimEnd('\') + '\'
        if(-not $resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe launcher output path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    $native=Join-Path $output 'Briefcase\Core\Native'
    New-Item -ItemType Directory -Path $native -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root "loader\Briefcase.Launcher\bin\$Configuration\Briefcase.Launcher.exe") -Destination $output
    Copy-Item -LiteralPath (Join-Path $root "loader\Briefcase.Bootstrap\bin\$Configuration\Briefcase.Bootstrap.dll") -Destination $native
    Copy-Item -LiteralPath (Join-Path $root "runtime\Briefcase.UnrealRuntime\bin\$Configuration\Briefcase.UnrealRuntime.dll") -Destination $native

    Write-Host "[OK] Launcher test files: $output"
    Write-Host '[INFO] Overlay these files on an existing Briefcase installation.'
    Write-Host '[INFO] Temporarily rename Win64\version.dll before the injection-only test.'
    exit 0
} catch {
    Write-Host "[ERROR] $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    exit 1
}