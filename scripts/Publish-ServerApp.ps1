param(
    [string]$OutputDirectory,
    [string]$NativeBuildDirectory,
    [switch]$EnableUpdates,
    [switch]$SkipArchive,
    [string]$VersionOverride
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = if ([string]::IsNullOrWhiteSpace($VersionOverride)) {
    (Get-Content -LiteralPath (Join-Path $projectRoot 'VERSION') -Raw).Trim()
} else { $VersionOverride.Trim() }
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain a three-part numeric version.' }
$project = Join-Path $projectRoot 'Briefcase.ServerApp/Briefcase.ServerApp.csproj'
$launcherProject = Join-Path $projectRoot 'Briefcase.ServerApp.Launcher/Briefcase.ServerApp.Launcher.csproj'
$publishDirectory = Join-Path $projectRoot 'artifacts/server-app-aot'
$launcherPublishDirectory = Join-Path $projectRoot 'artifacts/server-app-launcher-aot'
$nativeExecutable = Join-Path $projectRoot 'Briefcase.ServerApp/bin/Release/net10.0/win-x64/native/Briefcase.ServerApp.exe'
$nativeLauncherExecutable = Join-Path $projectRoot 'Briefcase.ServerApp.Launcher/bin/Release/net10.0/win-x64/native/Briefcase.Launcher.exe'
if ([string]::IsNullOrWhiteSpace($NativeBuildDirectory)) {
    $NativeBuildDirectory = Join-Path $projectRoot '../BriefcaseNative/build'
}
$nativeBuild = [System.IO.Path]::GetFullPath($NativeBuildDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $nativeBuild 'CMakeCache.txt') -PathType Leaf)) {
    throw "Configure BriefcaseNative first, then provide its build directory: $nativeBuild"
}
if ($env:VSCMD_ARG_TGT_ARCH -eq 'x64') {
    cmake --build $nativeBuild --config Release --target Briefcase.ServerInjector Briefcase.ServerBootstrap
} else {
    $vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio C++ build tools are required to package ServerApp.' }
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $devShell = Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
    if (-not (Test-Path -LiteralPath $devShell -PathType Leaf)) { throw 'Visual Studio developer command prompt is unavailable.' }
    $buildCommand = '"' + $devShell + '" -arch=x64 >nul && cmake --build "' + $nativeBuild + '" --config Release --target Briefcase.ServerInjector Briefcase.ServerBootstrap'
    & cmd.exe /d /s /c $buildCommand
}
if ($LASTEXITCODE -ne 0) { throw 'ServerApp native injection components failed to build.' }
$nativeFiles = @('Native/Briefcase.ServerInjector.exe', 'Native/Briefcase.ServerBootstrap.dll')
foreach ($name in $nativeFiles) {
    $source = Join-Path $nativeBuild ([IO.Path]::GetFileName($name))
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        $source = Join-Path (Join-Path $nativeBuild 'Release') ([IO.Path]::GetFileName($name))
    }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing ServerApp native component: $name" }
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'dist/ServerApp'
}
$destination = [System.IO.Path]::GetFullPath($OutputDirectory)
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))
if (-not $destination.StartsWith($distRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The output directory must be below this project''s dist directory.'
}
$check = $destination
while ($check.StartsWith($distRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    if (Test-Path -LiteralPath $check) {
        $entry = Get-Item -LiteralPath $check -Force
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing a redirected output path: $check"
        }
    }
    if ($check -eq $distRoot) { break }
    $check = [System.IO.Path]::GetDirectoryName($check)
}

$env:AVALONIA_TELEMETRY_OPTOUT = '1'
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:StripSymbols=true -p:BriefcaseNativeBuildDirectory=$nativeBuild -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'ServerApp AOT publication failed.' }
dotnet publish $launcherProject -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:StripSymbols=true -o $launcherPublishDirectory
if ($LASTEXITCODE -ne 0) { throw 'ServerApp Launcher AOT publication failed.' }

$runtimeFiles = @('libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'av_libglesv2.dll')
foreach ($name in $runtimeFiles) {
    $appRuntime = Join-Path $publishDirectory $name
    $launcherRuntime = Join-Path $launcherPublishDirectory $name
    if (-not (Test-Path -LiteralPath $appRuntime -PathType Leaf) -or
        -not (Test-Path -LiteralPath $launcherRuntime -PathType Leaf) -or
        (Get-FileHash -LiteralPath $appRuntime -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $launcherRuntime -Algorithm SHA256).Hash) {
        throw "The app and launcher require different copies of $name."
    }
}

if (Test-Path -LiteralPath $destination) {
    Remove-Item -LiteralPath $destination -Recurse -Force
}
New-Item -ItemType Directory -Path $destination | Out-Null
$appDirectory = Join-Path $destination "App/versions/$version"
New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
$appExecutable = Join-Path $publishDirectory 'Briefcase.ServerApp.exe'
if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) { $appExecutable = $nativeExecutable }
$launcherExecutable = Join-Path $launcherPublishDirectory 'Briefcase.Launcher.exe'
if (-not (Test-Path -LiteralPath $launcherExecutable -PathType Leaf)) { $launcherExecutable = $nativeLauncherExecutable }
if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf) -or
    -not (Test-Path -LiteralPath $launcherExecutable -PathType Leaf)) { throw 'Missing AOT application or launcher.' }
