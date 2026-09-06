[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
try {
    $root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $game=[IO.Path]::GetFullPath('D:\SteamLibrary\steamapps\common\DeceiveInc\DeceiveInc\Binaries\Win64')
    $build='6A96564B-06283000'
    $snapshotSource=Join-Path $game "Briefcase\Core\Sdk\Metadata\DeceiveInc.Client.$build.json"
    if(-not (Test-Path -LiteralPath $snapshotSource)) {
        $snapshotSource=Join-Path $root "sdk\snapshots\DeceiveInc.Client.$build.json"
    }
    if(-not (Test-Path -LiteralPath $snapshotSource)) {
        throw "Missing validation input: $snapshotSource"
    }

    $outputDirectory=Join-Path $root 'artifacts\sdk-emitter'
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    $snapshot=Join-Path $outputDirectory "DeceiveInc.Client.$build.json"
    $snapshotContents=[IO.File]::ReadAllText($snapshotSource)
    [IO.File]::WriteAllText($snapshot,$snapshotContents,[Text.UTF8Encoding]::new($false))

    $project=Join-Path $root 'managed\Briefcase.SdkEmitter\Briefcase.SdkEmitter.csproj'
    dotnet build $project -c $Configuration --nologo
    if($LASTEXITCODE -ne 0) { throw "Persisted SDK emitter build failed with exit code $LASTEXITCODE." }

    $referenceRoot=Join-Path $outputDirectory 'source-reference'
    if(Test-Path -LiteralPath $referenceRoot) {
        $resolved=[IO.Path]::GetFullPath($referenceRoot)
        $artifactRoot=[IO.Path]::GetFullPath($outputDirectory) + [IO.Path]::DirectorySeparatorChar
        if(-not $resolved.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)) {
            throw 'Unsafe source-reference cleanup path.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }

    $referenceCore=Join-Path $referenceRoot 'Core'
    New-Item -ItemType Directory -Path $referenceCore -Force | Out-Null
    $modSdk=Join-Path $root "managed\Briefcase.ModApi\bin\$Configuration\net10.0\Briefcase.ModApi.dll"
    Copy-Item -LiteralPath $modSdk -Destination (Join-Path $referenceCore 'Briefcase.ModApi.dll')

    $sourceGeneratorProject=Join-Path $root 'managed\Briefcase.SdkGenerator\Briefcase.SdkGenerator.csproj'
    dotnet build $sourceGeneratorProject -c $Configuration --nologo
    if($LASTEXITCODE -ne 0) { throw "Source SDK generator build failed with exit code $LASTEXITCODE." }
    $sourceGenerator=Join-Path $root "managed\Briefcase.SdkGenerator\bin\$Configuration\net10.0\Briefcase.SdkGenerator.dll"
    dotnet $sourceGenerator --root $referenceCore --snapshot $snapshot
    if($LASTEXITCODE -ne 0) { throw "Source SDK reference generation failed with exit code $LASTEXITCODE." }
    $reference=Join-Path $referenceCore "Sdk\Generated\Client\$build\bin\Release\net10.0\Briefcase.DeceiveInc.Client.Sdk.dll"
    if(-not (Test-Path -LiteralPath $reference)) { throw "Missing generated source reference: $reference" }

    $prototype=Join-Path $outputDirectory 'Briefcase.DeceiveInc.Client.Sdk.EmitPrototype.dll'
    $emitter=Join-Path $root "managed\Briefcase.SdkEmitter\bin\$Configuration\net10.0\Briefcase.SdkEmitter.dll"
    dotnet $emitter $snapshot $reference $prototype
    if($LASTEXITCODE -ne 0) { throw "Persisted SDK emitter validation failed with exit code $LASTEXITCODE." }

    $productionCore=Join-Path $outputDirectory 'production-core'
    if(Test-Path -LiteralPath $productionCore) {
        $resolved=[IO.Path]::GetFullPath($productionCore)
        $artifactRoot=[IO.Path]::GetFullPath($outputDirectory) + [IO.Path]::DirectorySeparatorChar
        if(-not $resolved.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)) {
            throw 'Unsafe production-root cleanup path.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    dotnet $emitter production $snapshot $productionCore
    if($LASTEXITCODE -ne 0) { throw "Persisted SDK production validation failed with exit code $LASTEXITCODE." }

    $consumer=Join-Path $root 'validation\Briefcase.SdkEmitter.ConsumerValidation\Briefcase.SdkEmitter.ConsumerValidation.csproj'
    dotnet build $consumer -c $Configuration --nologo "-p:EmittedSdkPath=$prototype"
    if($LASTEXITCODE -ne 0) { throw "Persisted SDK consumer validation failed with exit code $LASTEXITCODE." }
    Write-Host '[OK] A typed C# consumer compiled against the persisted SDK DLL.'
    exit 0
} catch { Write-Host "[ERROR] $($_.Exception.Message)"; exit 1 }
