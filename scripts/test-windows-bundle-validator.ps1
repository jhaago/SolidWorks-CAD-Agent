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
    foreach ($required in @("RemoteAgent\SolidWorksCadAgent.RemoteAgent.exe", "RemoteAgent\Newtonsoft.Json.dll", "Setup\register-remote-agent-url.ps1", "LIVE-REMOTE.txt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $expanded $required))) { throw "Live remote bundle is missing $required" }
    }
    $remoteScript = Get-Content -LiteralPath (Join-Path $expanded "Setup\register-remote-agent-url.ps1") -Raw
    if ($remoteScript -notmatch 'http://127\.0\.0\.1:5079/' -or $remoteScript -match 'http://[+*]') { throw "Remote setup must reserve the fixed loopback prefix only." }
    if (@(Get-ChildItem $expanded -Recurse -File | Where-Object { $_.Name -match '^(devices\.dat|remote-workstation-.*\.enc)$' }).Count -gt 0) { throw "Bundle must not include device credentials." }
    # The validator must reject a package with its remote executable removed.
    $remoteExe = Join-Path $expanded "RemoteAgent\SolidWorksCadAgent.RemoteAgent.exe"
    $remoteBytes = [System.IO.File]::ReadAllBytes($remoteExe)
    Remove-Item -LiteralPath $remoteExe
    $missingZip = Join-Path $testRoot "missing-remote-agent.zip"
    Compress-Archive -Path (Join-Path $expanded "*") -DestinationPath $missingZip
    $missingRejected = $false
    try { & $validatorPath -ZipPath $missingZip } catch { $missingRejected = $_.Exception.Message -like '*RemoteAgent*' }
    if (-not $missingRejected) { throw "Validator accepted a bundle without RemoteAgent." }
    [System.IO.File]::WriteAllBytes($remoteExe, $remoteBytes)
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
