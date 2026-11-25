#Requires -Version 7
<#
.SYNOPSIS
    Checks the skill's Markdown: no stale NimBus API names, valid skill frontmatter, and relative
    links that resolve.

.EXAMPLE
    pwsh eng/check-content.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$skillRoot = Join-Path $root 'skills'
$failures = [System.Collections.Generic.List[string]]::new()

# 1. API names that do not exist (or no longer exist) in NimBus 5.x. Keep this list in step with
#    the NimBus version the template pins.
$staleApis = @(
    @{ Pattern = '\bPublishAsync\('; Reason = 'IPublisherClient has Publish(...), not PublishAsync' }
    @{ Pattern = '\bRespondAsync\('; Reason = 'request/reply uses AddRequestHandler + IRequestHandler' }
    @{ Pattern = '\bMetricsMiddleware\b'; Reason = 'there is no MetricsMiddleware; use the NimBus instrumentation' }
    @{ Pattern = '\.RetryPolicies\('; Reason = 'use ConfigureRetryPolicies(...)' }
    @{ Pattern = '\bIPermanentFailureClassifier\b'; Reason = 'replaced by IFailureDispositionClassifier' }
    @{ Pattern = '\bRuleKey<'; Reason = 'retry rules match exception types: AddExceptionRule<TException>(policy)' }
    @{ Pattern = 'AddExceptionRule\(RuleKey'; Reason = 'retry rules match exception types: AddExceptionRule<TException>(policy)' }
)
$files = Get-ChildItem -LiteralPath $skillRoot -Recurse -File -Include '*.md', '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
foreach ($stale in $staleApis) {
    $files | Select-String -Pattern $stale.Pattern | ForEach-Object {
        $failures.Add("$($_.Path):$($_.LineNumber): stale API ($($stale.Reason))")
    }
}

# 2. Skill frontmatter: only name and description; name equals the folder; description <= 1024 chars.
Get-ChildItem -LiteralPath $skillRoot -Recurse -Filter 'SKILL.md' | ForEach-Object {
    $text = Get-Content -LiteralPath $_.FullName -Raw
    if ($text -notmatch '(?s)^---\r?\n(.*?)\r?\n---') {
        $failures.Add("$($_.FullName): missing frontmatter")
        return
    }
    $frontmatter = $Matches[1]
    $keys = [regex]::Matches($frontmatter, '(?m)^([A-Za-z0-9_-]+):') | ForEach-Object { $_.Groups[1].Value }
    $extra = $keys | Where-Object { $_ -notin 'name', 'description' }
    if ($extra) { $failures.Add("$($_.FullName): frontmatter keys other than name/description: $($extra -join ', ')") }
    $name = [regex]::Match($frontmatter, '(?m)^name:\s*(.+)$').Groups[1].Value.Trim()
    if ($name -ne $_.Directory.Name) { $failures.Add("$($_.FullName): name '$name' differs from folder '$($_.Directory.Name)'") }
    $description = [regex]::Match($frontmatter, '(?m)^description:\s*(.+)$').Groups[1].Value.Trim()
    if ($description.Length -eq 0 -or $description.Length -gt 1024) {
        $failures.Add("$($_.FullName): description length $($description.Length) (must be 1-1024)")
    }
}

# 3. Relative Markdown links resolve (anchors and external links are not checked).
Get-ChildItem -LiteralPath $skillRoot -Recurse -Filter '*.md' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    ForEach-Object {
        $file = $_
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($match in [regex]::Matches($text, '\]\(([^)\s]+)\)')) {
            $target = $match.Groups[1].Value
            if ($target -match '^(https?:|mailto:|#)') { continue }
            $path = ($target -split '#')[0]
            if ([string]::IsNullOrEmpty($path)) { continue }
            $resolved = Join-Path $file.DirectoryName $path
            if (-not (Test-Path -LiteralPath $resolved)) {
                $failures.Add("$($file.FullName): broken link '$target'")
            }
        }
    }

# 4. No template C# file ends on a conditional. The .NET 11 SDK's template engine leaves the
#    closing #endif behind when a switched-off block is the last thing in a file, and the generated
#    project then fails to compile. Put the block before another type instead. A file that
#    template.json drops entirely when its condition is false is safe.
$wholeFileConditionals = @('LocalInboxTableCreator.cs')
Get-ChildItem -LiteralPath (Join-Path $skillRoot 'nimbus-adapter' 'template') -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Name -notin $wholeFileConditionals } |
    ForEach-Object {
        $last = Get-Content -LiteralPath $_.FullName | Where-Object { $_.Trim() } | Select-Object -Last 1
        if ($last -match '^\s*#endif\b') {
            $failures.Add("$($_.FullName): ends with #endif (move the conditional block before another type)")
        }
    }

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "   $_" }
    throw "$($failures.Count) content check(s) failed."
}
Write-Host 'Content checks passed: no stale API names, valid frontmatter, links resolve, no file ends on a conditional.'
