Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Get-ModRoot { return [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')) }
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


