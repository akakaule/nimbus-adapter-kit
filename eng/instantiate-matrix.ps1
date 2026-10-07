#Requires -Version 7
<#
.SYNOPSIS
    Instantiates the nimbus-adapter template in every supported option combination, checks that no
    template token or conditional survived, and builds and tests each result in Release.

.DESCRIPTION
    Run this before pushing any change under skills/nimbus-adapter/template. CI runs it too. The
    generated solutions land in .instantiate/ (git-ignored) so a failure can be inspected.

    The package-mode case needs a catalog package: the script packs the Contracts project of the
    'default' instance into a local feed (.instantiate/feed) under a unique version, then generates
    a package-mode adapter against it.

.EXAMPLE
    pwsh eng/instantiate-matrix.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputRoot = (Join-Path $PSScriptRoot '..' '.instantiate'),
    [switch] $SkipTests,
    # Skip the Bicep compile (needs the Azure CLI with Bicep).
    [switch] $SkipBicep
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
    @{ Name = 'ingress-custom-endpoint'; Args = @('-n', 'Contoso.Bc', '--SystemName', 'BusinessCentral', '--Ingress', 'true', '--Endpoint', 'ErpEndpoint') }
)

# Text that must never reach a generated solution: the template's own tokens and placeholders.
$tokens = 'TemplateAdapter|ExternalSystem|externalSystem|externalsystem|CONTRACTS_PACKAGE_ID|CONTRACTS_PACKAGE_VERSION|PLATFORM_TYPE|CONTRACTS_FEED'

function Test-Instance([string] $Name, [string[]] $Arguments) {
    $target = Join-Path $OutputRoot $Name
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }

    Write-Host "== ${Name}: dotnet new nimbus-adapter $($Arguments -join ' ')"
    dotnet new nimbus-adapter @Arguments -o $target | Out-Null

    $files = Get-ChildItem -LiteralPath $target -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    $leftovers = $files | Select-String -Pattern $tokens -CaseSensitive
    if ($leftovers) {
        $leftovers | ForEach-Object { Write-Host "   $($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
        throw "Template tokens survived instantiation in '$Name'."
    }
    # Template conditionals must be resolved, never copied: a surviving marker means a file type
    # the engine did not process (for example a Bicep file with CRLF line endings).
    $markers = $files | Select-String -Pattern '^\s*(//#|<!--#|#if \(|#endif\b|#else\b)' -CaseSensitive
    if ($markers) {
        $markers | ForEach-Object { Write-Host "   $($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
        throw "Template conditional markers survived instantiation in '$Name'."
    }
    $renamed = Get-ChildItem -LiteralPath $target -Recurse | Where-Object { $_.Name -cmatch $tokens }
    if ($renamed) {
        throw "Template tokens survived in file names in '$Name': $($renamed.Name -join ', ')"
    }

    $solutions = @(Get-ChildItem -LiteralPath $target -Filter '*.slnx')
    if ($solutions.Count -ne 1) {
        throw "Expected one solution in '$Name', found: $($solutions.Name -join ', ')"
    }
    dotnet build $solutions[0].FullName -c Release --nologo -v quiet | Out-Host
    if (-not $SkipTests) {
        dotnet test $solutions[0].FullName -c Release --no-build --nologo | Out-Host
    }

    # The generated Bicep and every parameter file must compile (conditionals included).
    if (-not $SkipBicep) {
        az bicep build --file (Join-Path $target 'deploy' 'main.bicep') --stdout | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $target 'deploy') -Filter '*.bicepparam' | ForEach-Object {
            az bicep build-params --file $_.FullName --stdout | Out-Null
        }
    }

    return $target
}

dotnet new install $template --force | Out-Null
try {
    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

    $instances = @{}
    foreach ($case in $cases) {
        $instances[$case.Name] = Test-Instance $case.Name $case.Args
    }

    # Package mode: a catalog package built from the default instance's Contracts project.
    $feed = Join-Path $OutputRoot 'feed'
    New-Item -ItemType Directory -Force -Path $feed | Out-Null
    $version = '1.0.0-local.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
    $contracts = Join-Path $instances['default'] 'src' 'Contoso.Erp.Contracts'
    Write-Host "== packing Contoso.Erp.Contracts $version into $feed"
    dotnet pack $contracts -c Release -o $feed --nologo -v quiet -p:PackageId=Contoso.Erp.Contracts -p:Version=$version | Out-Null

    Test-Instance 'package' @(
        '-n', 'Fabrikam.BcAdapter', '--SystemName', 'BusinessCentral',
        '--Contracts', 'package',
        '--ContractsPackageId', 'Contoso.Erp.Contracts',
        '--ContractsPackageVersion', $version,
        '--PlatformType', 'Contoso.Erp.Contracts.AdapterCatalog',
        '--ContractsFeed', $feed) | Out-Null
}
finally {
    dotnet new uninstall $template | Out-Null
}

Write-Host "All $($cases.Count + 1) template combinations instantiate, build and pass their tests."
