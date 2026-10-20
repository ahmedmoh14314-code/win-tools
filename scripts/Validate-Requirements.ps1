<#
.SYNOPSIS
  Validates docs/product/requirements.json against the ledger rules.
.DESCRIPTION
  Checks: unique stable IDs, allowed statuses, rationale on rejected/deferred,
  evidence on verified, blockedBy on blocked, non-empty acceptance criteria for
  active items, and ADR references that resolve to files in docs/adr.
  Exits 1 on any violation. Repository tooling only; not product code.
#>
[CmdletBinding()]
param(
    [string]$Path = (Join-Path $PSScriptRoot '..\docs\product\requirements.json'),
    [string]$AdrDir = (Join-Path $PSScriptRoot '..\docs\adr')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ledger = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
$allowed = @('unverified', 'in-progress', 'blocked', 'verified', 'rejected', 'deferred')
$errors = New-Object System.Collections.Generic.List[string]
$seen = @{}

foreach ($r in $ledger.requirements) {
    $id = $r.id
    if ($id -notmatch '^[A-Z]+-\d{3}$') { $errors.Add("${id}: malformed id") }
    if ($seen.ContainsKey($id)) { $errors.Add("${id}: duplicate id") } else { $seen[$id] = $true }
    if ($allowed -notcontains $r.status) { $errors.Add("${id}: invalid status '$($r.status)'") }

    $has = { param($name) ($r.PSObject.Properties.Name -contains $name) -and -not [string]::IsNullOrWhiteSpace($r.$name) }

    switch ($r.status) {
        'rejected' { if (-not (& $has 'rationale')) { $errors.Add("${id}: rejected without rationale") } }
        'deferred' { if (-not (& $has 'rationale')) { $errors.Add("${id}: deferred without rationale") } }
        'verified' { if (-not (& $has 'evidence')) { $errors.Add("${id}: verified without evidence") } }
        'blocked'  { if (-not (& $has 'blockedBy')) { $errors.Add("${id}: blocked without blockedBy") } }
    }

    if ($r.status -in @('unverified', 'in-progress', 'blocked', 'verified') -and @($r.acceptanceCriteria).Count -eq 0) {
        $errors.Add("${id}: active requirement has no acceptance criteria")
    }

    if ($r.PSObject.Properties.Name -contains 'relatedAdrs') {
        foreach ($adr in $r.relatedAdrs) {
            $match = Get-ChildItem -LiteralPath $AdrDir -Filter "$adr*.md" -ErrorAction SilentlyContinue
            if (-not $match) { $errors.Add("${id}: references $adr but no file $adr-*.md exists in docs/adr") }
        }
    }
}

$counts = $ledger.requirements | Group-Object status | ForEach-Object { "$($_.Name)=$($_.Count)" }
Write-Output ("Requirements: {0} ({1})" -f @($ledger.requirements).Count, ($counts -join ', '))

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Output "ERROR: $_" }
    exit 1
}
Write-Output 'Ledger OK'
