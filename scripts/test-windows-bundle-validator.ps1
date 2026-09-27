[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceZip
)

$ErrorActionPreference = "Stop"
$validatorPath = Join-Path $PSScriptRoot "validate-windows-test-bundle.ps1"
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "SolidWorksCadAgent-BundleValidatorTest-" + [Guid]::NewGuid().ToString("N"))
$expanded = Join-Path $testRoot "expanded"
$tamperedZip = Join-Path $testRoot "tampered-native-claim.zip"

try {
    New-Item -ItemType Directory -Path $expanded -Force | Out-Null
    Expand-Archive -LiteralPath $SourceZip -DestinationPath $expanded -Force
    Set-Content -LiteralPath (Join-Path $expanded "BUILD-CAPABILITY.txt") -Value "NativeSolidWorksInterop" -Encoding ASCII
    Compress-Archive -Path (Join-Path $expanded "*") -DestinationPath $tamperedZip -CompressionLevel Optimal

    $rejected = $false
    try {
        & $validatorPath -ZipPath $tamperedZip
    }
    catch {
        $rejected = $true
        Write-Host "Validator correctly rejected a false native capability claim: $($_.Exception.Message)"
    }

    if (-not $rejected) {
        throw "Bundle validator accepted a false NativeSolidWorksInterop capability claim."
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