Copy-Item -LiteralPath $appExecutable -Destination (Join-Path $appDirectory 'Briefcase.ServerApp.exe')
Copy-Item -LiteralPath $launcherExecutable -Destination (Join-Path $destination 'Briefcase.Launcher.exe')
foreach ($name in $runtimeFiles) {
    Copy-Item -LiteralPath (Join-Path $publishDirectory $name) -Destination (Join-Path $appDirectory $name)
    Copy-Item -LiteralPath (Join-Path $launcherPublishDirectory $name) -Destination (Join-Path $destination $name)
}
$launcherFiles = @('Briefcase.Launcher.exe') + $runtimeFiles
$launcherManifestFiles = @($launcherFiles | ForEach-Object {
    $file = Join-Path $destination $_
    [ordered]@{
        path = $_
        bytes = (Get-Item -LiteralPath $file).Length
        sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$launcherManifestDirectory = Join-Path $destination 'Launcher'
New-Item -ItemType Directory -Path $launcherManifestDirectory -Force | Out-Null
$launcherManifest = [ordered]@{ schemaVersion = 1; version = $version; files = $launcherManifestFiles }
[IO.File]::WriteAllText((Join-Path $launcherManifestDirectory 'manifest.json'),
    ($launcherManifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
foreach ($name in $nativeFiles) {
    $source = Join-Path $publishDirectory $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing app-private injection component: $name" }
    $target = Join-Path $appDirectory $name
    New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
$translationDestination = Join-Path $appDirectory 'Translations'
New-Item -ItemType Directory -Path $translationDestination -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $destination 'Translations') -Force | Out-Null
foreach ($language in @('en', 'fr')) {
    $translation = Join-Path $projectRoot "Briefcase.ServerApp/Translations/$language.json"
    if (-not (Test-Path -LiteralPath $translation -PathType Leaf)) {
        throw "Missing translation file: $translation"
    }
    Copy-Item -LiteralPath $translation -Destination (Join-Path $translationDestination "$language.json")
    Copy-Item -LiteralPath $translation -Destination (Join-Path $destination "Translations/$language.json")
}
$formSchema = Join-Path $publishDirectory 'Schemas/ServerConfiguration.json'
if (-not (Test-Path -LiteralPath $formSchema -PathType Leaf)) {
    throw "Missing published server configuration schema: $formSchema"
}
$schemaDestination = Join-Path $appDirectory 'Schemas'
New-Item -ItemType Directory -Path $schemaDestination -Force | Out-Null
Copy-Item -LiteralPath $formSchema -Destination (Join-Path $schemaDestination 'ServerConfiguration.json')
$manifestFiles = @(Get-ChildItem -LiteralPath $appDirectory -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($appDirectory.Length + 1).Replace([char]92, [char]47)
    [ordered]@{
        path = $relative
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$manifest = [ordered]@{ schemaVersion = 1; version = $version; files = $manifestFiles }
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $appDirectory 'manifest.json'), ($manifest | ConvertTo-Json -Depth 8), $utf8)
[IO.File]::WriteAllText((Join-Path $destination 'App/current.json'), (@{ version = $version } | ConvertTo-Json), $utf8)
[IO.File]::WriteAllText((Join-Path $destination 'UpdateSource.json'),
    (@{ schemaVersion = 1; enabled = [bool]$EnableUpdates; repository = 'EnoPM/Briefcase' } | ConvertTo-Json), $utf8)

if ($SkipArchive) {
    Write-Output "ServerApp AOT folder ready: $destination"
    return
}

$archive = Join-Path $distRoot "Briefcase.ServerApp-win-x64-$version.zip"
if (Test-Path -LiteralPath $archive) {
    $entry = Get-Item -LiteralPath $archive -Force
    if ($entry.PSIsContainer -or ($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to replace an unexpected archive path: $archive"
    }
}
$temporaryArchive = Join-Path $distRoot ("Briefcase.ServerApp-win-x64.$([guid]::NewGuid().ToString('N')).zip")
try {
    # Explicit ZIP entry names keep the Translations subfolder portable under
    # both Windows PowerShell 5.1 and PowerShell 7.
    $writeZip = [System.IO.Compression.ZipFile]::Open(
        $temporaryArchive, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in (Get-ChildItem -LiteralPath $destination -Recurse -File)) {
            $entryName = $file.FullName.Substring($destination.Length + 1).Replace([char]92, [char]47)
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $writeZip, $file.FullName, $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $writeZip.Dispose() }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($temporaryArchive)
    try {
        $actualFiles = @($zip.Entries | ForEach-Object { $_.FullName.Replace([char]92, [char]47) } | Sort-Object)
        $expectedFiles = @(
            'Briefcase.Launcher.exe', 'Launcher/manifest.json', 'UpdateSource.json', 'App/current.json',
            'Translations/en.json', 'Translations/fr.json'
        ) + $runtimeFiles + @($manifestFiles | ForEach-Object { "App/versions/$version/$($_.path)" }) +
            @("App/versions/$version/manifest.json")
        $expectedFiles = @($expectedFiles | Sort-Object)
        if (@(Compare-Object $expectedFiles $actualFiles).Count -ne 0) {
            throw "The ServerApp archive contains unexpected files. Expected: $($expectedFiles -join ', '); actual: $($actualFiles -join ', ')."
        }
    }
    finally {
        $zip.Dispose()
    }
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    Move-Item -LiteralPath $temporaryArchive -Destination $archive
}
finally {
    if (Test-Path -LiteralPath $temporaryArchive) { Remove-Item -LiteralPath $temporaryArchive -Force }
}
Write-Output "ServerApp archive ready: $archive"

