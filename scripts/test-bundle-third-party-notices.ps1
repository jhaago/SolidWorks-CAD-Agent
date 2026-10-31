[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SourceZip)
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('SolidWorksCadAgent-Notices-' + [Guid]::NewGuid().ToString('N'))
$expanded = Join-Path $testRoot 'expanded'
try {
    Expand-Archive -LiteralPath $SourceZip -DestinationPath $expanded
    $files = @('THIRD-PARTY-NOTICES.md','third_party\solidworks-mcp\LICENSE','third_party\SolidworksMCP\LICENSE','third_party\reuse-manifest.json')
    foreach ($relative in $files) {
        $path = Join-Path $expanded $relative
        $source = Join-Path (Split-Path $PSScriptRoot -Parent) $relative
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing distributed notice: $relative" }
        if ((Get-FileHash -LiteralPath $path).Hash -ne (Get-FileHash -LiteralPath $source).Hash) { throw "Distributed notice differs: $relative" }
        $bytes = [IO.File]::ReadAllBytes($path)
        Remove-Item -LiteralPath $path
        $badZip = Join-Path $testRoot ('missing-' + [Guid]::NewGuid().ToString('N') + '.zip')
        Compress-Archive -Path (Join-Path $expanded '*') -DestinationPath $badZip
        $rejected = $false
        try { & (Join-Path $PSScriptRoot 'validate-windows-test-bundle.ps1') -ZipPath $badZip }
        catch { $rejected = $_.Exception.Message -like '*missing required files*' }
        if (-not $rejected) { throw "Validator accepted missing notice: $relative" }
        [IO.File]::WriteAllBytes($path,$bytes)
    }
    Write-Host 'All third-party notices are distributed unchanged; missing-notice fixtures were rejected.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
