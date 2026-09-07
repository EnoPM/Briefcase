[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipUnit,
    [switch]$IncludeIntegration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Checked(
    [string]$FilePath,
    [string[]]$Arguments,
    [string]$Description) {
    Write-Host "`n== $Description =="
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} `
        'Microsoft Visual Studio\Installer\vswhere.exe'
    $installation = @(& $vswhere -latest -products '*' `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath)
    if ($installation.Count -ne 1) {
        throw 'MSVC v143 x64 build tools are required for native unit tests.'
    }
    return Join-Path $installation[0] 'MSBuild\Current\Bin\amd64\MSBuild.exe'
}

try {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

    if (-not $SkipUnit) {
        $msbuild = Find-MSBuild
        $nativeProject = Join-Path $root `
            'tests\Briefcase.Native.Tests\Briefcase.Native.Tests.vcxproj'
        Invoke-Checked $msbuild @(
            $nativeProject,
            "/p:Configuration=$Configuration",
            '/p:Platform=x64',
            '/m',
            '/nologo',
            '/verbosity:minimal',
            '/nr:false') `
            'Briefcase native unit-test build'
        $nativeTests = Join-Path $root `
            "tests\Briefcase.Native.Tests\bin\$Configuration\Briefcase.Native.Tests.exe"
        Invoke-Checked $nativeTests @() 'Briefcase native unit tests'

        $artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
        $results = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'test-results'))
        $artifactPrefix = $artifactRoot.TrimEnd('\') + '\'
        if (-not $results.StartsWith(
                $artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe test-results path: $results"
        }
        if (Test-Path -LiteralPath $results) {
            Remove-Item -LiteralPath $results -Recurse -Force
        }
        New-Item -ItemType Directory -Path $results -Force | Out-Null
        Invoke-Checked 'dotnet' @(
            'test',
            (Join-Path $root 'tests\Briefcase.Core.Tests\Briefcase.Core.Tests.csproj'),
            '-c', $Configuration,
            '--nologo',
            '--logger', 'trx;LogFileName=Briefcase.Core.Tests.trx',
            '--results-directory', $results,
            '--collect', 'XPlat Code Coverage',
            '--settings', (Join-Path $root 'tests\coverage.runsettings')) `
            'Briefcase unit tests'

        $coverageFile = Get-ChildItem -LiteralPath $results `
            -Filter 'coverage.cobertura.xml' -File -Recurse |
            Select-Object -First 1
        if ($null -eq $coverageFile) {
            throw 'The unit-test run did not produce a Cobertura coverage report.'
        }
        [xml]$coverage = [IO.File]::ReadAllText($coverageFile.FullName)
        $lineRate = [double]$coverage.coverage.'line-rate'
        $minimumLineRate = 0.14
        if ($lineRate -lt $minimumLineRate) {
            throw "Line coverage $($lineRate.ToString('P2')) is below the " +
                  "$($minimumLineRate.ToString('P0')) baseline."
        }
        $coverageSummary = "[OK] Line coverage: $($lineRate.ToString('P2')) " +
            "($($coverage.coverage.'lines-covered') / " +
            "$($coverage.coverage.'lines-valid') lines)."
        Write-Host $coverageSummary
    }

    if ($IncludeIntegration) {
        Invoke-Checked 'dotnet' @(
            'run',
            '--project', (Join-Path $root 'validation\Briefcase.GameThread.Validation\Briefcase.GameThread.Validation.csproj'),
            '-c', $Configuration) `
            'Game-thread integration validation'

        $emitterValidation = Join-Path $root 'scripts\build\validate_persisted_sdk_emitter.ps1'
        Invoke-Checked 'powershell.exe' @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass',
            '-File', $emitterValidation,
            '-Configuration', $Configuration) `
            'Persisted SDK integration validation'

        $serverProps = Join-Path $root 'artifacts\server-sdk\Core\Sdk\Generated\Server\Current.props'
        if (-not (Test-Path -LiteralPath $serverProps -PathType Leaf)) {
            throw 'The server SDK is missing. Build the server framework before integration tests.'
        }
        Invoke-Checked 'dotnet' @(
            'run',
            '--project', (Join-Path $root 'validation\Briefcase.ServerAdministration.Validation\Briefcase.ServerAdministration.Validation.csproj'),
            '-c', $Configuration,
            "-p:BriefcaseGeneratedServerProps=$serverProps") `
            'Server administration integration validation'
    }

    Write-Host "`n[OK] Requested Briefcase tests passed."
    exit 0
}
catch {
    Write-Host "`n[ERROR] $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    exit 1
}
