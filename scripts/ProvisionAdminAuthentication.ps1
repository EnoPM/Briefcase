[CmdletBinding()]
param(
    [string]$ServerConfigurationPath='',
    [string]$ClientSettingsPath='',
    [string]$ServerWin64='',
    [string]$ClientGameWin64='')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')

function Get-OrAddProperty(
    [object]$Object,
    [string]$Name,
    [object]$DefaultValue) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $DefaultValue
    return $DefaultValue
}

function Write-AtomicUtf8([string]$Path, [string]$Contents) {
    $directory = [IO.Path]::GetDirectoryName($Path)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporary = $Path + '.tmp'
    [IO.File]::WriteAllText($temporary, $Contents, [Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($Path)) {
        $replacementBackup = $Path + '.replace.bak'
        [IO.File]::Replace($temporary, $Path, $replacementBackup)
        [IO.File]::Delete($replacementBackup)
    } else {
        [IO.File]::Move($temporary, $Path)
    }
}

try {
    if([string]::IsNullOrWhiteSpace($ServerConfigurationPath)) {
        $server=Resolve-BriefcaseLocalPath `
            -Value $ServerWin64 `
            -EnvironmentVariable 'BRIEFCASE_SERVER_GAME_DIR' `
            -LocalSetting 'BriefcaseServerGameWin64' `
            -CommandLineHint '-ServerWin64' `
            -Description 'server Win64'
        $ServerConfigurationPath=Join-Path $server '..\..\Saved\Config\WindowsServer\TripwireServer.ini'
    }
    if([string]::IsNullOrWhiteSpace($ClientSettingsPath)) {
        $client=Resolve-BriefcaseLocalPath `
            -Value $ClientGameWin64 `
            -EnvironmentVariable 'BRIEFCASE_CLIENT_GAME_DIR' `
            -LocalSetting 'BriefcaseClientGameWin64' `
            -CommandLineHint '-ClientGameWin64' `
            -Description 'client Win64'
        $ClientSettingsPath=Join-Path $client 'Briefcase\settings.json'
    }
    $ServerConfigurationPath=[IO.Path]::GetFullPath($ServerConfigurationPath)
    $ClientSettingsPath=[IO.Path]::GetFullPath($ClientSettingsPath)

    if (-not (Test-Path -LiteralPath $ServerConfigurationPath)) {
        throw "Server configuration was not found: $ServerConfigurationPath"
    }
    if (-not (Test-Path -LiteralPath $ClientSettingsPath)) {
        throw "Client Briefcase settings were not found: $ClientSettingsPath"
    }

    $secretBytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($secretBytes) }
    finally { $generator.Dispose() }
    $secret = [Convert]::ToBase64String($secretBytes).TrimEnd('=').
        Replace('+', '-').Replace('/', '_')

    $serverText = [IO.File]::ReadAllText($ServerConfigurationPath)
    $passwordLine = [regex]::new('^AdminPassword=.*$',
        [Text.RegularExpressions.RegexOptions]::Multiline)
    if ($passwordLine.IsMatch($serverText)) {
        $serverText = $passwordLine.Replace(
            $serverText,
            [Text.RegularExpressions.MatchEvaluator]{
                param($match)
                return 'AdminPassword=' + $secret
            },
            1)
    }
    else {
        $sectionName = '[/Script/DeceiveInc.TripwireServerSettings]'
        $section = [regex]::new(
            '^' + [regex]::Escape($sectionName) + '\s*$',
            [Text.RegularExpressions.RegexOptions]::Multiline)
        if (-not $section.IsMatch($serverText)) {
            throw "The Deceive Inc. server settings section was not found."
        }
        $serverText = $section.Replace(
            $serverText,
            [Text.RegularExpressions.MatchEvaluator]{
                param($match)
                return $match.Value + [Environment]::NewLine +
                    'AdminPassword=' + $secret
            },
            1)
    }

    Copy-Item -LiteralPath $ServerConfigurationPath `
        -Destination ($ServerConfigurationPath + '.briefcase-auth.bak') -Force
    Write-AtomicUtf8 $ServerConfigurationPath $serverText

    $settingsText = [IO.File]::ReadAllText($ClientSettingsPath)
    $settings = if ([string]::IsNullOrWhiteSpace($settingsText)) {
        [pscustomobject]@{}
    } else {
        $settingsText | ConvertFrom-Json
    }
    $mods = Get-OrAddProperty $settings 'Mods' ([pscustomobject]@{})
    $client = Get-OrAddProperty $mods 'server-admin-control.client' ([pscustomobject]@{})
    $values = Get-OrAddProperty $client 'Values' ([pscustomobject]@{})
    $settingName = 'Briefcase server/Administration password'
    $setting = $values.PSObject.Properties[$settingName]
    if ($null -eq $setting) {
        $values | Add-Member -MemberType NoteProperty -Name $settingName -Value $secret
    } else {
        $setting.Value = $secret
    }
    $clientText = $settings | ConvertTo-Json -Depth 100
    Copy-Item -LiteralPath $ClientSettingsPath `
        -Destination ($ClientSettingsPath + '.briefcase-auth.bak') -Force
    Write-AtomicUtf8 $ClientSettingsPath $clientText

    [Array]::Clear($secretBytes, 0, $secretBytes.Length)
    $secret = $null
    Write-Host '[OK] Generated a 256-bit administration secret.'
    Write-Host "[OK] Server AdminPassword updated: $ServerConfigurationPath"
    Write-Host "[OK] Client administrator credential updated: $ClientSettingsPath"
    Write-Host '[INFO] Restart both processes before testing authentication.'
    exit 0
}
catch {
    Write-Host "[ERROR] $($_.Exception.Message)"
    exit 1
}
