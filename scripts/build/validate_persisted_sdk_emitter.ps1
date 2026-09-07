[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'

function Write-SnapshotCopy(
    [string]$Root,
    [hashtable]$Spec,
    [string]$Destination) {
    $fileName = "DeceiveInc.$($Spec.Target).$($Spec.Build).json"
    $installed = Join-Path $Spec.GameRoot "Briefcase\Core\Sdk\Metadata\$fileName"
    $repositoryJson = Join-Path $Root "sdk\snapshots\$fileName"
    $repositoryArchive = "$repositoryJson.zip"

    if (Test-Path -LiteralPath $installed -PathType Leaf) {
        $contents = [IO.File]::ReadAllText($installed)
        [IO.File]::WriteAllText($Destination, $contents, [Text.UTF8Encoding]::new($false))
        return
    }
    if (Test-Path -LiteralPath $repositoryJson -PathType Leaf) {
        $contents = [IO.File]::ReadAllText($repositoryJson)
        [IO.File]::WriteAllText($Destination, $contents, [Text.UTF8Encoding]::new($false))
        return
    }
    if (-not (Test-Path -LiteralPath $repositoryArchive -PathType Leaf)) {
        throw "Missing validation input for $($Spec.Target): $repositoryJson or $repositoryArchive"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($repositoryArchive)
    try {
        $entry = $archive.GetEntry($fileName)
        if ($null -eq $entry) {
            throw "Archive $repositoryArchive does not contain $fileName."
        }
        $input = $entry.Open()
        try {
            $output = [IO.File]::Create($Destination)
            try { $input.CopyTo($output) }
            finally { $output.Dispose() }
        }
        finally { $input.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Remove-SafeArtifactDirectory(
    [string]$Root,
    [string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $Root 'artifacts')) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe artifact cleanup path: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

try {
    $root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $outputRoot=Join-Path $root 'artifacts\sdk-emitter'
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

    $project=Join-Path $root 'managed\Briefcase.SdkEmitter\Briefcase.SdkEmitter.csproj'
    dotnet build $project -c $Configuration --nologo
    if($LASTEXITCODE -ne 0) { throw "Persisted SDK emitter build failed with exit code $LASTEXITCODE." }

    $sourceGeneratorProject=Join-Path $root 'managed\Briefcase.SdkGenerator\Briefcase.SdkGenerator.csproj'
    dotnet build $sourceGeneratorProject -c $Configuration --nologo
    if($LASTEXITCODE -ne 0) { throw "Source SDK generator build failed with exit code $LASTEXITCODE." }

    $modSdk=Join-Path $root "managed\Briefcase.ModApi\bin\$Configuration\net10.0\Briefcase.ModApi.dll"
    $sourceGenerator=Join-Path $root "managed\Briefcase.SdkGenerator\bin\$Configuration\net10.0\Briefcase.SdkGenerator.dll"
    $emitter=Join-Path $root "managed\Briefcase.SdkEmitter\bin\$Configuration\net10.0\Briefcase.SdkEmitter.dll"
    $specs = @(
        @{
            Target='Client'
            Build='6A96564B-06283000'
            GameRoot='D:\SteamLibrary\steamapps\common\DeceiveInc\DeceiveInc\Binaries\Win64'
        },
        @{
            Target='Server'
            Build='6A966107-05B60000'
            GameRoot='D:\GameServers\steamcmd\steamapps\common\Deceive Inc. Dedicated Server\DeceiveInc\Binaries\Win64'
        }
    )

    $clientPrototype = $null
    foreach ($spec in $specs) {
        $target = $spec.Target
        $build = $spec.Build
        $targetRoot = Join-Path $outputRoot $target.ToLowerInvariant()
        Remove-SafeArtifactDirectory $root $targetRoot
        New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null

        $snapshot=Join-Path $targetRoot "DeceiveInc.$target.$build.json"
        Write-SnapshotCopy $root $spec $snapshot

        $referenceRoot=Join-Path $targetRoot 'source-reference'
        $referenceCore=Join-Path $referenceRoot 'Core'
        New-Item -ItemType Directory -Path $referenceCore -Force | Out-Null
        Copy-Item -LiteralPath $modSdk -Destination (Join-Path $referenceCore 'Briefcase.ModApi.dll')

        dotnet $sourceGenerator --root $referenceCore --snapshot $snapshot
        if($LASTEXITCODE -ne 0) {
            throw "$target source SDK reference generation failed with exit code $LASTEXITCODE."
        }
        $reference=Join-Path $referenceCore "Sdk\Generated\$target\$build\bin\Release\net10.0\Briefcase.DeceiveInc.$target.Sdk.dll"
        if(-not (Test-Path -LiteralPath $reference)) {
            throw "Missing generated $target source reference: $reference"
        }

        $prototype=Join-Path $targetRoot "Briefcase.DeceiveInc.$target.Sdk.EmitPrototype.dll"
        dotnet $emitter $snapshot $reference $prototype
        if($LASTEXITCODE -ne 0) {
            throw "$target persisted SDK validation failed with exit code $LASTEXITCODE."
        }

        $productionCore=Join-Path $targetRoot 'production-core'
        dotnet $emitter production $snapshot $productionCore
        if($LASTEXITCODE -ne 0) {
            throw "$target persisted SDK production validation failed with exit code $LASTEXITCODE."
        }
        if ($target -eq 'Client') { $clientPrototype = $prototype }
    }

    # The packaged snapshots remain schema 2 compatibility fixtures. This tiny
    # schema 3 sample covers recursive types, enums, bool masks, and C# name collisions.
    $schema3Root = Join-Path $outputRoot 'schema3-smoke'
    Remove-SafeArtifactDirectory $root $schema3Root
    New-Item -ItemType Directory -Path $schema3Root -Force | Out-Null
    $schema3Snapshot = Join-Path $schema3Root 'DeceiveInc.Client.00000001-00000002.json'
    $schema3Source = Join-Path $root 'tests\fixtures\sdk\Schema3Smoke.json'
    Copy-Item -LiteralPath $schema3Source -Destination $schema3Snapshot
    $schema3Core = Join-Path $schema3Root 'source-reference\Core'
    New-Item -ItemType Directory -Path $schema3Core -Force | Out-Null
    Copy-Item -LiteralPath $modSdk -Destination (Join-Path $schema3Core 'Briefcase.ModApi.dll')
    dotnet $sourceGenerator --root $schema3Core --snapshot $schema3Snapshot
    if($LASTEXITCODE -ne 0) { throw "Schema 3 source SDK generation failed with exit code $LASTEXITCODE." }
    $schema3Reference = Join-Path $schema3Core 'Sdk\Generated\Client\00000001-00000002\bin\Release\net10.0\Briefcase.DeceiveInc.Client.Sdk.dll'
    $schema3Prototype = Join-Path $schema3Root 'Briefcase.DeceiveInc.Client.Sdk.EmitPrototype.dll'
    dotnet $emitter $schema3Snapshot $schema3Reference $schema3Prototype
    if($LASTEXITCODE -ne 0) { throw "Schema 3 persisted SDK comparison failed with exit code $LASTEXITCODE." }
    Write-Host '[OK] Schema 3 enum, recursive type, bool, collision, and parameter metadata validated.'

    $consumer=Join-Path $root 'validation\Briefcase.SdkEmitter.ConsumerValidation\Briefcase.SdkEmitter.ConsumerValidation.csproj'
    dotnet build $consumer -c $Configuration --nologo "-p:EmittedSdkPath=$clientPrototype"
    if($LASTEXITCODE -ne 0) { throw "Persisted SDK consumer validation failed with exit code $LASTEXITCODE." }
    Write-Host '[OK] A typed C# consumer compiled against the persisted client SDK DLL.'
    exit 0
} catch {
    Write-Host "[ERROR] $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    exit 1
}
