#Requires -Version 7
<#
.SYNOPSIS
    Instantiates the nimbus-adapter template in every supported option combination, checks that no
    template token survived, and builds and tests each result in Release.

.DESCRIPTION
    Run this before pushing any change under skills/nimbus-adapter/template. CI runs it too. The
    generated solutions land in .instantiate/ (git-ignored) so a failure can be inspected.

.EXAMPLE
    pwsh eng/instantiate-matrix.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputRoot = (Join-Path $PSScriptRoot '..' '.instantiate'),
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$template = (Resolve-Path (Join-Path $PSScriptRoot '..' 'skills' 'nimbus-adapter' 'template')).Path
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)

# Every combination the template supports. Keep this list in step with .template.config/template.json.
$cases = @(
    @{ Name = 'default'; Args = @('-n', 'Contoso.Erp', '--SystemName', 'BusinessCentral') }
    @{ Name = 'no-inbox'; Args = @('-n', 'Contoso.Crm', '--SystemName', 'Dataverse', '--Inbox', 'false') }
    @{ Name = 'dotted-name'; Args = @('-n', 'Fabrikam.Integration.Finance', '--SystemName', 'FinanceOperations') }
)

# Text that must never reach a generated solution: the template's own tokens.
$tokens = 'TemplateAdapter|ExternalSystem|externalSystem|externalsystem'

dotnet new install $template --force | Out-Null
try {
    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

    foreach ($case in $cases) {
        $target = Join-Path $OutputRoot $case.Name
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }

        Write-Host "== $($case.Name): dotnet new nimbus-adapter $($case.Args -join ' ')"
        dotnet new nimbus-adapter @($case.Args) -o $target | Out-Null

        $leftovers = Get-ChildItem -LiteralPath $target -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            Select-String -Pattern $tokens -CaseSensitive
        if ($leftovers) {
            $leftovers | ForEach-Object { Write-Host "   $($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
            throw "Template tokens survived instantiation in '$($case.Name)'."
        }
        $renamed = Get-ChildItem -LiteralPath $target -Recurse | Where-Object { $_.Name -cmatch $tokens }
        if ($renamed) {
            throw "Template tokens survived in file names in '$($case.Name)': $($renamed.Name -join ', ')"
        }

        $solution = Get-ChildItem -LiteralPath $target -Filter '*.slnx' | Select-Object -First 1
        dotnet build $solution.FullName -c Release --nologo -v quiet
        if (-not $SkipTests) {
            dotnet test $solution.FullName -c Release --no-build --nologo
        }
    }
}
finally {
    dotnet new uninstall $template | Out-Null
}

Write-Host "All $($cases.Count) template combinations instantiate, build and pass their tests."
