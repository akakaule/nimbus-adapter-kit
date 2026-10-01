#Requires -Version 7
<#
.SYNOPSIS
    Fails when a tracked file (or a generated template instance) contains a term from the
    anonymization denylist.

.DESCRIPTION
    The kit is shared with many customers and must not name any of them. The denylist itself is
    sensitive, so it is never committed: CI reads it from the ANONYMIZATION_DENYLIST secret, and
    locally you pass a file. One term per line; lines starting with # are comments. A term is
    matched case-insensitively as a whole word unless it starts with ~ (then as a substring).

.EXAMPLE
    pwsh eng/check-anonymization.ps1 -DenylistFile $HOME/.nimbus-kit-denylist.txt
#>
[CmdletBinding()]
param(
    [string] $DenylistFile,
    # Extra folders to scan besides the tracked files (e.g. the instantiated template output).
    [string[]] $ExtraPaths = @()
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$raw = if ($DenylistFile) { Get-Content -LiteralPath $DenylistFile -Raw } else { $env:ANONYMIZATION_DENYLIST }
if ([string]::IsNullOrWhiteSpace($raw)) {
    throw 'No denylist: pass -DenylistFile or set ANONYMIZATION_DENYLIST.'
}
$terms = $raw -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') }
$patterns = $terms | ForEach-Object {
    if ($_.StartsWith('~')) { [regex]::Escape($_.Substring(1)) } else { '(?<![A-Za-z0-9])' + [regex]::Escape($_) + '(?![A-Za-z0-9])' }
}
$regex = [regex]::new(($patterns -join '|'), 'IgnoreCase')

Push-Location $root
try {
    $files = @(git ls-files) | ForEach-Object { Join-Path $root $_ }
}
finally {
    Pop-Location
}
foreach ($extra in $ExtraPaths) {
    if (Test-Path -LiteralPath $extra) {
        $files += Get-ChildItem -LiteralPath $extra -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object { $_.FullName }
    }
}

$hits = 0
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file)) { continue }
    $lineNumber = 0
    foreach ($line in [System.IO.File]::ReadLines($file)) {
        $lineNumber++
        if ($regex.IsMatch($line)) {
            $hits++
            # Print the location only: echoing the matched term would leak it into CI logs.
            Write-Host "   $([System.IO.Path]::GetRelativePath($root, $file)):$lineNumber"
        }
    }
}

if ($hits -gt 0) {
    throw "$hits line(s) contain a denylisted term."
}
Write-Host "Anonymization check passed ($($files.Count) files, $($terms.Count) terms)."
