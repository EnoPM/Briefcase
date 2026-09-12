[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration='Release',
    [string]$ServerWin64='')

. (Join-Path $PSScriptRoot '..\Common.ps1')

function Remove-SafeDirectory([string]$Path, [string]$AllowedRoot) {
    if(-not (Test-Path -LiteralPath $Path)) { return }
    $resolved=[IO.Path]::GetFullPath($Path)
    $prefix=[IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\') + '\'
    if(-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe cleanup path: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

try {
    $root=Get-ModRoot
    $serverWin64=Resolve-BriefcaseLocalPath `
        -Value $ServerWin64 `
        -EnvironmentVariable 'BRIEFCASE_SERVER_GAME_DIR' `
        -LocalSetting 'BriefcaseServerGameWin64' `
        -CommandLineHint '-ServerWin64' `
        -Description 'server Win64' `
        -Optional
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $install=@(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if($install.Count -ne 1) { throw 'MSVC v143 x64 build tools are required.' }
    $msbuild=Join-Path $install[0] 'MSBuild\Current\Bin\amd64\MSBuild.exe'

    Write-Host "Building Briefcase server native runtime | $Configuration | x64"
    $runtimeProject=Join-Path $root 'runtime\Briefcase.UnrealRuntime\Briefcase.UnrealRuntime.vcxproj'
    $result=Invoke-ModNative -FilePath $msbuild -WorkingDirectory $root -Arguments @(
        $runtimeProject,"/p:Configuration=$Configuration",'/p:Platform=x64','/m','/nologo','/verbosity:minimal','/nr:false')
    if($result -ne 0){ exit $result }

    Write-Host "Building Briefcase server proxy | $Configuration | x64"
    $proxyProject=Join-Path $root 'loader\Briefcase.VersionProxy\Briefcase.VersionProxy.vcxproj'
    $result=Invoke-ModNative -FilePath $msbuild -WorkingDirectory $root -Arguments @(
        $proxyProject,"/p:Configuration=$Configuration",'/p:Platform=x64',
        '/p:BuildProjectReferences=false','/m','/nologo','/verbosity:minimal','/nr:false')
    if($result -ne 0){ exit $result }

    $artifactRoot=Join-Path $root 'artifacts'
    $publishCore=Join-Path $artifactRoot 'publish-server\Core'
    Remove-SafeDirectory $publishCore $artifactRoot
    Write-Host "Publishing Briefcase headless host | $Configuration"
    $managedHost=Join-Path $root 'managed\Briefcase.ManagedHost\Briefcase.ManagedHost.csproj'
    $result=Invoke-ModNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        'publish',$managedHost,'-c',$Configuration,'--self-contained','false',
        '-p:BriefcaseHeadless=true','--output',$publishCore)
    if($result -ne 0){ exit $result }

    $updateInstallerProject=Join-Path $root 'managed\Briefcase.UpdateInstaller\Briefcase.UpdateInstaller.csproj'
    $updateInstallerPublish=Join-Path $root 'artifacts\publish\UpdateInstaller'
    if(Test-Path -LiteralPath $updateInstallerPublish) {
        $resolvedUpdater=[IO.Path]::GetFullPath($updateInstallerPublish)
        $artifactPrefix=[IO.Path]::GetFullPath((Join-Path $root 'artifacts')).TrimEnd('\') + '\'
        if(-not $resolvedUpdater.StartsWith($artifactPrefix,[StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe update installer publish path: $resolvedUpdater"
        }
        Remove-Item -LiteralPath $resolvedUpdater -Recurse -Force
    }
    Write-Host "Publishing Briefcase update installer | $Configuration | Native AOT"
    $result=Invoke-ModNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        'publish',$updateInstallerProject,'-c',$Configuration,'--runtime','win-x64',
        '--self-contained','true','--output',$updateInstallerPublish)
    if($result -ne 0){ exit $result }
    $build='6A966107-05B60000'
    # CI and clean developer machines use the reviewed, address-free snapshot
    # archive committed for this exact executable build. Keeping this large
    # JSON as a ZIP saves repository space and makes Git treat it as binary.
    $snapshotFileName="DeceiveInc.Server.$build.json"
    $snapshotArchive=Join-Path $root "sdk\snapshots\$snapshotFileName.zip"
    $installedSnapshot=if($null -eq $serverWin64) {
        $null
    } else {
        Join-Path $serverWin64 "Briefcase\Core\Sdk\Metadata\$snapshotFileName"
    }
    $snapshotContents=$null
    if(Test-Path -LiteralPath $snapshotArchive) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive=[IO.Compression.ZipFile]::OpenRead($snapshotArchive)
        try {
            $entries=@($archive.Entries)
            if($entries.Count -ne 1 -or $entries[0].FullName -ne $snapshotFileName) {
                throw "Server snapshot archive must contain only $snapshotFileName."
            }
            if($entries[0].Length -gt 32MB) {
                throw "Server snapshot archive exceeds the 32 MiB safety limit."
            }
            $reader=[IO.StreamReader]::new(
                $entries[0].Open(),
                [Text.UTF8Encoding]::new($false),
                $true)
            try { $snapshotContents=$reader.ReadToEnd() }
            finally { $reader.Dispose() }
        }
        finally { $archive.Dispose() }
        Write-Host "Using repository server snapshot: $snapshotArchive"
    }
    elseif($null -ne $installedSnapshot -and (Test-Path -LiteralPath $installedSnapshot)) {
        # A live installed-server snapshot remains a local development fallback.
        $snapshotContents=[IO.File]::ReadAllText($installedSnapshot)
        Write-Host "Using installed server snapshot: $installedSnapshot"
    }
    else {
        throw "No server SDK snapshot is available for build $build."
    }

    $serverBuildCore=Join-Path $artifactRoot 'server-sdk\Core'
    Remove-SafeDirectory $serverBuildCore $artifactRoot
    $jsonSnapshot=Join-Path $serverBuildCore "Sdk\Metadata\DeceiveInc.Server.$build.json"
    $snapshot=Join-Path $serverBuildCore "Sdk\Metadata\DeceiveInc.Server.$build.bsnap"
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($snapshot)) -Force | Out-Null
    [IO.File]::WriteAllText(
        $jsonSnapshot,
        $snapshotContents,
        [Text.UTF8Encoding]::new($false))
    $sdkEmitter=Join-Path $publishCore 'Briefcase.SdkEmitter.dll'
    $result=Invoke-ModNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        $sdkEmitter,'convert',$jsonSnapshot,$snapshot)
    if($result -ne 0){ exit $result }
    Remove-Item -LiteralPath $jsonSnapshot -Force
    $result=Invoke-ModNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        $sdkEmitter,'production',$snapshot,$serverBuildCore)
    if($result -ne 0){ exit $result }
    $generatedProps=Join-Path $serverBuildCore 'Sdk\Generated\Server\Current.props'
    if(-not (Test-Path -LiteralPath $generatedProps)) {
        throw "Generated server SDK reference was not published: $generatedProps"
    }

    Write-Host "Building ServerAdminControl.Server | $Configuration"
    $serverModProject=Join-Path $root 'managed\builtins\ServerAdminControl.Server\ServerAdminControl.Server.csproj'
    $result=Invoke-ModNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        'build',$serverModProject,'-c',$Configuration,
        "-p:BriefcaseGeneratedServerProps=$generatedProps")
    if($result -ne 0){ exit $result }

    $distribution=Join-Path $root 'dist\Briefcase.Server'
    Remove-SafeDirectory $distribution (Join-Path $root 'dist')
    $framework=Join-Path $distribution 'Briefcase'
    $core=Join-Path $framework 'Core'
    $native=Join-Path $core 'Native'
    $builtIns=Join-Path $core 'BuiltIns'
    $mods=Join-Path $framework 'Mods'
    $dotnetDistribution=Join-Path $core 'DotNet'
    $updaterDistribution=Join-Path $core 'Updater'
    New-Item -ItemType Directory -Path $core,$native,$builtIns,$updaterDistribution,$mods -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'VERSION') `
        -Destination (Join-Path $framework 'VERSION') -Force
    Copy-Item -LiteralPath (Join-Path $root "loader\Briefcase.VersionProxy\bin\$Configuration\version.dll") `
        -Destination (Join-Path $distribution 'version.dll')
    Copy-Item -LiteralPath (Join-Path $root "runtime\Briefcase.UnrealRuntime\bin\$Configuration\Briefcase.UnrealRuntime.dll") `
        -Destination (Join-Path $native 'Briefcase.UnrealRuntime.dll')
    Copy-Item -LiteralPath (Join-Path $updateInstallerPublish 'Briefcase.UpdateInstaller.exe') `
        -Destination (Join-Path $updaterDistribution 'Briefcase.UpdateInstaller.exe') -Force
    Copy-Item -LiteralPath (Join-Path $root 'scripts\server\StartBriefcaseServer.bat') `
        -Destination (Join-Path $distribution 'StartBriefcaseServer.bat')
    Copy-Item -LiteralPath (Join-Path $root 'scripts\server\StartBriefcaseServerNoUI.bat') `
        -Destination (Join-Path $distribution 'StartBriefcaseServerNoUI.bat')
    Copy-Item -Path (Join-Path $publishCore '*') -Destination $core -Recurse -Force
    @(
        'Briefcase.SdkEmitter.exe',
        'Briefcase.SdkEmitter.deps.json',
        'Briefcase.SdkEmitter.runtimeconfig.json'
    ) | ForEach-Object {
        $cliArtifact=Join-Path $core $_
        if(Test-Path -LiteralPath $cliArtifact) { Remove-Item -LiteralPath $cliArtifact -Force }
    }
    Copy-Item -LiteralPath (Join-Path $serverBuildCore 'Sdk') -Destination $core -Recurse

    $dotnetRoot=Join-Path $env:ProgramFiles 'dotnet'
    $hostFxrRoot=Join-Path $dotnetRoot 'host\fxr'
    $sharedRoot=Join-Path $dotnetRoot 'shared\Microsoft.NETCore.App'
    $runtimeVersions=Get-ChildItem -LiteralPath $hostFxrRoot -Directory |
        Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName 'hostfxr.dll')) -and
            (Test-Path -LiteralPath (Join-Path $sharedRoot $_.Name))
        } |
        Sort-Object { [version]$_.Name } -Descending
    if($runtimeVersions.Count -eq 0) { throw 'A matching x64 .NET runtime was not found.' }
    $runtimeVersion=$runtimeVersions[0].Name
    $bundledFxr=Join-Path $dotnetDistribution "host\fxr\$runtimeVersion"
    $bundledShared=Join-Path $dotnetDistribution "shared\Microsoft.NETCore.App\$runtimeVersion"
    New-Item -ItemType Directory -Path $bundledFxr,$bundledShared -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $runtimeVersions[0].FullName 'hostfxr.dll') -Destination $bundledFxr
    Copy-Item -Path (Join-Path $sharedRoot "$runtimeVersion\*") -Destination $bundledShared -Recurse -Force

    $modOutput=Join-Path $root "managed\builtins\ServerAdminControl.Server\bin\$Configuration\net10.0"
    Copy-Item -LiteralPath (Join-Path $modOutput 'ServerAdminControl.Server.dll') -Destination $builtIns
    # Server mods are built and distributed independently from Briefcase Core.
    [IO.File]::WriteAllText(
        (Join-Path $framework 'loader.json'),
        "{`n  `"schemaVersion`": 1,`n  `"automaticUpdates`": true,`n  `"updateRestartMode`": `"auto`",`n  `"sdkSnapshotFormat`": `"binary`",`n  `"sdkSnapshotRefresh`": `"missing`"`n}`n",
        [Text.UTF8Encoding]::new($false))

    Organize-BriefcaseFrameworkPackage $framework

    $forbidden=@(Get-ChildItem -LiteralPath $framework -File -Recurse | Where-Object {
        $_.Name -match '^(ImGui|cimgui|Briefcase\.Rendering|Briefcase\.ClientModApi|Briefcase\.AvaloniaUi|Briefcase\.AvaloniaMenu|Avalonia\.|SkiaSharp|Vortice\.|SharpGen\.)'
    })
    if($forbidden.Count -ne 0) {
        throw "The server package contains client UI files: $($forbidden.Name -join ', ')"
    }

    Write-Host "[OK] Headless server package: $distribution"
    Write-Host "[OK] Generated server SDK: $generatedProps"
    Write-Host "[OK] Bundled .NET ${runtimeVersion}: $dotnetDistribution"
    Write-Host '[OK] No client UI, Avalonia, rendering, or Skia binary is present.'
    exit 0
} catch {
    Write-Host "[ERROR] $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    exit 1
}
