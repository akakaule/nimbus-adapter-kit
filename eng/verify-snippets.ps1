#Requires -Version 7
<#
.SYNOPSIS
    Compiles every system pack's snippets the way they are used: dropped into a copy of the
    template's adapter project, then built in Release.

.DESCRIPTION
    Snippets live in skills/nimbus-adapter/references/systems/<system>/snippets/*.cs, in namespace
    TemplateAdapter.Adapter.Clients, and use the template's internal helpers (HttpFailure), so they
    must compile inside the adapter assembly. Each system is built separately (its snippets may
    share helper names with another system's). Output lands in .instantiate/snippets-<system>.

.EXAMPLE
    pwsh eng/verify-snippets.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputRoot = (Join-Path $PSScriptRoot '..' '.instantiate')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$template = Join-Path $root 'skills' 'nimbus-adapter' 'template'
$systems = Join-Path $root 'skills' 'nimbus-adapter' 'references' 'systems'
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)

$packs = Get-ChildItem -LiteralPath $systems -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'snippets') }
if (-not $packs) {
    throw "No system packs with snippets found under $systems."
}

foreach ($pack in $packs) {
    $snippets = Get-ChildItem -LiteralPath (Join-Path $pack.FullName 'snippets') -Filter '*.cs'
    if (-not $snippets) { continue }

    $target = Join-Path $OutputRoot "snippets-$($pack.Name)"
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }

    # Copy the template without build output, then add the snippets to the adapter's Clients folder.
    Get-ChildItem -LiteralPath $template -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object {
            $destination = Join-Path $target ([System.IO.Path]::GetRelativePath($template, $_.FullName))
            New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
    $clients = Join-Path $target 'src' 'TemplateAdapter.Adapter' 'Clients'
    $snippets | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $clients }

    Write-Host "== $($pack.Name): $($snippets.Count) snippet(s)"
    dotnet build (Join-Path $target 'TemplateAdapter.slnx') -c Release --nologo -v quiet
}

Write-Host "Every system pack's snippets compile inside the template adapter (Release)."
