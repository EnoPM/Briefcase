Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Get-ModRoot { return [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')) }

function Get-BriefcaseLocalValue([string]$Name) {
    $settingsPath=Join-Path (Get-ModRoot) 'scripts\LocalPaths.ps1'
    if(-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) { return $null }
    return & {
        param([string]$Path,[string]$VariableName)
        . $Path
        $variable=Get-Variable -Name $VariableName -ErrorAction SilentlyContinue
        if($null -ne $variable) { return $variable.Value }
        return $null
    } $settingsPath $Name
}

function Resolve-BriefcaseLocalPath {
    param(
        [AllowEmptyString()][string]$Value='',
        [Parameter(Mandatory=$true)][string]$EnvironmentVariable,
        [Parameter(Mandatory=$true)][string]$LocalSetting,
        [Parameter(Mandatory=$true)][string]$CommandLineHint,
        [Parameter(Mandatory=$true)][string]$Description,
        [switch]$Optional)

    $candidate=$Value
    if([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate=[Environment]::GetEnvironmentVariable($EnvironmentVariable)
    }
    if([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate=Get-BriefcaseLocalValue $LocalSetting
    }
    if([string]::IsNullOrWhiteSpace($candidate)) {
        if($Optional) { return $null }
        throw "No $Description path is configured. Pass $CommandLineHint, set $EnvironmentVariable, or copy scripts\LocalPaths.example.ps1 to scripts\LocalPaths.ps1."
    }
    return [IO.Path]::GetFullPath($candidate)
}
function ConvertTo-ModArgument([string]$Value) {
    # ProcessStartInfo launches an executable directly, without cmd.exe.
    # Our arguments cannot contain quotes; double trailing backslashes before
    # the closing quote, including the slash at the end of SolutionDir.
    if ($null -eq $Value) { throw 'A native process argument cannot be null.' }
    if ($Value.Contains('"')) { throw 'Unexpected quote in a native argument.' }
    return '"' + [regex]::Replace($Value, '(\\+)$', '$1$1') + '"'
}

function Invoke-ModNative {
    param([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory)
    if ([string]::IsNullOrWhiteSpace($FilePath)) { throw 'A native executable path is required.' }
    Push-Location -LiteralPath $WorkingDirectory
    try {
        # Array splatting lets PowerShell perform Windows command-line quoting.
        # This works in both Windows PowerShell 5.1 and modern PowerShell.
        $env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
        $env:VSLANG = '1033'
        & $FilePath @Arguments | Out-Host
        return $LASTEXITCODE
    }
    finally { Pop-Location }
}
function Organize-BriefcaseFrameworkPackage {
    param([Parameter(Mandatory=$true)][string]$FrameworkDirectory)

    $framework=[IO.Path]::GetFullPath($FrameworkDirectory)
    $core=[IO.Path]::GetFullPath((Join-Path $framework 'Core'))
    if(-not (Test-Path -LiteralPath $core -PathType Container)) {
        throw "Briefcase Core directory is missing: $core"
    }

    $thirdParty=[IO.Path]::GetFullPath((Join-Path $core 'ThirdPartyLibraries'))
    New-Item -ItemType Directory -Path $thirdParty -Force | Out-Null
    $dotNetPrefix=[IO.Path]::GetFullPath((Join-Path $core 'DotNet')).TrimEnd('\') + '\'
    $thirdPartyPrefix=$thirdParty.TrimEnd('\') + '\'

    $libraries=@(Get-ChildItem -LiteralPath $core -Filter '*.dll' -File -Recurse | Where-Object {
        $full=[IO.Path]::GetFullPath($_.FullName)
        -not $full.StartsWith($dotNetPrefix,[StringComparison]::OrdinalIgnoreCase) -and
        -not $full.StartsWith($thirdPartyPrefix,[StringComparison]::OrdinalIgnoreCase) -and
        $_.Name -notmatch '^(Briefcase\.|ServerAdminControl\.)'
    })
    foreach($library in $libraries) {
        $destination=Join-Path $thirdParty $library.Name
        if(Test-Path -LiteralPath $destination) {
            $sourceHash=(Get-FileHash -LiteralPath $library.FullName -Algorithm SHA256).Hash
            $destinationHash=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            if($sourceHash -ne $destinationHash) {
                throw "Third-party library name collision: $($library.Name)"
            }
            Remove-Item -LiteralPath $library.FullName -Force
        }
        else {
            Move-Item -LiteralPath $library.FullName -Destination $destination
        }
    }

    # Installed packages never contain symbols. Repository build directories
    # retain their PDB files for local debugging.
    Get-ChildItem -LiteralPath $framework -Filter '*.pdb' -File -Recurse |
        Remove-Item -Force

    # Moving native runtime assets can leave empty NuGet runtime directories.
    Get-ChildItem -LiteralPath $core -Directory -Recurse |
        Sort-Object { $_.FullName.Length } -Descending |
        Where-Object {
            $_.FullName -ne $thirdParty -and
            @(Get-ChildItem -LiteralPath $_.FullName -Force).Count -eq 0
        } |
        Remove-Item -Force
}