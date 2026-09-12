[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration='Release',
    [string]$GameWin64='')

. (Join-Path $PSScriptRoot '..\Common.ps1')

function Build-ManagedProject(
    [string]$Root,
    [string]$Project,
    [string]$Label,
    [string]$GeneratedSdkProps) {
    Write-Host "Building $Label | $Configuration"
    $arguments=@('build',$Project,'-c',$Configuration)
    if(-not [string]::IsNullOrWhiteSpace($GeneratedSdkProps)) {
        $arguments += "-p:BriefcaseGeneratedClientProps=$GeneratedSdkProps"
    }
    $result=Invoke-ModNative -FilePath 'dotnet' -WorkingDirectory $Root -Arguments @(
        $arguments)
    if($result -ne 0){ exit $result }
}

function Copy-BuiltIn([string]$Root, [string]$ProjectName, [string]$Destination) {
    $output=Join-Path $Root "managed\builtins\$ProjectName\bin\$Configuration\net10.0"
    $source=Join-Path $output "$ProjectName.dll"
    Copy-Item -LiteralPath $source -Destination $Destination -Force
}

try {
    $root=Get-ModRoot
    if([string]::IsNullOrWhiteSpace($GameWin64)) {
        $GameWin64=$env:BRIEFCASE_CLIENT_GAME_DIR
    }
    $gameDirectory=if([string]::IsNullOrWhiteSpace($GameWin64)) {
        $null
    } else {
        [IO.Path]::GetFullPath($GameWin64)
    }
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $install=@(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if ($install.Count -ne 1) { throw 'MSVC v143 x64 build tools are required.' }
    $msbuild=Join-Path $install[0] 'MSBuild\Current\Bin\amd64\MSBuild.exe'
    $runtimeProject=Join-Path $root 'runtime\Briefcase.UnrealRuntime\Briefcase.UnrealRuntime.vcxproj'
    Write-Host "Building Briefcase.UnrealRuntime.dll | $Configuration | x64"
    $result=Invoke-ModNative -FilePath $msbuild -WorkingDirectory $root -Arguments @(
        $runtimeProject,"/p:Configuration=$Configuration",'/p:Platform=x64','/m','/nologo','/verbosity:minimal','/nr:false')
    if($result -ne 0){ exit $result }

    $proxyProject=Join-Path $root 'loader\Briefcase.VersionProxy\Briefcase.VersionProxy.vcxproj'
    Write-Host "Building Briefcase version.dll | $Configuration | x64"
    $result=Invoke-ModNative -FilePath $msbuild -WorkingDirectory $root -Arguments @(
        $proxyProject,"/p:Configuration=$Configuration",'/p:Platform=x64',
        '/p:BuildProjectReferences=false','/m','/nologo','/verbosity:minimal','/nr:false')
    if($result -ne 0){ exit $result }

    $managedHostProject=Join-Path $root 'managed\Briefcase.ManagedHost\Briefcase.ManagedHost.csproj'
    $managedHostPublish=Join-Path $root 'artifacts\publish\Core'
    if(Test-Path -LiteralPath $managedHostPublish) {
        $resolvedPublish=[IO.Path]::GetFullPath($managedHostPublish)
        $artifactPrefix=[IO.Path]::GetFullPath((Join-Path $root 'artifacts')) +
            [IO.Path]::DirectorySeparatorChar
        if(-not $resolvedPublish.StartsWith($artifactPrefix,[StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe managed publish path: $resolvedPublish"
        }
        Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
    }
    Write-Host "Publishing Briefcase managed host | $Configuration | framework-dependent"
    $result=Invoke-ModNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        'publish',$managedHostProject,'-c',$Configuration,'--self-contained','false',
        '--runtime','win-x64','--output',$managedHostPublish)
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
    $avaloniaProject=Join-Path $root 'managed\Briefcase.AvaloniaUi\Briefcase.AvaloniaUi.csproj'
    $avaloniaMenuProject=Join-Path $root 'managed\Briefcase.AvaloniaMenu\Briefcase.AvaloniaMenu.csproj'
    $avaloniaPublish=Join-Path $root 'artifacts\publish\AvaloniaUi'
    if(Test-Path -LiteralPath $avaloniaPublish) {
        $resolvedAvalonia=[IO.Path]::GetFullPath($avaloniaPublish)
        $artifactPrefix=[IO.Path]::GetFullPath((Join-Path $root 'artifacts')) +
            [IO.Path]::DirectorySeparatorChar
        if(-not $resolvedAvalonia.StartsWith($artifactPrefix,[StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe Avalonia publish path: $resolvedAvalonia"
        }
        Remove-Item -LiteralPath $resolvedAvalonia -Recurse -Force
    }
    Write-Host "Building Briefcase Avalonia UI | $Configuration"
    # This is a component assembly loaded into Briefcase's existing .NET host.
    # A RID publish would also republish ManagedHost's executable tooling and
    # can produce duplicate apphost artifacts. A clean build output contains
    # the component deps file and all package assets required by its ALC.
    $result=Invoke-ModNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        'build',$avaloniaProject,'-c',$Configuration,'--no-incremental',
        '--output',$avaloniaPublish)
    if($result -ne 0){ exit $result }
    Write-Host "Building lazy Briefcase Avalonia menu | $Configuration"
    $result=Invoke-ModNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        'build',$avaloniaMenuProject,'-c',$Configuration,'--no-incremental',
        '--output',$avaloniaPublish)
    if($result -ne 0){ exit $result }

    # Compile repository code against the reviewed, complete snapshot for this
    # exact executable build. A live runtime snapshot can be intentionally
    # bounded and therefore omit types that were not loaded during that launch;
    # it is only a fallback when the repository snapshot is unavailable.
    $build='6A96564B-06283000'
    $snapshotSource=Join-Path $root "sdk\snapshots\DeceiveInc.Client.$build.json"
    if(-not (Test-Path -LiteralPath $snapshotSource) -and $null -ne $gameDirectory) {
        $snapshotSource=Join-Path $gameDirectory "Briefcase\Core\Sdk\Metadata\DeceiveInc.Client.$build.json"
    }
    if(-not (Test-Path -LiteralPath $snapshotSource)) {
        throw "No client SDK snapshot is available for build $build. Provide -GameWin64 or BRIEFCASE_CLIENT_GAME_DIR to use an installed snapshot."
    }
    $buildCore=Join-Path $root 'artifacts\build-sdk\Core'
    if(Test-Path -LiteralPath $buildCore) {
        $resolvedBuildCore=[IO.Path]::GetFullPath($buildCore)
        $artifactPrefix=[IO.Path]::GetFullPath((Join-Path $root 'artifacts')) +
            [IO.Path]::DirectorySeparatorChar
        if(-not $resolvedBuildCore.StartsWith($artifactPrefix,[StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe SDK build path: $resolvedBuildCore"
        }
        Remove-Item -LiteralPath $resolvedBuildCore -Recurse -Force
    }
    $snapshot=Join-Path $buildCore "Sdk\Metadata\DeceiveInc.Client.$build.json"
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($snapshot)) -Force | Out-Null
    $snapshotContents=[IO.File]::ReadAllText($snapshotSource)
    [IO.File]::WriteAllText($snapshot,$snapshotContents,[Text.UTF8Encoding]::new($false))
    $sdkEmitterProject=Join-Path $root 'managed\Briefcase.SdkEmitter\Briefcase.SdkEmitter.csproj'
    Write-Host "Generating the client SDK | $Configuration"
    $result=Invoke-ModNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        'run','--project',$sdkEmitterProject,'-c',$Configuration,'--no-launch-profile','--',
        'production',$snapshot,$buildCore)
    if($result -ne 0){ exit $result }
    $generatedSdkProps=Join-Path $buildCore 'Sdk\Generated\Client\Current.props'
    if(-not (Test-Path -LiteralPath $generatedSdkProps)) {
        throw "Generated SDK reference was not published: $generatedSdkProps"
    }

    foreach($projectName in @(
        'Briefcase.EventSample',
        'Briefcase.HelloSample',
        'Briefcase.HotReloadSample')) {
        Build-ManagedProject $root `
            (Join-Path $root "samples\$projectName\$projectName.csproj") `
            $projectName `
            $generatedSdkProps
    }
    Build-ManagedProject $root `
        (Join-Path $root 'managed\builtins\Briefcase.ServerBrowser.Client\Briefcase.ServerBrowser.Client.csproj') `
        'Briefcase Core server browser (client)' `
        $generatedSdkProps
    Build-ManagedProject $root `
        (Join-Path $root 'managed\builtins\ServerAdminControl.Client\ServerAdminControl.Client.csproj') `
        'Briefcase Core server administration (client)' `
        $generatedSdkProps

    $gameExe=if($null -eq $gameDirectory) {
        $null
    } else {
        Join-Path $gameDirectory 'DeceiveInc-Win64-Shipping.exe'
    }
    if($null -ne $gameExe -and (Test-Path -LiteralPath $gameExe)) {
        $bytes=[IO.File]::ReadAllBytes($gameExe)
        $pe=[BitConverter]::ToInt32($bytes,0x3C)
        $timestamp=[BitConverter]::ToUInt32($bytes,$pe+8)
        $imageSize=[BitConverter]::ToUInt32($bytes,$pe+0x50)
        if($timestamp -ne 0x6A96564B -or $imageSize -ne 0x06283000){
            throw "Game executable does not match RuntimeProfile.h: $gameExe"
        }
        Write-Host '[OK] Installed executable matches the Briefcase runtime profile.'
    }

    $distribution=Join-Path $root 'dist\Briefcase'
    if(Test-Path -LiteralPath $distribution) {
        $resolved=[IO.Path]::GetFullPath($distribution)
        if(-not $resolved.StartsWith(([IO.Path]::GetFullPath($root) + [IO.Path]::DirectorySeparatorChar),
            [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe distribution path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }

    $frameworkDistribution=Join-Path $distribution 'Briefcase'
    $coreDistribution=Join-Path $frameworkDistribution 'Core'
    $nativeDistribution=Join-Path $coreDistribution 'Native'
    $avaloniaDistribution=Join-Path $coreDistribution 'Ui\Avalonia'
    $builtInsDistribution=Join-Path $coreDistribution 'BuiltIns'
    $dotNetDistribution=Join-Path $coreDistribution 'DotNet'
    $updaterDistribution=Join-Path $coreDistribution 'Updater'
    $modsDistribution=Join-Path $frameworkDistribution 'Mods'
    New-Item -ItemType Directory -Path $coreDistribution,$nativeDistribution,$avaloniaDistribution,$builtInsDistribution,$updaterDistribution,$modsDistribution -Force | Out-Null

    Copy-Item -LiteralPath (Join-Path $root 'VERSION') `
        -Destination (Join-Path $frameworkDistribution 'VERSION') -Force

    Copy-Item -LiteralPath (Join-Path $root "loader\Briefcase.VersionProxy\bin\$Configuration\version.dll") `
        -Destination (Join-Path $distribution 'version.dll') -Force
    Copy-Item -LiteralPath (Join-Path $root "runtime\Briefcase.UnrealRuntime\bin\$Configuration\Briefcase.UnrealRuntime.dll") `
        -Destination (Join-Path $nativeDistribution 'Briefcase.UnrealRuntime.dll') -Force
    Copy-Item -LiteralPath (Join-Path $updateInstallerPublish 'Briefcase.UpdateInstaller.exe') `
        -Destination (Join-Path $updaterDistribution 'Briefcase.UpdateInstaller.exe') -Force

    Copy-Item -Path (Join-Path $managedHostPublish '*') -Destination $coreDistribution -Recurse -Force
    Copy-Item -Path (Join-Path $avaloniaPublish '*') -Destination $avaloniaDistribution -Recurse -Force
    # The Avalonia publish contains project-reference copies. Core already owns
    # those assemblies; the custom load context deliberately shares them.
    @(
        'Briefcase.ManagedHost.dll',
        'Briefcase.ManagedHost.pdb',
        'Briefcase.Updater.dll',
        'Briefcase.Updater.pdb',
        'Briefcase.ModApi.dll',
        'Briefcase.ModApi.pdb',
        'Briefcase.ClientModApi.dll',
        'Briefcase.ClientModApi.pdb',
        'Briefcase.Rendering.dll',
        'Briefcase.Rendering.pdb',
        'Briefcase.SdkEmitter.dll',
        'Briefcase.SdkEmitter.pdb',
        'Briefcase.SdkSnapshots.dll',
        'Briefcase.SdkSnapshots.pdb'
    ) | ForEach-Object {
        $sharedCopy=Join-Path $avaloniaDistribution $_
        if(Test-Path -LiteralPath $sharedCopy) { Remove-Item -LiteralPath $sharedCopy -Force }
    }
    # Native package symbols are useful when developing Avalonia itself, but
    # add tens of megabytes and are not useful for diagnosing Briefcase code.
    Get-ChildItem -LiteralPath $coreDistribution -Filter '*.pdb' -File -Recurse |
        Where-Object { $_.Name -like 'lib*Sharp.pdb' -or $_.DirectoryName -like '*\runtimes\*' } |
        Remove-Item -Force
    # The package is Windows x64-only. Keep the component deps metadata,
    # but omit native assets for platforms that this loader cannot start on.
    $runtimeAssets=Join-Path $avaloniaDistribution 'runtimes'
    if(Test-Path -LiteralPath $runtimeAssets) {
        Get-ChildItem -LiteralPath $runtimeAssets -Directory |
            Where-Object { $_.Name -ne 'win-x64' } |
            Remove-Item -Recurse -Force
    }
    # Briefcase.SdkEmitter is a library at runtime. Its CLI companions are used
    # only by repository validation and do not belong in the game package.
    @(
        'Briefcase.SdkEmitter.exe',
        'Briefcase.SdkEmitter.deps.json',
        'Briefcase.SdkEmitter.runtimeconfig.json'
    ) | ForEach-Object {
        $cliArtifact=Join-Path $coreDistribution $_
        if(Test-Path -LiteralPath $cliArtifact) { Remove-Item -LiteralPath $cliArtifact -Force }
    }

    $dotnetRoot=Join-Path $env:ProgramFiles 'dotnet'
    $hostFxrRoot=Join-Path $dotnetRoot 'host\fxr'
    $sharedRoot=Join-Path $dotnetRoot 'shared\Microsoft.NETCore.App'
    $runtimeVersions=Get-ChildItem -LiteralPath $hostFxrRoot -Directory |
        Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName 'hostfxr.dll')) -and
            (Test-Path -LiteralPath (Join-Path $sharedRoot $_.Name))
        } |
        Sort-Object { [version]$_.Name } -Descending
    if($runtimeVersions.Count -eq 0) {
        throw 'A matching x64 Microsoft.NETCore.App runtime and hostfxr were not found.'
    }
    $runtimeVersion=$runtimeVersions[0].Name
    $bundledFxr=Join-Path $dotNetDistribution "host\fxr\$runtimeVersion"
    $bundledShared=Join-Path $dotNetDistribution "shared\Microsoft.NETCore.App\$runtimeVersion"
    New-Item -ItemType Directory -Path $bundledFxr,$bundledShared -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $runtimeVersions[0].FullName 'hostfxr.dll') -Destination $bundledFxr
    Copy-Item -Path (Join-Path $sharedRoot "$runtimeVersion\*") -Destination $bundledShared -Recurse -Force

    # User mods are built and distributed independently. Keeping this directory
    # empty makes it impossible for a framework release to include private mod
    # assemblies by accident.
    Copy-BuiltIn $root 'Briefcase.ServerBrowser.Client' $builtInsDistribution
    Copy-BuiltIn $root 'ServerAdminControl.Client' $builtInsDistribution

    $loaderConfiguration = @'
{
  "schemaVersion": 1,
  "automaticUpdates": true,
  "updateRestartMode": "auto",
  "sdkSnapshotFormat": "binary",
  "sdkSnapshotRefresh": "missing",
  "avaloniaMenuLifetime": "cached",
  "avaloniaMenuMargins": {
    "horizontalPercent": 12.5,
    "verticalPercent": 8.0
  },
  "gameWindowChrome": false
}
'@
    [IO.File]::WriteAllText(
        (Join-Path $frameworkDistribution 'loader.json'),
        $loaderConfiguration,
        [Text.UTF8Encoding]::new($false))

    Organize-BriefcaseFrameworkPackage $frameworkDistribution

    # Inter is embedded in Briefcase.AvaloniaUi.dll. Keep its SIL Open Font
    # License beside the other third-party runtime assets in every client build.
    Copy-Item -LiteralPath (Join-Path $root 'third_party\inter\OFL.txt') `
        -Destination (Join-Path $coreDistribution 'ThirdPartyLibraries\Inter.OFL.txt') -Force

    Write-Host "[OK] Briefcase package: $distribution"
    Write-Host "[OK] Bundled .NET ${runtimeVersion}: $dotNetDistribution"
    Write-Host "[OK] Managed mods: $modsDistribution"
    Write-Host "[OK] Core built-ins: $builtInsDistribution"
    Write-Host '[OK] Nothing was deployed.'
    exit 0
} catch {
    Write-Host "[ERROR] $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    exit 1
}
